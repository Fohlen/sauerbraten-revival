using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Network;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// The single entry point for world mutation (docs/editor-and-commands.md §2).
/// Nothing mutates the octree directly; every edit, game-mode change and undo goes
/// through <see cref="Submit"/>. It captures the pre-state needed for undo, applies
/// the command deterministically, and records it in the log.
///
/// Undo/redo follow §5.5: only this peer's commands are undoable, and the inverse
/// is derived from a pre-state capture (Rule 4). This is the offline (M3) form;
/// networking wraps it with request/broadcast and conflict rejection (M6).
/// </summary>
public sealed class CommandSink
{
    private readonly OctaWorld _world;
    private readonly CommandLog _log;

    /// <summary>Ops whose forward application is destructive and needs a pre-state capture to undo.</summary>
    private static readonly HashSet<WorldOp> Destructive = new()
    {
        WorldOp.DeleteCube,
        WorldOp.EditFace,
        WorldOp.SetFaceTexture,
        WorldOp.ReplaceTexture,
        WorldOp.SetFaceMaterial,
        WorldOp.FlipCube,
        WorldOp.RotateCube,
        WorldOp.ReplaceBlock,
        WorldOp.AddEntity,
        WorldOp.RemoveEntity,
        WorldOp.EditEntity,
    };

    public OctaWorld World => _world;
    public CommandLog Log => _log;

    public CommandSink(OctaWorld world, int localPeerId = 0)
    {
        _world = world;
        _log = new CommandLog(localPeerId);
    }

    /// <summary>True if a given op needs a captured pre-state for undo.</summary>
    public static bool IsDestructive(WorldOp op) => Destructive.Contains(op);

    /// <summary>
    /// Submit a locally-issued command: capture undo state, apply, record. Returns
    /// the log entry.
    /// </summary>
    public LogEntry Submit(Command cmd)
    {
        var entry = PrepareUndo(cmd);
        _world.Apply(cmd);
        var recorded = _log.Record(cmd);
        recorded.UndoBlock = entry.UndoBlock;
        recorded.UndoRegion = entry.UndoRegion;
        recorded.RedoBlock = entry.RedoBlock;
        recorded.UndoEntity = entry.UndoEntity;
        recorded.RedoEntity = entry.RedoEntity;
        return recorded;
    }

    /// <summary>Apply and record a command that originated on another peer (no undo entry for us).</summary>
    public void Receive(Command cmd)
    {
        _world.Apply(cmd);
        _log.RecordRemote(cmd);
    }

    /// <summary>
    /// Undo the most recent local command. Returns the undone entry, or null if
    /// there is nothing to undo. Reverts by restoring the captured pre-state.
    /// </summary>
    public LogEntry? Undo()
    {
        var entry = _log.PeekUndo();
        if (entry == null) return null;

        if (entry.UndoBlock != null)
        {
            // Restore the captured region (Rule 4: the pre-state was captured when
            // the forward command was applied).
            var restore = Command.CreateReplaceBlock(
                entry.UndoRegion.ToSelection(),
                NetMessage.SerializeBlock(entry.UndoBlock),
                _log.LocalPeerId);
            _world.Apply(restore);
        }
        else if (entry.Command.Op == WorldOp.AddEntity && entry.Command.Payload.HasValue)
        {
            var p = entry.Command.Payload.Value.Item8;
            var revert = Command.CreateRemoveEntity(p.EntityId, _log.LocalPeerId);
            _world.Apply(revert);
        }
        else if (entry.Command.Op == WorldOp.RemoveEntity && entry.UndoEntity != null)
        {
            var restore = Command.CreateAddEntity(entry.UndoEntity, _log.LocalPeerId);
            _world.Apply(restore);
        }
        else if (entry.Command.Op == WorldOp.EditEntity && entry.UndoEntity != null)
        {
            var restore = Command.CreateEditEntity(entry.UndoEntity, _log.LocalPeerId);
            _world.Apply(restore);
        }
        else if (entry.Command.Op == WorldOp.Remip)
        {
            // Derivation-only op: no pre-state to restore. Not undoable (a forward
            // remip is lossy); discard rather than corrupt. See §5.4/§5.5.
        }

        return _log.PopForUndo();
    }

    /// <summary>Redo the most recently undone command, re-applying it and recording it again.</summary>
    public LogEntry? Redo()
    {
        var entry = _log.PopForRedo();
        if (entry == null) return null;

        // Re-capture the pre-state so the redone command is undoable again, then
        // re-apply. For ReplaceBlock we re-apply the exact written block; otherwise
        // re-apply the original command (deterministic given the undone state).
        var fresh = PrepareUndo(entry.Command);
        if (entry.Command.Op == WorldOp.ReplaceBlock && entry.RedoBlock != null)
        {
            var again = Command.CreateReplaceBlock(
                entry.UndoRegion.ToSelection(),
                NetMessage.SerializeBlock(entry.RedoBlock),
                _log.LocalPeerId);
            _world.Apply(again);
        }
        else
        {
            _world.Apply(entry.Command);
        }

        var recorded = _log.Record(entry.Command);
        recorded.UndoBlock = fresh.UndoBlock;
        recorded.UndoRegion = fresh.UndoRegion;
        recorded.RedoBlock = fresh.RedoBlock;
        recorded.UndoEntity = fresh.UndoEntity;
        recorded.RedoEntity = fresh.RedoEntity;
        return recorded;
    }

