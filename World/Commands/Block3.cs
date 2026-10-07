using Godot;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// A rectangular region of an octree at a fixed grid size — a C# port of
/// <c>block3</c> (octa.h:215). It is the geometry of a captured/copied block:
/// an origin, a size in grid units, the grid size, and the face orientation the
/// block was copied at.
///
/// Unlike the original (which allocated <c>block3</c> + a flat <c>cube[]</c>
/// payload in one buffer), the cube data is carried separately — a
/// <see cref="Block3"/> only describes *where* a block lives. The payload
/// (the captured cubes) is owned by the command/undo layer.
/// </summary>
public readonly struct Block3
{
    /// <summary>Origin in world units.</summary>
    public readonly Vector3I O;

    /// <summary>Size in grid units (not world units).</summary>
    public readonly Vector3I S;

    /// <summary>Cube size of the block (world units per grid cell).</summary>
    public readonly int Grid;

    /// <summary>Face orientation the block was captured at.</summary>
    public readonly int Orient;

    public Block3(Vector3I o, Vector3I s, int grid, int orient)
    {
        O = o;
        S = s;
        Grid = grid;
        Orient = orient;
    }

    /// <summary>octa.h: <c>block3(const selinfo &sel)</c>.</summary>
    public Block3(Selection sel) : this(sel.O, sel.S, sel.Grid, sel.Orient) { }

    /// <summary>octa.h: <c>block3::size()</c> — total grid cells.</summary>
    public int Size() => S.X * S.Y * S.Z;

    /// <summary>octa.h: <c>block3::c()</c> is the payload; here it is the command layer's job. See docs.</summary>
    public bool IsZero => S == Vector3I.Zero;
}
