using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Network;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// The single entry point for world state and mutation (docs/editor-and-commands.md §2).
/// Holds the live world state (<see cref="Octa"/>, <see cref="Entities"/>, <see cref="Environment"/>),
/// deterministic command dispatch, command log, and keyframe history.
///
/// Nothing mutates the octree directly; every edit, game-mode change and undo goes
/// through <see cref="Submit"/>. It captures the pre-state needed for undo, applies
/// the command deterministically, and records it in the log.
/// </summary>
public sealed class CommandSink
{
    private readonly CommandLog _log;
    private readonly List<KeyframePackage> _keyframes = new();

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

    public Octa Octa { get; }
    public List<MapEntity> Entities { get; } = new();
    public SetEnvironmentCmd? Environment { get; set; }
    public KeyframePackage? LatestKeyframe { get; private set; }

    /// <summary>
    /// Fired whenever a command is applied and logged (forward, undo, redo, or remote).
    /// Unified with <see cref="CommandLog.CommandApplied"/>.
    /// </summary>
    public event Action<Command>? CommandApplied
    {
        add => _log.CommandApplied += value;
        remove => _log.CommandApplied -= value;
    }

    /// <summary>Self-reference for backwards compatibility.</summary>
    public CommandSink World => this;

    public CommandLog Log => _log;
    public IReadOnlyList<KeyframePackage> Keyframes => _keyframes;

    public CommandSink(
        Octa octa,
        IEnumerable<MapEntity>? entities = null,
        SetEnvironmentCmd? environment = null,
        int localPeerId = 0,
        KeyframePackage? initialKeyframe = null)
    {
        Octa = octa;
        if (entities != null) Entities.AddRange(entities);
        Environment = environment;
        _log = new CommandLog(localPeerId);
        if (initialKeyframe != null)
        {
            _keyframes.Add(initialKeyframe);
        }
    }

    /// <summary>True if a given op needs a captured pre-state for undo.</summary>
    public static bool IsDestructive(WorldOp op) => Destructive.Contains(op);

    /// <summary>Captures a region of the live octree for undo.</summary>
    public OctaEdit.CapturedBlock Capture(Block3 region, int rgrid) => OctaEdit.BlockCopy(Octa, region, rgrid);