    /// <summary>
    /// Build the undo state for a command before it is applied. Destructive ops
    /// capture the region they are about to change; <see cref="WorldOp.ReplaceBlock"/>
    /// additionally captures what it is about to write (for redo).
    /// </summary>
    private LogEntry PrepareUndo(Command cmd)
    {
        var entry = new LogEntry { Command = cmd };
        if (!IsDestructive(cmd.Op) || !cmd.Payload.HasValue) return entry;

        var payload = cmd.Payload.Value;
        switch (cmd.Op)
        {
            case WorldOp.DeleteCube:
            case WorldOp.SetFaceTexture:
            case WorldOp.ReplaceTexture:
            case WorldOp.SetFaceMaterial:
                {
                    var region = SelectionRegion(cmd);
                    entry.UndoRegion = region;
                    entry.UndoBlock = _world.Capture(region, region.Grid);
                    break;
                }
            case WorldOp.FlipCube:
            case WorldOp.RotateCube:
                {
                    // Rotate squares the selection's in-plane extent (mprotate sets
                    // ss = max(S[R], S[C])), so capture the square the edit can touch, not
                    // the pre-rotation rectangle — otherwise undo under-captures a thin one.
                    var sel = cmd.Selection!.ToBlock();
                    int d = Selection.Dimension(sel.Orient);
                    int r = Octa.R[d], c = Octa.C[d];
                    int ss = Math.Max(sel.S[r], sel.S[c]);
                    var origin = sel.O;
                    var size = sel.S;
                    size = SetAxis(size, r, ss);
                    size = SetAxis(size, c, ss);
                    var region = new Block3(origin, size, sel.Grid, sel.Orient);
                    entry.UndoRegion = region;
                    entry.UndoBlock = _world.Capture(region, region.Grid);
                    break;
                }
            case WorldOp.EditFace:
                {
                    // Fill collapses the selection to one grid slice and may move it by
                    // one cell along the oriented axis, so capture one cell either side to
                    // cover whichever slice is actually mutated.
                    var sel = payload.Item1.Sel.ToBlock();
                    int d = Selection.Dimension(sel.Orient);
                    var origin = sel.O;
                    var size = sel.S;
                    origin = SetAxis(origin, d, origin[d] - sel.Grid);
                    size = SetAxis(size, d, size[d] + 2);
                    var region = new Block3(origin, size, sel.Grid, sel.Orient);
                    entry.UndoRegion = region;
                    entry.UndoBlock = _world.Capture(region, region.Grid);
                    break;
                }
            case WorldOp.ReplaceBlock:
                {
                    var p = payload.Item12;
                    var region = p.Sel.ToBlock();
                    entry.UndoRegion = region;
                    entry.UndoBlock = _world.Capture(region, region.Grid);
                    if (p.Data.HasValue)
                        entry.RedoBlock = NetMessage.DeserializeBlock(p.Data.Value.ToArray());
                    break;
                }
            case WorldOp.RemoveEntity:
                {
                    var p = payload.Item8;
                    var existing = _world.Entities.Find(e => e.Id == p.EntityId);
                    if (existing != null)
                    {
                        entry.UndoEntity = new MapEntity
                        {
                            Id = existing.Id,
                            Type = existing.Type,
                            Position = existing.Position,
                            Attr1 = existing.Attr1,
                            Attr2 = existing.Attr2,
                            Attr3 = existing.Attr3,
                            Attr4 = existing.Attr4,
                            Attr5 = existing.Attr5,
                        };
                    }
                    break;
                }
            case WorldOp.EditEntity:
                {
                    var p = payload.Item8;
                    var existing = _world.Entities.Find(e => e.Id == p.EntityId);
                    if (existing != null)
                    {
                        entry.UndoEntity = new MapEntity
                        {
                            Id = existing.Id,
                            Type = existing.Type,
                            Position = existing.Position,
                            Attr1 = existing.Attr1,
                            Attr2 = existing.Attr2,
                            Attr3 = existing.Attr3,
                            Attr4 = existing.Attr4,
                            Attr5 = existing.Attr5,
                        };
                    }
                    entry.RedoEntity = p.ToMapEntity();
                    break;
                }
            case WorldOp.AddEntity:
                break;
        }
        return entry;
    }

    private static Block3 SelectionRegion(Command cmd) =>
        cmd.Selection?.ToBlock() ?? default;

    private static Vector3I SetAxis(Vector3I v, int d, int value) => d switch
    {
        0 => new Vector3I(value, v.Y, v.Z),
        1 => new Vector3I(v.X, value, v.Z),
        _ => new Vector3I(v.X, v.Y, value),
    };
}
