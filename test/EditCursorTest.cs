using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Editor;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the editor cursor geometry (M4a): <see cref="CursorGeometry"/> ray casts
/// and <see cref="EditCursor"/>'s grid normalization. Expected values are the
/// original engine's exact output from <c>tools/octaprobe</c>.
/// </summary>
public class EditCursorTest : GoTestBase
{
    public EditCursorTest(Node testScene) : base(testScene) { }

    [Test]
    public void RayBoxIntersectMatchesEngine()
    {
        // octaprobe: ray from (10,10,10) dir (-1,-1,-1) hits box (0,0,0) size 8
        // -> dist=3.4641 orient=1 (the +X face).
        var ray = new Vector3(-1, -1, -1).Normalized();
        bool hit = CursorGeometry.RayBoxIntersect(Vector3.Zero, new Vector3(8, 8, 8),
            new Vector3(10, 10, 10), ray, out float dist, out int orient);
        TestAssert.True(hit, "ray hits the box");
        TestAssert.True(Mathf.Abs(dist - 3.4641f) < 0.001f, $"dist {dist} ≈ 3.4641");
        TestAssert.Equal(1, orient, "orient is +X face");
    }

    [Test]
    public void RayBoxIntersectTopFace()
    {
        // octaprobe: ray dir -z from (4,4,20) -> dist=12 orient=5 (+Z face).
        bool hit = CursorGeometry.RayBoxIntersect(Vector3.Zero, new Vector3(8, 8, 8),
            new Vector3(4, 4, 20), new Vector3(0, 0, -1), out float dist, out int orient);
        TestAssert.True(hit, "ray hits the top face");
        TestAssert.Equal(12f, dist, "dist");
        TestAssert.Equal(5, orient, "orient is +Z face");
    }

    [Test]
    public void RayBoxIntersectMisses()
    {
        // A ray parallel to the box, offset laterally, never enters its slabs.
        bool hit = CursorGeometry.RayBoxIntersect(Vector3.Zero, new Vector3(8, 8, 8),
            new Vector3(100, 100, 100), new Vector3(0, 1, 0), out _, out _);
        TestAssert.True(!hit, "offset parallel ray misses");
    }

    [Test]
    public void NormalizeLookupCubeMatchesEngine()
    {
        // octaprobe: lusize 8, gridsize 8 -> unchanged (13,29,5).
        var (lu1, s1) = EditCursor.NormalizePure(new Vector3I(13, 29, 5), 8, new Vector3I(16, 16, 8), 8);
        TestAssert.Equal(new Vector3I(13, 29, 5), lu1, "g8 unchanged");
        TestAssert.Equal(8, s1, "g8 size");

        // gridsize 32 (grow): snap to the 32 cell -> (0,0,0).
        var (lu2, s2) = EditCursor.NormalizePure(lu1, s1, new Vector3I(16, 16, 8), 32);
        TestAssert.Equal(Vector3I.Zero, lu2, "g32 snaps to 32-cell");
        TestAssert.Equal(32, s2, "g32 size");

        // back to gridsize 8 (shrink): the /8*8 path from o=(20,20,12) -> (16,16,8).
        var (lu3, s3) = EditCursor.NormalizePure(lu2, s2, new Vector3I(20, 20, 12), 8);
        TestAssert.Equal(new Vector3I(16, 16, 8), lu3, "g8b /8*8 path");
        TestAssert.Equal(8, s3, "g8b size");
    }

    [Test]
    public void EditMovePlaneProjectsToPlane()
    {
        // With no handle (subsequent calls during a drag), the destination is the
        // raw plane intersection.
        var handle = Vector3.Zero;
        bool ok = CursorGeometry.EditMovePlane(Vector3.Zero, new Vector3(4, 4, 10),
            new Vector3(0, 0, -1), d: 2, off: 0f, ref handle, out Vector3 dest, first: false);
        TestAssert.True(ok, "plane intersection");
        TestAssert.Equal(4f, dest.X, "dest x");
        TestAssert.Equal(4f, dest.Y, "dest y");
        TestAssert.Equal(0f, dest.Z, "dest z on the plane");
    }

    [Test]
    public void EditMovePlaneFirstCallCapturesHandle()
    {
        // On the first call the handle captures (dest - planeOrigin), so dest is
        // zero afterwards; the caller re-adds it. This mirrors the original.
        var handle = Vector3.Zero;
        CursorGeometry.EditMovePlane(Vector3.Zero, new Vector3(4, 4, 10),
            new Vector3(0, 0, -1), d: 2, off: 0f, ref handle, out Vector3 dest, first: true);
        TestAssert.Equal(new Vector3(4, 4, 0), handle, "handle = dest - planeOrigin");
        TestAssert.Equal(Vector3.Zero.X, dest.X, "dest zeroed after capture");
    }

    [Test]
    public void EditMovePlaneParallelRayFails()
    {
        var handle = Vector3.Zero;
        bool ok = CursorGeometry.EditMovePlane(Vector3.Zero, new Vector3(0, 0, 10),
            new Vector3(1, 0, 0), d: 2, off: 0f, ref handle, out _, first: true);
        TestAssert.True(!ok, "ray parallel to the plane fails");
    }

    [Test]
    public void GridPowerClampsAndCancelsSelection()
    {
        var cursor = new EditCursor();
        cursor.BeginDrag(); // give it a selection
        cursor.SetGridPower(5, worldSize: 1024);
        TestAssert.Equal(5, cursor.GridPower, "power set");
        TestAssert.Equal(32, cursor.GridSize, "gridsize = 1<<power");
        TestAssert.True(!cursor.HasSelection, "grid change cancels the selection");
    }

