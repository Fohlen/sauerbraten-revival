using System.Runtime.InteropServices;
using Godot;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// A world mutation. The op plus its op-specific parameters; the sequence number
/// gives it a total order in the log, and the author identifies who issued it
/// (attribution, not authority — docs/editor-and-commands.md §1.1).
///
/// Payloads are lightweight value-type structs encoded via FlatSharp (<see cref="Network.NetMessage"/>),
/// so save/demo/network share a single zero-allocation encoding.
/// </summary>
public sealed class Command
{
    /// <summary>What the command does.</summary>
    public WorldOp Op { get; init; }

    /// <summary>Monotonic position in the log; assigned by the sink, not the caller.</summary>
    public ulong Sequence { get; set; }

    /// <summary>Which peer issued it (undo may only roll back this peer's commands, §5.5 Rule 1).</summary>
    public int AuthorPeerId { get; init; }

    /// <summary>Op-specific parameters.</summary>
    public ICommandPayload Payload { get; init; } = EmptyPayload.Instance;

    public Command() { }

    public Command(WorldOp op, ICommandPayload payload, int author = 0)
    {
        Op = op;
        Payload = payload;
        AuthorPeerId = author;
    }

    public override string ToString() => $"{Op}#{Sequence}(peer {AuthorPeerId})";
}

/// <summary>Marker for op-specific parameter sets. Each concrete type maps 1:1 to an <see cref="WorldOp"/>.</summary>
public interface ICommandPayload { }

/// <summary>Payload for ops that carry no parameters (e.g. <see cref="WorldOp.Snapshot"/>).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct EmptyPayload : ICommandPayload
{
    public static readonly EmptyPayload Instance = default;
}

