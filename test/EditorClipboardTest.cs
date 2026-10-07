using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Editor;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers clipboard + undo/redo bindings (M4d): copy is client-local, paste is a
/// ReplaceBlock command, cut is copy+delete, and undo/redo drive the sink.
/// </summary>
public class EditorClipboardTest : GoTestBase
{
    public EditorClipboardTest(Node testScene) : base(testScene) { }

    private EditorController Setup(out EmptyMap map)
    {
        map = new EmptyMap { WorldScale = 10, ChunkSize = 128 };
        TestScene.AddChild(map);
        map._Ready();
        var ec = new EditorController { Octa = map.Octa, Sink = map.Sink, Renderer = map.Renderer };
        map.AddChild(ec);
        ec._Ready();
        ec.Cursor.SetCursorForTest(new Vector3I(512, 512, 128), 5, 8);
        ec.Cursor.Selection.O = new Vector3I(512, 512, 128);
        ec.Cursor.Selection.S = new Vector3I(1, 1, 1);
        ec.Cursor.Selection.Grid = 8;
        return ec;
    }

    private static bool Empty(EmptyMap map, Vector3I p) => map.Octa!.LeafCube(p)?.IsEmpty() ?? true;

    [Test]
    public void CopyCapturesWithoutMutating()
    {
        var ec = Setup(out var map);
        bool solidBefore = !Empty(map, new Vector3I(512, 512, 128));
        ec.EditCopy();
        TestAssert.True(ec.Clipboard != null, "clipboard captured");
        TestAssert.Equal(solidBefore, !Empty(map, new Vector3I(512, 512, 128)), "copy did not mutate");
        map.QueueFree();
    }

    [Test]
    public void CutCopiesAndDeletes()
    {
        var ec = Setup(out var map);
        TestAssert.True(!Empty(map, new Vector3I(512, 512, 128)), "precondition: solid");
        ec.EditCut();
        TestAssert.True(ec.Clipboard != null, "clipboard captured");
        TestAssert.True(Empty(map, new Vector3I(512, 512, 128)), "cut deleted the cell");
        map.QueueFree();
    }

    [Test]
    public void PasteRestoresCopiedCell()
    {
        var ec = Setup(out var map);
        var cell = new Vector3I(512, 512, 128);
        ec.EditCopy();
        ec.EditDelete();
        TestAssert.True(Empty(map, cell), "deleted");

        ec.EditPaste();
        TestAssert.True(!Empty(map, cell), "paste restored the cell");
        map.QueueFree();
    }

    [Test]
    public void UndoRevertsLastEdit()
    {
        var ec = Setup(out var map);
        var cell = new Vector3I(512, 512, 128);
        TestAssert.True(!Empty(map, cell), "precondition: solid");

        ec.EditDelete();
        TestAssert.True(Empty(map, cell), "deleted");

        ec.EditUndo();
        TestAssert.True(!Empty(map, cell), "undo restored the cell");
        map.QueueFree();
    }

    [Test]
    public void RedoReappliesUndoneEdit()
    {
        var ec = Setup(out var map);
        var cell = new Vector3I(512, 512, 128);
        ec.EditDelete();
        ec.EditUndo();
        TestAssert.True(!Empty(map, cell), "undo restored");

        ec.EditRedo();
        TestAssert.True(Empty(map, cell), "redo re-deleted");
        map.QueueFree();
    }

    [Test]
    public void MiddleMouseSetsCornerMode()
    {
        var ec = Setup(out var map);
        var down = new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true };
        ec._UnhandledInput(down);
        TestAssert.True(ec.Cursor.SelectCorners, "middle press enables corner mode");
        TestAssert.True(ec.Cursor.Dragging, "middle press starts a drag");

        var up = new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = false };
        ec._UnhandledInput(up);
        TestAssert.True(!ec.Cursor.SelectCorners, "release clears corner mode");
        map.QueueFree();
    }
}
