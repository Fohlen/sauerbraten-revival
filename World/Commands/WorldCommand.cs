using Godot;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// A world mutation. The op plus its op-specific parameters; the sequence number
/// gives it a total order in the log, and the author identifies who issued it
/// (attribution, not authority — docs/editor-and-commands.md §1.1).
///
/// Payloads are typed objects here; their canonical <c>byte[]</c> form is the
/// codec's job (<see cref="EditCodec"/>), so save/demo/network share one encoding.
/// </summary>
public sealed class EditCommand
{
    /// <summary>What the command does.</summary>
    public EditOp Op { get; init; }

    /// <summary>Monotonic position in the log; assigned by the sink, not the caller.</summary>
    public ulong Sequence { get; set; }

    /// <summary>Which peer issued it (undo may only roll back this peer's commands, §5.5 Rule 1).</summary>
    public int AuthorPeerId { get; init; }

    /// <summary>Op-specific parameters.</summary>
    public ICommandPayload Payload { get; init; } = EmptyPayload.Instance;

    public EditCommand() { }

    public EditCommand(EditOp op, ICommandPayload payload, int author = 0)
    {
        Op = op;
        Payload = payload;
        AuthorPeerId = author;
    }

    public override string ToString() => $"{Op}#{Sequence}(peer {AuthorPeerId})";
}

/// <summary>Marker for op-specific parameter sets. Each concrete type maps 1:1 to an <see cref="EditOp"/>.</summary>
public interface ICommandPayload { }

/// <summary>Payload for ops that carry no parameters (e.g. <see cref="EditOp.Snapshot"/> has an index, but the no-arg form is a valid marker).</summary>
public sealed class EmptyPayload : ICommandPayload
{
    public static readonly EmptyPayload Instance = new();
}

/// <summary>
/// A rectangular selection, as commands carry it (the serializable subset of
/// <see cref="Selection"/>: origin, size, grid, orient — no cursor state).
/// </summary>
public readonly struct SelectionPayload : ICommandPayload
{
    public readonly Vector3I Origin;
    public readonly Vector3I Size;
    public readonly int Grid;
    public readonly int Orient;

    public SelectionPayload(Vector3I origin, Vector3I size, int grid, int orient)
    {
        Origin = origin;
        Size = size;
        Grid = grid;
        Orient = orient;
    }

    public SelectionPayload(Selection sel) : this(sel.O, sel.S, sel.Grid, sel.Orient) { }

    public Block3 ToBlock() => new(Origin, Size, Grid, Orient);

    public Selection ToSelection() => new(Origin, Size, Grid, Orient);
}

/// <summary><see cref="EditOp.DeleteCube"/>: empty every cube in the selection.</summary>
public sealed class DeleteCubePayload : ICommandPayload
{
    public SelectionPayload Selection;
    public DeleteCubePayload(SelectionPayload sel) => Selection = sel;
}

/// <summary><see cref="EditOp.Fill"/>: mpeditface mode 1 over a selection.</summary>
public sealed class FillPayload : ICommandPayload
{
    public SelectionPayload Selection;
    public int Dir;    // +1 push (carve), -1 pull (fill)
    public int Mode;   // 0/1/2 as in mpeditface; mode 1 is the fill branch

    public FillPayload(SelectionPayload sel, int dir, int mode)
    {
        Selection = sel;
        Dir = dir;
        Mode = mode;
    }
}

/// <summary><see cref="EditOp.SetFaceTexture"/>: edittexcube over a selection.</summary>
public sealed class SetFaceTexturePayload : ICommandPayload
{
    public SelectionPayload Selection;
    public int Texture;
    public int Orient;   // -1 = all faces, else the single face orientation

    public SetFaceTexturePayload(SelectionPayload sel, int texture, int orient)
    {
        Selection = sel;
        Texture = texture;
        Orient = orient;
    }
}

/// <summary><see cref="EditOp.ReplaceTexture"/>: replacetexcube.</summary>
public sealed class ReplaceTexturePayload : ICommandPayload
{
    public SelectionPayload? Selection;   // null = whole world
    public bool InSelection;
    public int OldTexture;
    public int NewTexture;

    public ReplaceTexturePayload(SelectionPayload? sel, bool inSelection, int oldTexture, int newTexture)
    {
        Selection = sel;
        InSelection = inSelection;
        OldTexture = oldTexture;
        NewTexture = newTexture;
    }
}

/// <summary><see cref="EditOp.SetFaceMaterial"/>: mpeditmat.</summary>
public sealed class SetFaceMaterialPayload : ICommandPayload
{
    public SelectionPayload Selection;
    public int MaterialId;   // < 0 removes the filtered material
    public int Filter;       // < 0 = no filter; else filtermat | (filtergeom << 16)

    public SetFaceMaterialPayload(SelectionPayload sel, int materialId, int filter)
    {
        Selection = sel;
        MaterialId = materialId;
        Filter = filter;
    }
}

/// <summary><see cref="EditOp.FlipCube"/> / <see cref="EditOp.RotateCube"/>: a selection plus an axis (and direction for rotate).</summary>
public sealed class TransformCubePayload : ICommandPayload
{
    public SelectionPayload Selection;
    public int Axis;        // dimension 0..2
    public int Direction;   // rotate: +1/-1 clockwise; unused for flip

    public TransformCubePayload(SelectionPayload sel, int axis, int direction = 0)
    {
        Selection = sel;
        Axis = axis;
        Direction = direction;
    }
}

/// <summary><see cref="EditOp.Remip"/>: pass-through. Only the selection and the user's mipvis toggle (docs §5.4).</summary>
public sealed class RemipPayload : ICommandPayload
{
    public bool MipVis;

    public RemipPayload(bool mipVis) => MipVis = mipVis;
}

/// <summary>
/// <see cref="EditOp.ReplaceBlock"/>: the universal carrier (docs §5.3). Writes a
/// captured region into the world. Paste, prefab placement and paste-style undo
/// all reduce to this. The block is held as live cubes for now; its canonical
/// serialized form lands with the save format (M5).
/// </summary>
public sealed class ReplaceBlockPayload : ICommandPayload
{
    /// <summary>Where the block is written (the target selection).</summary>
    public SelectionPayload Target;

    /// <summary>The captured cubes, in the block's own (x,y,z) order. May be null for an all-empty block.</summary>
    public World.OctaEdit.CapturedBlock? Block;

    public ReplaceBlockPayload(SelectionPayload target, World.OctaEdit.CapturedBlock? block)
    {
        Target = target;
        Block = block;
    }
}