/// <summary>
/// A rectangular selection, as commands carry it (the serializable subset of
/// <see cref="Selection"/>: origin, size, grid, orient — no cursor state).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SelectionPayload : ICommandPayload
{
    public const int PayloadSize = 32;

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

/// <summary><see cref="WorldOp.DeleteCube"/>: empty every cube in the selection.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct DeleteCubePayload : ICommandPayload
{
    public readonly SelectionPayload Selection;
    public DeleteCubePayload(SelectionPayload sel) => Selection = sel;
}

/// <summary><see cref="WorldOp.EditFace"/>: mpeditface mode 1 over a selection.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct EditFacePayload : ICommandPayload
{
    public readonly SelectionPayload Selection;
    public readonly int Dir;    // +1 push (carve), -1 pull (fill)
    public readonly int Mode;   // 0/1/2 as in mpeditface; mode 1 is the fill branch

    public EditFacePayload(SelectionPayload sel, int dir, int mode)
    {
        Selection = sel;
        Dir = dir;
        Mode = mode;
    }
}

/// <summary><see cref="WorldOp.SetFaceTexture"/>: edittexcube over a selection.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SetFaceTexturePayload : ICommandPayload
{
    public readonly SelectionPayload Selection;
    public readonly int Texture;
    public readonly int Orient;   // -1 = all faces, else the single face orientation

    public SetFaceTexturePayload(SelectionPayload sel, int texture, int orient)
    {
        Selection = sel;
        Texture = texture;
        Orient = orient;
    }
}

/// <summary><see cref="WorldOp.ReplaceTexture"/>: replacetexcube.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ReplaceTexturePayload : ICommandPayload
{
    private readonly SelectionPayload _selection;
    private readonly byte _hasSelection;
    private readonly byte _inSelection;
    public readonly int OldTexture;
    public readonly int NewTexture;

    public SelectionPayload? Selection => _hasSelection != 0 ? _selection : null;
    public bool InSelection => _inSelection != 0;

    public ReplaceTexturePayload(SelectionPayload? sel, bool inSelection, int oldTexture, int newTexture)
    {
        _selection = sel ?? default;
        _hasSelection = sel.HasValue ? (byte)1 : (byte)0;
        _inSelection = inSelection ? (byte)1 : (byte)0;
        OldTexture = oldTexture;
        NewTexture = newTexture;
    }
}

/// <summary><see cref="WorldOp.SetFaceMaterial"/>: mpeditmat.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SetFaceMaterialPayload : ICommandPayload
{
    public readonly SelectionPayload Selection;
    public readonly int MaterialId;   // < 0 removes the filtered material
    public readonly int Filter;       // < 0 = no filter; else filtermat | (filtergeom << 16)

    public SetFaceMaterialPayload(SelectionPayload sel, int materialId, int filter)
    {
        Selection = sel;
        MaterialId = materialId;
        Filter = filter;
    }
}

/// <summary>
/// <see cref="WorldOp.FlipCube"/> / <see cref="WorldOp.RotateCube"/>: a selection to
/// mirror or rotate. The axis comes from the selection's <c>Orient</c>; only the
/// rotation direction is a payload field.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct TransformCubePayload : ICommandPayload
{
    public readonly SelectionPayload Selection;
    public readonly int Direction;   // rotate: +1/-1 clockwise; unused for flip

    public TransformCubePayload(SelectionPayload sel, int direction = 0)
    {
        Selection = sel;
        Direction = direction;
    }
}

/// <summary><see cref="WorldOp.Remip"/>: pass-through. Only the selection and the user's mipvis toggle (docs §5.4).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct RemipPayload : ICommandPayload
{
    private readonly byte _mipVis;
    public bool MipVis => _mipVis != 0;

    public RemipPayload(bool mipVis) => _mipVis = mipVis ? (byte)1 : (byte)0;
}

/// <summary><see cref="WorldOp.NewMap"/>: build a fresh empty world at a scale (world.cpp <c>emptymap</c>).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct NewMapPayload : ICommandPayload
{
    public readonly int Scale;

    public NewMapPayload(int scale) => Scale = scale;
}

/// <summary>
/// <see cref="WorldOp.ReplaceBlock"/>: the universal carrier (docs §5.3). Writes a
/// captured region into the world. Paste, prefab placement and paste-style undo
/// all reduce to this.
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

/// <summary><see cref="WorldOp.AddEntity"/>, <see cref="WorldOp.RemoveEntity"/>, <see cref="WorldOp.EditEntity"/> payload.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct EntityPayload : ICommandPayload
{
    public ulong Id { get; init; }
    public byte Type { get; init; }
    public int FixedX { get; init; }
    public int FixedY { get; init; }
    public int FixedZ { get; init; }
    public short Attr1 { get; init; }
    public short Attr2 { get; init; }
    public short Attr3 { get; init; }
    public short Attr4 { get; init; }
    public short Attr5 { get; init; }

    public EntityPayload(ulong id, byte type = 0, int fx = 0, int fy = 0, int fz = 0,
        short a1 = 0, short a2 = 0, short a3 = 0, short a4 = 0, short a5 = 0)
    {
        Id = id;
        Type = type;
        FixedX = fx;
        FixedY = fy;
        FixedZ = fz;
        Attr1 = a1;
        Attr2 = a2;
        Attr3 = a3;
        Attr4 = a4;
        Attr5 = a5;
    }

    public EntityPayload(Entities.MapEntity entity)
    {
        Id = entity.Id;
        Type = entity.Type;
        var (x, y, z) = entity.FixedPosition;
        FixedX = x;
        FixedY = y;
        FixedZ = z;
        Attr1 = entity.Attr1;
        Attr2 = entity.Attr2;
        Attr3 = entity.Attr3;
        Attr4 = entity.Attr4;
        Attr5 = entity.Attr5;
    }

    public Entities.MapEntity ToEntity() => new()
    {
        Id = Id,
        Type = Type,
        Position = new Vector3(
            FixedX / (float)FixedPoint.DMF,
            FixedY / (float)FixedPoint.DMF,
            FixedZ / (float)FixedPoint.DMF),
        Attr1 = Attr1,
        Attr2 = Attr2,
        Attr3 = Attr3,
        Attr4 = Attr4,
        Attr5 = Attr5,
    };
}

/// <summary><see cref="WorldOp.SetEnvironment"/>: map environment parameters (sun, sky, ambient, fog).</summary>
public sealed class SetEnvironmentPayload : ICommandPayload
{
    public uint SunColor { get; init; } = 0xFFFFFF;
    public float SunScale { get; init; } = 1f;
    public float SunYaw { get; init; } = 104f;
    public float SunPitch { get; init; } = 42f;
    public uint SkyColor { get; init; } = 0x000000;
    public uint AmbientColor { get; init; } = 0x191919;
    public string Skybox { get; init; } = "";
    public float FogDistance { get; init; } = 1000f;
    public uint FogColor { get; init; } = 0x8099B3;
}
