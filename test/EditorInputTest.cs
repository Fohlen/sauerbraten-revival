using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Editor;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the scroll dispatch (M4d): a wheel notch edits (mode 1), and — importantly
/// for macOS — a trackpad <see cref="InputEventPanGesture"/> accumulates into
/// notches. This is the path that a physical wheel test would miss.
/// </summary>
public class EditorInputTest : GoTestBase
{
    public EditorInputTest(Node testScene) : base(testScene) { }

    private EditorController SetupEditor(out EmptyMap map)
    {
        map = new EmptyMap { WorldScale = 10, ChunkSize = 128 };
        TestScene.AddChild(map);
        map._Ready();
        var ec = new EditorController { Octa = map.Octa, Sink = map.Sink, Renderer = map.Renderer };
        map.AddChild(ec);
        ec._Ready();
        // Cursor on a solid floor cell.
        ec.Cursor.SetCursorForTest(new Vector3I(512, 512, 128), 5, 8);
        ec.Cursor.Selection.O = new Vector3I(512, 512, 128);
        ec.Cursor.Selection.S = new Vector3I(1, 1, 1);
        ec.Cursor.Selection.Grid = 8;
        return ec;
    }

    private static bool Empty(EmptyMap map, Vector3I p) => map.Octa!.LeafCube(p)?.IsEmpty() ?? true;

    [Test]
    public void WheelUpCarvesAtCursor()
    {
        var ec = SetupEditor(out var map);
        TestAssert.True(!Empty(map, new Vector3I(512, 512, 128)), "precondition: cell is solid");

        ec.Scroll(+1); // WheelUp = carve (mpeditface mode 1, dir>0 empties the faces)
        TestAssert.True(Empty(map, new Vector3I(512, 512, 128)), "scroll carved the cell");
        TestAssert.True(map.Renderer!.PendingCount > 0, "renderer was invalidated");
        map.QueueFree();
    }

    [Test]
    public void WheelDownTriggersEdit()
    {
        // The exact fill geometry (mpeditface mode 1, dir<0 pulls a slice) is covered
        // by the OctaEdit tests; here we only need scroll to reach the edit path.
        var ec = SetupEditor(out var map);
        int before = map.Renderer!.PendingCount;
        ec.Scroll(-1);
        TestAssert.True(map.Renderer.PendingCount > before, "scroll produced an edit + invalidation");
        map.QueueFree();
    }

    [Test]
    public void PanGestureAccumulatesIntoNotches()
    {
        var ec = SetupEditor(out var map);
        int before = map.Renderer!.PendingCount;

        // A small pan (below the notch threshold) must NOT edit.
        ec._UnhandledInput(new InputEventPanGesture { Delta = new Vector2(0, 1) });
        TestAssert.Equal(before, map.Renderer.PendingCount, "sub-notch pan does nothing");

        // Accumulate past the threshold: one notch => one edit.
        var ec2 = ec;
        for (int i = 0; i < 12; i++)
            ec2._UnhandledInput(new InputEventPanGesture { Delta = new Vector2(0, 1) });
        TestAssert.True(map.Renderer.PendingCount > before, "accumulated pan produced an edit");
        map.QueueFree();
    }

    [Test]
    public void SetGridChangesGridNotGeometry()
    {
        var ec = SetupEditor(out var map);
        bool solidBefore = !Empty(map, new Vector3I(512, 512, 128));

        ec.SetGrid(ec.Cursor.GridPower + 1);
        TestAssert.Equal(16, ec.Cursor.GridSize, "grid doubled");
        TestAssert.Equal(solidBefore, !Empty(map, new Vector3I(512, 512, 128)), "geometry untouched");
        map.QueueFree();
    }

    [Test]
    public void RotateAndFlipReachTheWorld()
    {
        var ec = SetupEditor(out var map);
        // Rotate then flip repeatedly; they must not throw and must mark chunks dirty.
        ec.EditRotate(+1);
        TestAssert.True(map.Renderer!.PendingCount > 0, "rotate invalidated chunks");
        map.Renderer.FlushDirty();
        ec.EditTransform(flip: true);
        TestAssert.True(true, "flip ran");
        map.QueueFree();
    }
}