    /// <summary>
    /// Applies a command deterministically to the live world state without recording undo or log entries.
    /// </summary>
    public void Apply(Command cmd)
    {
        if (!cmd.Payload.HasValue) return;
        var payload = cmd.Payload.Value;

        switch (cmd.Op)
        {
            case WorldOp.DeleteCube:
                {
                    var p = payload.Item4;
                    OctaEdit.DeleteCube(Octa, p.Sel.ToBlock());
                    break;
                }
            case WorldOp.EditFace:
                {
                    var p = payload.Item1;
                    OctaEdit.EditFace(Octa, p.Sel.ToSelection(), p.Dir, p.Mode);
                    break;
                }
            case WorldOp.ReplaceBlock:
                {
                    var p = payload.Item12;
                    if (p.Data.HasValue)
                    {
                        var block = NetMessage.DeserializeBlock(p.Data.Value.ToArray());
                        OctaEdit.PasteBlock(Octa, block, p.Sel.ToBlock(), p.Sel.Grid);
                    }
                    break;
                }
            case WorldOp.SetFaceTexture:
                {
                    var p = payload.Item2;
                    OctaEdit.ForEachSelCube(Octa, p.Sel.ToBlock(), p.Sel.Grid,
                        (x, y, z, c) => OctaEdit.SetFaceTexture(c, p.TextureId, p.Flags));
                    break;
                }
            case WorldOp.ReplaceTexture:
                {
                    var p = payload.Item3;
                    OctaEdit.ReplaceTexture(Octa,
                        p.InSelection && p.Sel != null ? p.Sel.ToBlock() : null,
                        p.OldTexture, p.NewTexture, p.InSelection);
                    break;
                }
            case WorldOp.SetFaceMaterial:
                {
                    var p = payload.Item10;
                    OctaEdit.EditMaterial(Octa, p.Sel.ToBlock(), p.MaterialId, p.Filter);
                    break;
                }
            case WorldOp.FlipCube:
                {
                    var p = payload.Item6;
                    OctaEdit.FlipSelection(Octa, p.Sel.ToSelection());
                    break;
                }
            case WorldOp.RotateCube:
                {
                    var p = payload.Item7;
                    OctaEdit.RotateSelection(Octa, p.Sel.ToSelection(), p.Direction);
                    break;
                }
            case WorldOp.Remip:
                {
                    var p = payload.Item5;
                    LatestKeyframe = OctaEdit.RemipAll(Octa, p.MipVis, Entities, Environment);
                    break;
                }
            case WorldOp.NewMap:
                {
                    var p = payload.Item11;
                    Octa.EmptyMap(p.Scale);
                    break;
                }
            case WorldOp.AddEntity:
                {
                    var p = payload.Item8;
                    Entities.RemoveAll(e => e.Id == p.EntityId);
                    Entities.Add(p.ToMapEntity());
                    break;
                }
            case WorldOp.RemoveEntity:
                {
                    var p = payload.Item8;
                    Entities.RemoveAll(e => e.Id == p.EntityId);
                    break;
                }
            case WorldOp.EditEntity:
                {
                    var p = payload.Item8;
                    int idx = Entities.FindIndex(e => e.Id == p.EntityId);
                    if (idx >= 0)
                        Entities[idx] = p.ToMapEntity();
                    else
                        Entities.Add(p.ToMapEntity());
                    break;
                }
            case WorldOp.SetEnvironment:
                {
                    var p = payload.Item9;
                    Environment = p;
                    break;
                }
            default:
                throw new NotSupportedException($"Unhandled WorldOp {cmd.Op}");
        }
    }

    /// <summary>
    /// Captures a keyframe package at a remip boundary, increments milestone index,
    /// and chains the parent hash (ADR 0008 §4).
    /// </summary>
    public KeyframePackage CaptureKeyframeAndSquash(string? gitCommit = null)
    {
        uint keyframeIndex = (uint)_keyframes.Count;
        byte[] baseHash = _keyframes.Count > 0
            ? KeyframePackage.ComputeHash(_keyframes[^1])
            : Array.Empty<byte>();

        var keyframe = LatestKeyframe != null
            ? LatestKeyframe
            : KeyframePackage.Capture(
                Octa,
                Entities,
                Environment,
                keyframeIndex: keyframeIndex,
                baseSnapshotHash: baseHash,
                gitCommit: gitCommit);

        keyframe.KeyframeIndex = keyframeIndex;
        keyframe.BaseSnapshotHash = baseHash;
        if (gitCommit != null) keyframe.GitCommit = gitCommit;

        _keyframes.Add(keyframe);
        return keyframe;
    }

    /// <summary>
    /// Submit a locally-issued command: capture undo state, apply, record. Returns
    /// the log entry.
    /// </summary>
    public LogEntry Submit(Command cmd)
    {
        if (cmd.Op == WorldOp.Remip)
        {
            _log.ClearDeltas();
        }

        var entry = PrepareUndo(cmd);
        Apply(cmd);
        var recorded = _log.Record(cmd);
        recorded.UndoBlock = entry.UndoBlock;
        recorded.UndoRegion = entry.UndoRegion;
        recorded.RedoBlock = entry.RedoBlock;
        recorded.UndoEntity = entry.UndoEntity;
        recorded.RedoEntity = entry.RedoEntity;

        if (cmd.Op == WorldOp.Remip)
        {
            CaptureKeyframeAndSquash();
        }

        return recorded;
    }

    /// <summary>Apply and record a command that originated on another peer (no undo entry for us).</summary>
    public void Receive(Command cmd)
    {
        if (cmd.Op == WorldOp.Remip)
        {
            _log.ClearDeltas();
        }

        Apply(cmd);
        _log.RecordRemote(cmd);

        if (cmd.Op == WorldOp.Remip)
        {
            CaptureKeyframeAndSquash();
        }
    }

