using Godot;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// Editor selection state — a C# port of <c>selinfo</c> (shared/iengine.h:62) plus
/// the selection operations from <c>octaedit.cpp</c> (reorient, selextend,
/// setselpos, movesel). This is the vocabulary every edit command speaks: a
/// command payload carries a <see cref="Block3"/>, and the cursor maintains a
/// <see cref="Selection"/> while extending it.
///
/// Identifiers keep the original word tokens (o, s, grid, orient, corner, cx,
/// cy, cxs, cys) but use C# PascalCase. Coordinates are <see cref="Vector3I"/>
/// (the project's ivec decision D, matching <see cref="Octa"/>).
///
/// Semantics are ported *literally*: integer division (not float) in
/// <see cref="Extend"/>, and grid masking in <see cref="SetPos"/>. Do not
/// "simplify" these — Cube2's selection feel depends on the exact math.
/// </summary>
public sealed class Selection
{
    /// <summary>octaedit.cpp reorient: the axis-edge the selection was last oriented on.</summary>
    public int Corner { get; set; }

    /// <summary>The cursor position within the selection plane (loopsel-style), and its extents.</summary>
    public int Cx { get; set; }
    public int Cxs { get; set; }
    public int Cy { get; set; }
    public int Cys { get; set; }

    /// <summary>Origin in world units (grid-aligned).</summary>
    public Vector3I O { get; set; }

    /// <summary>Size in grid units (not world units).</summary>
    public Vector3I S { get; set; }

    /// <summary>Cube size being edited (default 8, matching the original's selinfo()).</summary>
    public int Grid { get; set; } = 8;

    /// <summary>Face orientation being edited (see <see cref="Orient"/>).</summary>
    public int Orient { get; set; }

    public Selection() { }

    public Selection(Vector3I o, Vector3I s, int grid = 8, int orient = 0)
    {
        O = o;
        S = s;
        Grid = grid;
        Orient = orient;
    }

    /// <summary>iengine.h: <c>selinfo::size()</c> — total grid cells in the selection.</summary>
    public int Size() => S.X * S.Y * S.Z;

    /// <summary>iengine.h: <c>selinfo::us(d)</c> — selection extent in world units along axis d.</summary>
    public int Us(int d) => S[d] * Grid;

    /// <summary>iengine.h: <c>selinfo::operator==</c>.</summary>
    public bool Matches(Selection other) =>
        O == other.O && S == other.S && Grid == other.Grid && Orient == other.Orient;

    /// <summary>A cursor with no selection (s == 0).</summary>
    public bool IsZero => S == Vector3I.Zero;

    /// <summary>octa.h: <c>dimension(orient)</c>.</summary>
    public static int Dimension(int orient) => orient >> 1;

    /// <summary>octa.h: <c>dimcoord(orient)</c>.</summary>
    public static int DimCoord(int orient) => orient & 1;

    /// <summary>
    /// iengine.h: <c>selinfo::validate()</c> — clamps the selection to the world
    /// bounds, dropping the part that lies outside. Mutates <see cref="O"/> and
    /// <see cref="S"/> in place. Returns false if the resulting selection is empty.
    /// </summary>
    public bool Validate(int worldSize)
    {
        if (Grid <= 0 || Grid >= worldSize) return false;
        if (O.X >= worldSize || O.Y >= worldSize || O.Z >= worldSize) return false;

        // Pull the origin back to 0, shrinking the size by the number of grid
        // cells that fell off the negative side (integer division, as original).
        if (O.X < 0) { S = SetAxis(S, 0, S.X - (Grid - 1 - O.X) / Grid); O = SetAxis(O, 0, 0); }
        if (O.Y < 0) { S = SetAxis(S, 1, S.Y - (Grid - 1 - O.Y) / Grid); O = SetAxis(O, 1, 0); }
        if (O.Z < 0) { S = SetAxis(S, 2, S.Z - (Grid - 1 - O.Z) / Grid); O = SetAxis(O, 2, 0); }

        S = new Vector3I(
            Mathf.Clamp(S.X, 0, (worldSize - O.X) / Grid),
            Mathf.Clamp(S.Y, 0, (worldSize - O.Y) / Grid),
            Mathf.Clamp(S.Z, 0, (worldSize - O.Z) / Grid));

        return S.X > 0 && S.Y > 0 && S.Z > 0;
    }

    /// <summary>
    /// octaedit.cpp reorient — set the oriented axis and recompute the cursor
    /// extents (cxs/cys) from the selection's row/col sizes.
    /// </summary>
    public void Reorient(int orient)
    {
        Cx = 0;
        Cy = 0;
        Cxs = S[Octa.R[Dimension(orient)]] * 2;
        Cys = S[Octa.C[Dimension(orient)]] * 2;
        Orient = orient;
    }

    /// <summary>
    /// octaedit.cpp selextend — grow the selection to include <paramref name="cur"/>
    /// (a grid-aligned world position). Negative expansion moves the origin; positive
    /// expansion past the far edge grows the size. Integer division matches the original.
    /// </summary>
    public void Extend(Vector3I cur)
    {
        for (int i = 0; i < 3; i++)
        {
            if (cur[i] < O[i])
            {
                S = SetAxis(S, i, S[i] + (O[i] - cur[i]) / Grid);
                O = SetAxis(O, i, cur[i]);
            }
            else if (cur[i] >= O[i] + S[i] * Grid)
            {
                S = SetAxis(S, i, (cur[i] - O[i]) / Grid + 1);
            }
        }
    }

    /// <summary>octaedit.cpp setselpos — move the selection origin, grid-masked. Size is unchanged.</summary>
    public void SetPos(Vector3I pos, int gridSize)
    {
        O = new Vector3I(pos.X & ~(gridSize - 1), pos.Y & ~(gridSize - 1), pos.Z & ~(gridSize - 1));
    }

    /// <summary>octaedit.cpp movesel — step the selection origin by one grid cell along an axis.</summary>
    public void Move(int dir, int dim)
    {
        if (dim < 0 || dim > 2) return;
        O = SetAxis(O, dim, O[dim] + dir * Grid);
    }

    /// <summary>Return a copy of <paramref name="v"/> with axis <paramref name="d"/> set to <paramref name="value"/>.</summary>
    private static Vector3I SetAxis(Vector3I v, int d, int value) => d switch
    {
        0 => new Vector3I(value, v.Y, v.Z),
        1 => new Vector3I(v.X, value, v.Z),
        _ => new Vector3I(v.X, v.Y, value),
    };

    /// <summary>Deep copy (the original's <c>savedsel</c> / <c>selsave</c> pattern).</summary>
    public Selection Clone() => new()
    {
        Corner = Corner,
        Cx = Cx,
        Cxs = Cxs,
        Cy = Cy,
        Cys = Cys,
        O = O,
        S = S,
        Grid = Grid,
        Orient = Orient,
    };

    /// <summary>Copy another selection's state into this one (the original's <c>sel = savedsel</c>).</summary>
    public void CopyFrom(Selection other)
    {
        Corner = other.Corner;
        Cx = other.Cx; Cxs = other.Cxs;
        Cy = other.Cy; Cys = other.Cys;
        O = other.O; S = other.S; Grid = other.Grid; Orient = other.Orient;
    }
}