    [Test]
    public void GridPowerClampsBelowWorldSize()
    {
        var cursor = new EditCursor();
        cursor.SetGridPower(12, worldSize: 1024); // gridsize would be 4096 >= worldsize
        TestAssert.Equal(512, cursor.GridSize, "gridsize clamped to worldsize/2");
    }

    [Test]
    public void ReorientSetsCursorExtents()
    {
        // reorient sets cx/cy = 0 and cxs/cys from the selection on the oriented axes.
        var cursor = new EditCursor();
        cursor.Selection.O = new Vector3I(0, 0, 0);
        cursor.Selection.S = new Vector3I(2, 3, 4);
        cursor.Selection.Grid = 8;

        cursor.Reorient(4); // Bottom: dim 2
        TestAssert.Equal(4, cursor.Selection.Orient, "orient stored");
        TestAssert.Equal(0, cursor.Selection.Cx, "cx reset");
        TestAssert.Equal(0, cursor.Selection.Cy, "cy reset");
        TestAssert.Equal(cursor.Selection.S[Octa.R[2]] * 2, cursor.Selection.Cxs, "cxs from row axis");
        TestAssert.Equal(cursor.Selection.S[Octa.C[2]] * 2, cursor.Selection.Cys, "cys from col axis");
    }

    [Test]
    public void ExtendSelectionGrowsToCursor()
    {
        var cursor = new EditCursor();
        cursor.SetCursorForTest(new Vector3I(0, 0, 0), 0, 8);
        cursor.Selection.O = new Vector3I(0, 0, 0);
        cursor.Selection.S = new Vector3I(1, 1, 1);
        cursor.Selection.Grid = 8;

        // Move the cursor to +X by two grid cells; extend grows the size.
        cursor.SetCursorForTest(new Vector3I(16, 0, 0), 0, 8);
        cursor.ExtendSelection();
        TestAssert.True(cursor.HasSelection, "extension creates/keeps a selection");
        TestAssert.Equal(new Vector3I(0, 0, 0), cursor.Selection.O, "origin unchanged");
        TestAssert.Equal(new Vector3I(3, 1, 1), cursor.Selection.S, "size absorbs the cells");
    }

    [Test]
    public void ExtendSelectionMovesOriginNegative()
    {
        var cursor = new EditCursor();
        cursor.Selection.O = new Vector3I(16, 16, 16);
        cursor.Selection.S = new Vector3I(1, 1, 1);
        cursor.Selection.Grid = 8;

        cursor.SetCursorForTest(new Vector3I(0, 16, 16), 0, 8);
        cursor.ExtendSelection();
        TestAssert.Equal(new Vector3I(0, 16, 16), cursor.Selection.O, "origin snapped to cursor");
        TestAssert.Equal(new Vector3I(3, 1, 1), cursor.Selection.S, "size grows");
    }

    [Test]
    public void MoveSelectionStepsByGrid()
    {
        var cursor = new EditCursor();
        cursor.Selection.O = new Vector3I(16, 16, 16);
        cursor.Selection.S = new Vector3I(1, 1, 1);
        cursor.Selection.Grid = 8;

        cursor.MoveSelection(1, 0);
        TestAssert.Equal(new Vector3I(24, 16, 16), cursor.Selection.O, "+x by one grid");
        cursor.MoveSelection(-1, 2);
        TestAssert.Equal(new Vector3I(24, 16, 8), cursor.Selection.O, "-z by one grid");
        cursor.MoveSelection(5, 7);
        TestAssert.Equal(new Vector3I(24, 16, 8), cursor.Selection.O, "out-of-range axis is a no-op");
    }

    [Test]
    public void MoveSelectionOrientedUsesFaceDirection()
    {
        var cursor = new EditCursor();
        cursor.Selection.O = new Vector3I(0, 0, 0);
        cursor.Selection.S = new Vector3I(1, 1, 1);
        cursor.Selection.Grid = 8;
        cursor.SetCursorForTest(Vector3I.Zero, orient: 5, gridSize: 8); // Top: dim 2, dc 1

        cursor.MoveSelectionOriented(1);
        // dc=1 -> s = -dir = -1, so origin moves -8 on axis 2.
        TestAssert.Equal(new Vector3I(0, 0, -8), cursor.Selection.O, "oriented move flips sign for +face");
    }

    [Test]
    public void CancelSelectionClearsState()
    {
        var cursor = new EditCursor();
        cursor.SetCursorForTest(Vector3I.Zero, 0, 8);
        cursor.Selection.S = new Vector3I(2, 2, 2);
        cursor.BeginDrag();
        TestAssert.True(cursor.HasSelection || cursor.Selection.S != Vector3I.Zero, "precondition");

        cursor.CancelSelection();
        TestAssert.True(!cursor.HasSelection, "selection cleared");
        TestAssert.Equal(Vector3I.Zero, cursor.Selection.S, "size zeroed");
        TestAssert.True(!cursor.Dragging, "drag reset");
    }

    [Test]
    public void SetSelectionPosMasksToGrid()
    {
        var cursor = new EditCursor();
        cursor.SetCursorForTest(Vector3I.Zero, 0, 8);
        cursor.SetSelectionPos(new Vector3I(19, 5, 254));
        TestAssert.Equal(new Vector3I(16, 0, 248), cursor.Selection.O, "origin masked to grid");
        TestAssert.True(cursor.HasSelection, "selection now exists");
    }
}
