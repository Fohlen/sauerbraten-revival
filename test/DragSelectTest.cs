using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Editor;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks drag-select (M4b/M4d): press starts a rubber-band selection, moving the
/// cursor grows it. Mirrors Cube2's <c>editdrag</c> (cancelsel + drag).
/// </summary>
public class DragSelectTest : GoTestBase
{
    public DragSelectTest(Node testScene) : base(testScene) { }

    private static EditCursor CursorAt(Vector3I cell, int grid = 8)
    {
        var c = new EditCursor();
        c.SetCursorForTest(cell, 5, grid); // orient Top (dim 2)
        return c;
    }

    [Test]
    public void DragGrowsSelectionToCursor()
    {
        var w = new Octa(6); // worldsize 64, all solid
        var eye = new Vector3(0, 0, 40);
        var dir = new Vector3(0, 0, -1);

        var cursor = CursorAt(new Vector3I(8, 8, 8));
        cursor.BeginDrag(); // press

        // Move the cursor: ray hits the top of whatever is below; emulate by
        // updating to a further cell directly.
        cursor.SetCursorForTest(new Vector3I(24, 8, 8), 5, 8);
        float d = OctaRay.RayCube(w, eye, dir, 1000f, out _);
        cursor.Update(w, eye, dir, d);

        TestAssert.True(cursor.HasSelection, "drag created a selection");
        TestAssert.True(cursor.Selection.S.X >= 2, $"selection spans >1 cell on X (got {cursor.Selection.S.X})");
    }

    [Test]
    public void CancelSelectionClearsDrag()
    {
        var cursor = CursorAt(new Vector3I(8, 8, 8));
        cursor.BeginDrag();
        TestAssert.True(cursor.Dragging, "dragging");
        cursor.CancelSelection();
        TestAssert.True(!cursor.Dragging, "cancel ends the drag");
        TestAssert.True(!cursor.HasSelection, "no selection");
    }

    [Test]
    public void ExtendGrowsSelection()
    {
        var cursor = CursorAt(new Vector3I(0, 0, 0));
        cursor.Selection.O = new Vector3I(0, 0, 0);
        cursor.Selection.S = new Vector3I(1, 1, 1);
        cursor.Selection.Grid = 8;

        cursor.SetCursorForTest(new Vector3I(16, 0, 0), 5, 8);
        cursor.ExtendSelection();
        TestAssert.Equal(new Vector3I(3, 1, 1), cursor.Selection.S, "extend grows to the cursor");
        TestAssert.True(cursor.HasSelection, "has selection");
    }
}
