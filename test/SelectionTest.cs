using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers <see cref="Selection"/> and <see cref="Block3"/> against the original
/// <c>selinfo</c> (shared/iengine.h:62) and its operations in
/// <c>octaedit.cpp</c> (reorient, selextend, setselpos, movesel).
/// </summary>
public class SelectionTest : GoTestBase
{
    public SelectionTest(Node testScene) : base(testScene) { }

    [Test]
    public void Defaults()
    {
        var sel = new Selection();
        TestAssert.Equal(8, sel.Grid, "default grid is 8");
        TestAssert.Equal(0, sel.Orient, "default orient is 0");
        TestAssert.True(sel.IsZero, "s == 0 means no selection");
        TestAssert.Equal(0, sel.Size(), "size of empty selection");
    }

    [Test]
    public void DimensionAndDimCoord()
    {
        // dimension(orient) = orient >> 1, dimcoord(orient) = orient & 1 (octa.h:297).
        TestAssert.Equal(0, Selection.Dimension(0), "Left -> dim 0");
        TestAssert.Equal(0, Selection.Dimension(1), "Right -> dim 0");
        TestAssert.Equal(2, Selection.Dimension(4), "Bottom -> dim 2");
        TestAssert.Equal(2, Selection.Dimension(5), "Top -> dim 2");
        TestAssert.Equal(0, Selection.DimCoord(0), "Left -> coord 0");
        TestAssert.Equal(1, Selection.DimCoord(1), "Right -> coord 1");
        TestAssert.Equal(1, Selection.DimCoord(5), "Top -> coord 1");
    }

    [Test]
    public void UsIsGridScaledExtent()
    {
        var sel = new Selection(new Vector3I(0, 0, 0), new Vector3I(2, 3, 4), grid: 8);
        TestAssert.Equal(16, sel.Us(0), "us(x) = s.x * grid");
        TestAssert.Equal(24, sel.Us(1), "us(y) = s.y * grid");
        TestAssert.Equal(32, sel.Us(2), "us(z) = s.z * grid");
        TestAssert.Equal(24, sel.Size(), "size = s.x*s.y*s.z");
    }

    [Test]
    public void ReorientSetsCursorExtents()
    {
        var sel = new Selection(new Vector3I(0, 0, 0), new Vector3I(2, 3, 4));

        // orient 4 = Bottom (dim 2). cxs = s[dim]*2, cys = s[C[dim]]*2.
        sel.Reorient(4);
        TestAssert.Equal(4, sel.Orient, "orient stored");
        TestAssert.Equal(0, sel.Cx, "cx reset");
        TestAssert.Equal(0, sel.Cy, "cy reset");
        TestAssert.Equal(sel.S[Octa.R[2]] * 2, sel.Cxs, "cxs from row axis");
        TestAssert.Equal(sel.S[Octa.C[2]] * 2, sel.Cys, "cys from col axis");

        // orient 2 = Back (dim 1).
        sel.Reorient(2);
        TestAssert.Equal(sel.S[Octa.R[1]] * 2, sel.Cxs, "cxs after reorient to dim 1");
        TestAssert.Equal(sel.S[Octa.C[1]] * 2, sel.Cys, "cys after reorient to dim 1");
    }

    [Test]
    public void SetPosGridMasks()
    {
        var sel = new Selection { Grid = 8 };
        sel.SetPos(new Vector3I(19, 5, 254), gridSize: 8);
        TestAssert.Equal(new Vector3I(16, 0, 248), sel.O, "origin masked down to grid");
        TestAssert.Equal(Vector3I.Zero, sel.S, "size untouched by setselpos");
    }

    [Test]
    public void MoveStepsByGrid()
    {
        var sel = new Selection(new Vector3I(16, 16, 16), new Vector3I(1, 1, 1), grid: 8);
        sel.Grid = 8;
        sel.Move(1, 0);
        TestAssert.Equal(new Vector3I(24, 16, 16), sel.O, "move +x by one grid");
        sel.Move(-1, 2);
        TestAssert.Equal(new Vector3I(24, 16, 8), sel.O, "move -z by one grid");
        sel.Move(5, 7);
        TestAssert.Equal(new Vector3I(24, 16, 8), sel.O, "out-of-range dim is a no-op");
    }

