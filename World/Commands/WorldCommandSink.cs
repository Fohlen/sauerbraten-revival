using System.Collections.Generic;
using Godot;

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
public sealed class WorldCommandSink
{
    private readonly IWorld _world;
    private readonly WorldCommandLog _log;

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
    };

    public IWorld World => _world;
    public WorldCommandLog Log => _log;

    public WorldCommandSink(IWorld world, int localPeerId = 0)
    {
        _world = world;
        _log = new WorldCommandLog(localPeerId);
    }

    /// <summary>True if a given op needs a captured pre-state for undo.</summary>
    public static bool IsDestructive(WorldOp op) => Destructive.Contains(op);

    /// <summary>
    /// Submit a locally-issued command: capture undo state, apply, record. Returns
    /// the log entry.
    /// </summary>
    public LogEntry Submit(WorldCommand cmd)
    {
        var entry = PrepareUndo(cmd);
        _world.Apply(cmd);
        var recorded = _log.Record(cmd);
        recorded.UndoBlock = entry.UndoBlock;
        recorded.UndoRegion = entry.UndoRegion;
        recorded.RedoBlock = entry.RedoBlock;
        return recorded;
    }

    /// <summary>Apply and record a command that originated on another peer (no undo entry for us).</summary>
    public void Receive(WorldCommand cmd)
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
            var restore = new WorldCommand(WorldOp.ReplaceBlock,
                new ReplaceBlockPayload(new SelectionPayload(entry.UndoRegion.O, entry.UndoRegion.S, entry.UndoRegion.Grid, entry.UndoRegion.Orient), entry.UndoBlock))
            { AuthorPeerId = _log.LocalPeerId };
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
            var again = new WorldCommand(WorldOp.ReplaceBlock,
                new ReplaceBlockPayload(new SelectionPayload(entry.UndoRegion.O, entry.UndoRegion.S, entry.UndoRegion.Grid, entry.UndoRegion.Orient), entry.RedoBlock))
            { AuthorPeerId = _log.LocalPeerId };
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
        return recorded;
    }

    /// <summary>
    /// Build the undo state for a command before it is applied. Destructive ops
    /// capture the region they are about to change; <see cref="WorldOp.ReplaceBlock"/>
    /// additionally captures what it is about to write (for redo).
    /// </summary>
    private LogEntry PrepareUndo(WorldCommand cmd)
    {
        var entry = new LogEntry { Command = cmd };
        if (!IsDestructive(cmd.Op)) return entry;

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
                var p = (TransformCubePayload)cmd.Payload;
                var sel = p.Selection;
                int d = Selection.Dimension(sel.Orient);
                int r = Octa.R[d], c = Octa.C[d];
                int ss = System.Math.Max(sel.Size[r], sel.Size[c]);
                var origin = sel.Origin;
                var size = sel.Size;
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
                var p = (EditFacePayload)cmd.Payload;
                var sel = p.Selection;
                int d = Selection.Dimension(sel.Orient);
                var origin = sel.Origin;
                var size = sel.Size;
                origin = SetAxis(origin, d, origin[d] - sel.Grid);
                size = SetAxis(size, d, size[d] + 2);
                var region = new Block3(origin, size, sel.Grid, sel.Orient);
                entry.UndoRegion = region;
                entry.UndoBlock = _world.Capture(region, region.Grid);
                break;
            }
            case WorldOp.ReplaceBlock:
            {
                var p = (ReplaceBlockPayload)cmd.Payload;
                var region = p.Target.ToBlock();
                entry.UndoRegion = region;
                entry.UndoBlock = _world.Capture(region, region.Grid);
                entry.RedoBlock = p.Block;
                break;
            }
        }
        return entry;
    }

    private static Block3 SelectionRegion(WorldCommand cmd) => cmd.Payload switch
    {
        DeleteCubePayload p => p.Selection.ToBlock(),
        EditFacePayload p => p.Selection.ToBlock(),
        SetFaceTexturePayload p => p.Selection.ToBlock(),
        ReplaceTexturePayload p => p.Selection?.ToBlock() ?? default,
        SetFaceMaterialPayload p => p.Selection.ToBlock(),
        TransformCubePayload p => p.Selection.ToBlock(),
        _ => default,
    };

    private static Vector3I SetAxis(Vector3I v, int d, int value) => d switch
    {
        0 => new Vector3I(value, v.Y, v.Z),
        1 => new Vector3I(v.X, value, v.Z),
        _ => new Vector3I(v.X, v.Y, value),
    };
}