    /// <summary>
    /// Undo the most recent local command. Returns the undone entry, or null if
    /// there is nothing to undo. Reverts by restoring the captured pre-state.
    /// </summary>
    public LogEntry? Undo()
    {
        var entry = _log.PeekUndo();
        if (entry == null) return null;

        Command? revertedCmd = null;
        if (entry.UndoBlock != null)
        {
            revertedCmd = Command.CreateReplaceBlock(
                entry.UndoRegion.ToSelection(),
                NetMessage.SerializeBlock(entry.UndoBlock),
                _log.LocalPeerId);
            Apply(revertedCmd);
        }
        else if (entry.Command.Op == WorldOp.AddEntity && entry.Command.Payload.HasValue)
        {
            var p = entry.Command.Payload.Value.Item8;
            revertedCmd = Command.CreateRemoveEntity(p.EntityId, _log.LocalPeerId);
            Apply(revertedCmd);
        }
        else if (entry.Command.Op == WorldOp.RemoveEntity && entry.UndoEntity != null)
        {
            revertedCmd = Command.CreateAddEntity(entry.UndoEntity, _log.LocalPeerId);
            Apply(revertedCmd);
        }
        else if (entry.Command.Op == WorldOp.EditEntity && entry.UndoEntity != null)
        {
            revertedCmd = Command.CreateEditEntity(entry.UndoEntity, _log.LocalPeerId);
            Apply(revertedCmd);
        }
        else if (entry.Command.Op == WorldOp.Remip)
        {
            // Derivation-only op: no pre-state to restore.
        }

        if (revertedCmd != null)
        {
            _log.NotifyReverted(revertedCmd);
        }

        return _log.PopForUndo();
    }

    /// <summary>Redo the most recently undone command, re-applying it and recording it again.</summary>
    public LogEntry? Redo()
    {
        var entry = _log.PopForRedo();
        if (entry == null) return null;

        var fresh = PrepareUndo(entry.Command);
        if (entry.Command.Op == WorldOp.ReplaceBlock && entry.RedoBlock != null)
        {
            var again = Command.CreateReplaceBlock(
                entry.UndoRegion.ToSelection(),
                NetMessage.SerializeBlock(entry.RedoBlock),
                _log.LocalPeerId);
            Apply(again);
        }
        else
        {
            Apply(entry.Command);
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
                    entry.UndoBlock = Capture(region, region.Grid);
                    break;
                }
            case WorldOp.FlipCube:
            case WorldOp.RotateCube:
                {
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
                    entry.UndoBlock = Capture(region, region.Grid);
                    break;
                }
            case WorldOp.EditFace:
                {
                    var sel = payload.Item1.Sel.ToBlock();
                    int d = Selection.Dimension(sel.Orient);
                    var origin = sel.O;
                    var size = sel.S;
                    origin = SetAxis(origin, d, origin[d] - sel.Grid);
                    size = SetAxis(size, d, size[d] + 2);
                    var region = new Block3(origin, size, sel.Grid, sel.Orient);
                    entry.UndoRegion = region;
                    entry.UndoBlock = Capture(region, region.Grid);
                    break;
                }
            case WorldOp.ReplaceBlock:
                {
                    var p = payload.Item12;
                    var region = p.Sel.ToBlock();
                    entry.UndoRegion = region;
                    entry.UndoBlock = Capture(region, region.Grid);
                    if (p.Data.HasValue)
                        entry.RedoBlock = NetMessage.DeserializeBlock(p.Data.Value.ToArray());
                    break;
                }
            case WorldOp.RemoveEntity:
                {
                    var p = payload.Item8;
                    var existing = Entities.Find(e => e.Id == p.EntityId);
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
                    var existing = Entities.Find(e => e.Id == p.EntityId);
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