    [Test]
    public void ExtendGrowsPositive()
    {
        // Starting selection [0,0,0] size 1 grid.
        var sel = new Selection(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), grid: 8);
        // Cursor at 8 lands exactly on the far edge -> size grows to 2.
        sel.Extend(new Vector3I(8, 0, 0));
        TestAssert.Equal(new Vector3I(0, 0, 0), sel.O, "origin unchanged");
        TestAssert.Equal(new Vector3I(2, 1, 1), sel.S, "size grows to include edge cell");
    }

    [Test]
    public void ExtendMovesOriginNegative()
    {
        var sel = new Selection(new Vector3I(16, 16, 16), new Vector3I(1, 1, 1), grid: 8);
        // Cursor before the origin -> origin moves and size grows.
        sel.Extend(new Vector3I(0, 16, 16));
        TestAssert.Equal(new Vector3I(0, 16, 16), sel.O, "origin snapped to cursor");
        TestAssert.Equal(new Vector3I(3, 1, 1), sel.S, "size absorbs the new cells");
    }

    [Test]
    public void ExtendInsideIsNoOp()
    {
        var sel = new Selection(new Vector3I(0, 0, 0), new Vector3I(4, 4, 4), grid: 8);
        sel.Extend(new Vector3I(16, 16, 16)); // inside, not on far edge
        TestAssert.Equal(new Vector3I(0, 0, 0), sel.O, "origin unchanged");
        TestAssert.Equal(new Vector3I(4, 4, 4), sel.S, "size unchanged");
    }

    [Test]
    public void ValidateClampsToWorld()
    {
        var sel = new Selection(new Vector3I(-8, 0, 0), new Vector3I(4, 4, 4), grid: 8);
        bool ok = sel.Validate(worldSize: 64);
        TestAssert.True(ok, "still non-empty after clamping");
        TestAssert.Equal(new Vector3I(0, 0, 0), sel.O, "negative origin pulled to 0");
        TestAssert.Equal(new Vector3I(3, 4, 4), sel.S, "size shrinks by the clamped cells");
    }

    [Test]
    public void ValidateRejectsBadGridAndOutOfWorld()
    {
        TestAssert.True(!new Selection(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), grid: 0).Validate(64),
            "grid <= 0 invalid");
        TestAssert.True(!new Selection(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), grid: 64).Validate(64),
            "grid >= worldsize invalid");
        TestAssert.True(!new Selection(new Vector3I(64, 0, 0), new Vector3I(1, 1, 1), grid: 8).Validate(64),
            "origin outside world invalid");
    }

    [Test]
    public void ValidateClampsFarEdge()
    {
        var sel = new Selection(new Vector3I(56, 0, 0), new Vector3I(4, 1, 1), grid: 8);
        TestAssert.True(sel.Validate(worldSize: 64), "clamped to far edge is valid");
        TestAssert.Equal(1, sel.S.X, "size clamped to remaining space");
    }

    [Test]
    public void CloneAndCopyFrom()
    {
        var sel = new Selection(new Vector3I(8, 16, 24), new Vector3I(2, 3, 1), grid: 8, orient: 5);
        sel.Cx = 1; sel.Cy = 2; sel.Cxs = 3; sel.Cys = 4; sel.Corner = 6;

        var clone = sel.Clone();
        TestAssert.True(sel.Matches(clone), "clone matches");
        clone.Move(1, 0);
        TestAssert.True(!sel.Matches(clone), "clone is independent");

        var dst = new Selection();
        dst.CopyFrom(sel);
        TestAssert.True(dst.Matches(sel), "copyfrom matches");
        TestAssert.Equal(1, dst.Cx, "copyfrom carries cursor state");
        TestAssert.Equal(6, dst.Corner, "copyfrom carries corner");
    }

    [Test]
    public void Block3FromSelection()
    {
        var sel = new Selection(new Vector3I(8, 16, 24), new Vector3I(2, 3, 4), grid: 8, orient: 2);
        var b = new Block3(sel);
        TestAssert.Equal(sel.O, b.O, "block origin");
        TestAssert.Equal(sel.S, b.S, "block size");
        TestAssert.Equal(sel.Grid, b.Grid, "block grid");
        TestAssert.Equal(sel.Orient, b.Orient, "block orient");
        TestAssert.Equal(24, b.Size(), "block size in cells");
        TestAssert.True(!b.IsZero, "non-empty block");
        TestAssert.True(new Block3().IsZero, "default block is zero");
    }
}
