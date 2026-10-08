using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Editor;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the <c>delta_use</c>/<c>delta_do</c> modifier dispatch — the port of
/// Cube2's universal delta (<c>data/stdlib.cfg</c>). A held modifier key selects
/// the operation; the wheel then applies it. The default (no modifier) is fill.
/// </summary>
public class DeltaDispatchTest : GoTestBase
{
    public DeltaDispatchTest(Node testScene) : base(testScene) { }

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
        ec.Cursor.Selection.Orient = 5;
        return ec;
    }

    private static WorldOp LastOp(EditorController ec) => ec.Sink!.Log.PeekUndo()!.Command.Op;

    [Test]
    public void NoModifierIsFill()
    {
        var ec = Setup(out var map);
        TestAssert.True(ec.ActiveModifier == null, "no modifier active by default");

        ec.DeltaDo(+1);
        TestAssert.Equal(WorldOp.EditFace, LastOp(ec), "default scroll is a fill (EditFace)");
        var p = (EditFacePayload)ec.Sink!.Log.PeekUndo()!.Command.Payload;
        TestAssert.Equal(1, p.Mode, "default fill is mpeditface mode 1");
        map.QueueFree();
    }

    [Test]
    public void GridModifierChangesGridNotGeometry()
    {
        var ec = Setup(out var map);
        int gridBefore = ec.Cursor.GridSize;
        bool solidBefore = !(map.Octa!.LeafCube(new Vector3I(512, 512, 128))?.IsEmpty() ?? true);

        ec.PressModifier(DeltaModifier.Grid);
        ec.DeltaDo(+1);

        TestAssert.Equal(gridBefore * 2, ec.Cursor.GridSize, "grid modifier doubled the grid");
        TestAssert.Equal(solidBefore, !(map.Octa!.LeafCube(new Vector3I(512, 512, 128))?.IsEmpty() ?? true),
            "grid modifier did not touch geometry");
        map.QueueFree();
    }

    [Test]
    public void FaceModifierIsEditFaceMode0()
    {
        var ec = Setup(out var map);
        ec.PressModifier(DeltaModifier.Face);
        ec.DeltaDo(+1);

        TestAssert.Equal(WorldOp.EditFace, LastOp(ec), "face modifier uses EditFace");
        var p = (EditFacePayload)ec.Sink!.Log.PeekUndo()!.Command.Payload;
        TestAssert.Equal(0, p.Mode, "face modifier is mpeditface mode 0");
        map.QueueFree();
    }

    [Test]
    public void CornerModifierIsEditFaceMode2()
    {
        var ec = Setup(out var map);
        ec.PressModifier(DeltaModifier.Corner);
        ec.DeltaDo(+1);

        TestAssert.Equal(WorldOp.EditFace, LastOp(ec), "corner modifier uses EditFace");
        var p = (EditFacePayload)ec.Sink!.Log.PeekUndo()!.Command.Payload;
        TestAssert.Equal(2, p.Mode, "corner modifier is mpeditface mode 2");
        map.QueueFree();
    }

    [Test]
    public void RotateModifierIsRotateCube()
    {
        var ec = Setup(out var map);
        ec.PressModifier(DeltaModifier.Rotate);
        ec.DeltaDo(+1);
        TestAssert.Equal(WorldOp.RotateCube, LastOp(ec), "rotate modifier uses RotateCube");
        map.QueueFree();
    }

    [Test]
    public void TextureModifierIsSetFaceTexture()
    {
        var ec = Setup(out var map);
        ec.PressModifier(DeltaModifier.Texture);
        ec.DeltaDo(+1);

        TestAssert.Equal(WorldOp.SetFaceTexture, LastOp(ec), "texture modifier uses SetFaceTexture");
        var p = (SetFaceTexturePayload)ec.Sink!.Log.PeekUndo()!.Command.Payload;
        TestAssert.Equal(ec.CurrentTexture, p.Texture, "texture modifier paints the current texture");
        TestAssert.Equal(ec.Cursor.Selection.Orient, p.Orient, "single-face unless allfaces");
        map.QueueFree();
    }

    [Test]
    public void FirstPressedModifierWins()
    {
        var ec = Setup(out var map);
        ec.PressModifier(DeltaModifier.Face);
        ec.PressModifier(DeltaModifier.Corner); // ignored: a modifier is already active
        TestAssert.Equal(DeltaModifier.Face, ec.ActiveModifier, "first pressed modifier wins");
        map.QueueFree();
    }

    [Test]
    public void ReleaseOnlyClearsTheMatchingModifier()
    {
        var ec = Setup(out var map);
        ec.PressModifier(DeltaModifier.Rotate);
        ec.ReleaseModifier(DeltaModifier.Face); // wrong modifier: no effect
        TestAssert.Equal(DeltaModifier.Rotate, ec.ActiveModifier, "releasing a different modifier is a no-op");
        ec.ReleaseModifier(DeltaModifier.Rotate);
        TestAssert.True(ec.ActiveModifier == null, "releasing the active modifier clears it");
        map.QueueFree();
    }

    [Test]
    public void UnportedModifiersAreNoOpsNotFill()
    {
        // The gated ids must NOT silently fall through to the fill default; a scroll
        // with them held should leave the log empty.
        var ec = Setup(out var map);
        int before = ec.Sink!.Log.Entries.Count;

        DeltaModifier[] gated =
        {
            DeltaModifier.HeightMapBrush, DeltaModifier.EntAutoView,
            DeltaModifier.EntAttr1, DeltaModifier.EntAttr5,
            DeltaModifier.TexOffsetX, DeltaModifier.TexScale,
            DeltaModifier.ComboEntAttr1TexOffsetX, DeltaModifier.ComboEntAttr5,
        };
        foreach (var m in gated)
        {
            ec.PressModifier(m);
            ec.DeltaDo(+1);
            ec.ReleaseModifier(m);
        }

        TestAssert.Equal(before, ec.Sink!.Log.Entries.Count, "gated modifiers produced no commands");
        map.QueueFree();
    }

    [Test]
    public void AllFacesRoutesOrientationMinusOne()
    {
        var ec = Setup(out var map);
        TestAssert.True(!ec.AllFaces, "allfaces off by default");
        ec.ToggleAllFaces();
        ec.EditTex(42);

        var p = (SetFaceTexturePayload)ec.Sink!.Log.PeekUndo()!.Command.Payload;
        TestAssert.Equal(-1, p.Orient, "allfaces paints all six faces (orient -1)");
        map.QueueFree();
    }

    [Test]
    public void MaterialBindSubtractsAndFilters()
    {
        var ec = Setup(out var map);
        ec.EditMat(Octa.MAT_CLIP);
        TestAssert.Equal(WorldOp.SetFaceMaterial, LastOp(ec), "editmat uses SetFaceMaterial");
        map.QueueFree();
    }

    [Test]
    public void RemipIsPassThroughAndRecorded()
    {
        var ec = Setup(out var map);
        ec.EditRemip(mipvis: true);
        var entry = ec.Sink!.Log.PeekUndo();
        TestAssert.True(entry != null, "remip was submitted");
        TestAssert.Equal(WorldOp.Remip, entry!.Command.Op, "remip op");
        TestAssert.True(((RemipPayload)entry.Command.Payload).MipVis, "mipvis rides the payload");
        map.QueueFree();
    }

    [Test]
    public void PasteHiliteSizesSelectionToClipboard()
    {
        var ec = Setup(out var map);
        ec.Cursor.Selection.S = new Vector3I(2, 1, 1);
        ec.EditCopy();
        ec.Cursor.CancelSelection();
        ec.PasteHilite();

        TestAssert.True(ec.Cursor.HasSelection, "pastehilite creates a selection");
        TestAssert.Equal(new Vector3I(2, 1, 1), ec.Cursor.Selection.S, "selection matches the copied block");
        map.QueueFree();
    }

    [Test]
    public void PushSelectionMovesAlongOrientedAxis()
    {
        var ec = Setup(out var map);
        // Orient +Z (5): dimension 2, dimcoord 1 -> MoveSelectionOriented flips sign.
        var before = ec.Cursor.Selection.O;
        ec.PushSelection(+1);
        TestAssert.True(ec.Cursor.Selection.O != before, "pushsel moved the selection");
        map.QueueFree();
    }
}
