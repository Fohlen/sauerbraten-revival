using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the M1c op handlers in <see cref="OctaEdit"/>: block capture
/// (<c>blockcopy</c>), transparent paste (<c>pasteblock</c>), delete
/// (<c>mpdelcube</c>) and fill (<c>mpeditface</c> mode 1). Outcomes marked
/// "octaprobe" are the original engine's exact output from
/// <c>tools/octaprobe</c>.
///
/// World model used here: <see cref="Octa"/> at worldscale 3 has
/// <c>WorldRoot</c> of eight size-4 cubes; <c>WorldRoot[0]</c> covers [0,4). A
/// grid-4 selection at (0,0,0) resolves (via <c>lookupcube</c> with tsize=grid)
/// to that cube.
/// </summary>
public class OctaOpsTest : GoTestBase
{
    public OctaOpsTest(Node testScene) : base(testScene) { }

    private static readonly Block3 Cell0x4 = new(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), 4, 0);

    [Test]
    public void GetMaterialReturnsLeafMaterial()
    {
        var c = new Cube { Material = Octa.MAT_WATER };
        TestAssert.Equal(Octa.MAT_WATER, OctaEdit.GetMaterial(c), "leaf material");
    }

    [Test]
    public void GetMaterialReturnsAirWhenChildrenDiffer()
    {
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        Cube root = w.WorldRoot[0];
        for (int i = 0; i < 8; i++) root.Children![i].Material = Octa.MAT_WATER;
        root.Children![3].Material = Octa.MAT_LAVA;
        TestAssert.Equal(Octa.MAT_AIR, OctaEdit.GetMaterial(root), "differing children -> MAT_AIR");
    }

    [Test]
    public void GetMaterialReturnsCommonChildMaterial()
    {
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        Cube root = w.WorldRoot[0];
        for (int i = 0; i < 8; i++) root.Children![i].Material = Octa.MAT_GLASS;
        TestAssert.Equal(Octa.MAT_GLASS, OctaEdit.GetMaterial(root), "uniform children -> that material");
    }

    [Test]
    public void DeleteCubeMatchesEngine()
    {
        // octaprobe: OP delcube empty=1 tex0=3 (texture preserved).
        var w = new Octa(3);
        Cube target = w.WorldRoot[0];
        for (int l = 0; l < 6; l++) target.Texture[l] = 3;

        OctaEdit.DeleteCube(w, Cell0x4);

        TestAssert.True(target.IsEmpty(), "selected cube emptied");
        TestAssert.Equal(3, target.Texture[0], "texture preserved by discardchildren");
    }

    [Test]
    public void DeleteCubeCollapsesChildren()
    {
        var w = new Octa(3);
        Cube target = w.WorldRoot[0];
        OctaEdit.SubdivideCube(w, target);
        TestAssert.True(target.Children is not null, "precondition: target has children");

        OctaEdit.DeleteCube(w, Cell0x4);

        TestAssert.True(target.Children is null, "children discarded");
        TestAssert.True(target.IsEmpty(), "now empty");
    }

    [Test]
    public void FillDownMatchesEngine()
    {
        // octaprobe: OP fill solid=1 tex0=9 mat=0. A mode-1 fill with dir<0 pulls
        // the slice solid and copies the backing cell's textures.
        //
        // Set up two vertically adjacent cells inside a subdivided size-4 cube:
        // lower = children[0] (origin 0,0,0), upper = children[4] (+Z, origin 0,0,2).
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        Cube root = w.WorldRoot[0];
        Cube lower = root.Children![0];   // size 2, covers [0,2)^3
        Cube upper = root.Children![4];   // size 2, covers z [2,4)

        lower.EmptyFaces();
        for (int l = 0; l < 6; l++) upper.Texture[l] = 9;
        upper.EmptyFaces();

        // Selection: one grid cell at the upper cell's origin, grid 2, orient Bottom.
        var sel = new Selection(new Vector3I(0, 0, 2), new Vector3I(1, 1, 1), 2, (int)Orient.Bottom);
        OctaEdit.Fill(w, sel, dir: -1, mode: 1);

        TestAssert.True(lower.IsEntirelySolid(), "slice pulled solid");
        TestAssert.Equal(9, lower.Texture[0], "textures copied from the backing cell");
    }

    [Test]
    public void FillUpCarves()
    {
        // octaprobe (fillprobe): orient Bottom, grid 2, origin (0,0,0) -> the carved
        // slice is at z=0 (the lower cell); the origin then advances to (0,0,2).
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        Cube lower = w.WorldRoot[0].Children![0];
        TestAssert.True(lower.IsEntirelySolid(), "precondition: lower cell solid");

        var sel = new Selection(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), 2, (int)Orient.Bottom);
        OctaEdit.Fill(w, sel, dir: 1, mode: 1);

        TestAssert.True(lower.IsEmpty(), "positive-slice carved empty");
        TestAssert.Equal(new Vector3I(0, 0, 2), sel.O, "origin advanced past the carved slice");
    }

    [Test]
    public void BlockCopyCapturesSelection()
    {
        var w = new Octa(3);
        Cube target = w.WorldRoot[0];
        target.Material = Octa.MAT_WATER;
        for (int l = 0; l < 6; l++) target.Texture[l] = 7;

        var block = OctaEdit.BlockCopy(w, Cell0x4, 4);

        TestAssert.Equal(1, block.Cubes.Length, "one cell captured");
        TestAssert.Equal(Octa.MAT_WATER, block.Cubes[0].Material, "material captured");
        TestAssert.Equal(7, block.Cubes[0].Texture[0], "texture captured");
        TestAssert.Equal(new Vector3I(1, 1, 1), block.Descriptor.S, "descriptor size retained");
    }

    [Test]
    public void BlockCopyIsDeepCopy()
    {
        var w = new Octa(3);
        Cube target = w.WorldRoot[0];
        OctaEdit.SubdivideCube(w, target);

        var block = OctaEdit.BlockCopy(w, Cell0x4, 4);

        TestAssert.True(block.Cubes[0].Children is not null, "subtree captured");
        block.Cubes[0].Children![2].EmptyFaces();
        TestAssert.True(target.Children![2].IsEntirelySolid(), "capture independent of world");
    }

    [Test]
    public void PasteBlockRoundTripsChecksum()
    {
        // Copy a cell, delete it, paste it back: the world checksum must return to
        // the original.
        var w = new Octa(3);
        Cube target = w.WorldRoot[0];
        target.Material = Octa.MAT_LAVA;
        for (int l = 0; l < 6; l++) target.Texture[l] = 11;

        ulong before = OgzGeometry.Compute(w.WorldRoot).Checksum;
        var block = OctaEdit.BlockCopy(w, Cell0x4, 4);
        OctaEdit.DeleteCube(w, Cell0x4);

        OctaEdit.PasteBlock(w, block, Cell0x4, 4);

        ulong after = OgzGeometry.Compute(w.WorldRoot).Checksum;
        TestAssert.Equal(before, after, "copy -> delete -> paste restores checksum");
    }

    [Test]
    public void PasteBlockIsTransparent()
    {
        // An all-empty source block must not clear a solid destination.
        var w = new Octa(3);
        Cube src = w.WorldRoot[0];
        src.EmptyFaces();
        src.Material = Octa.MAT_AIR;

        var block = OctaEdit.BlockCopy(w, Cell0x4, 4);

        // Destination WorldRoot[1] is solid. Select it (size-4 cube at (4,0,0)).
        var dst = new Block3(new Vector3I(4, 0, 0), new Vector3I(1, 1, 1), 4, 0);
        OctaEdit.PasteBlock(w, block, dst, 4);

        TestAssert.True(w.WorldRoot[1].IsEntirelySolid(), "empty source leaves destination solid");
    }

    [Test]
    public void PasteBlockOverwritesDestination()
    {
        var w = new Octa(3);
        Cube src = w.WorldRoot[0];
        src.Material = Octa.MAT_WATER;
        for (int l = 0; l < 6; l++) src.Texture[l] = 4;

        var block = OctaEdit.BlockCopy(w, Cell0x4, 4);

        var dst = new Block3(new Vector3I(4, 0, 0), new Vector3I(1, 1, 1), 4, 0);
        OctaEdit.PasteBlock(w, block, dst, 4);

        TestAssert.Equal(Octa.MAT_WATER, w.WorldRoot[1].Material, "destination material replaced");
        TestAssert.Equal(4, w.WorldRoot[1].Texture[0], "destination texture replaced");
    }

    [Test]
    public void ForEachSelCubeVisitsEveryCell()
    {
        var w = new Octa(4);
        var sel = new Block3(new Vector3I(0, 0, 0), new Vector3I(2, 2, 2), 4, 0);
        int count = 0;
        OctaEdit.ForEachSelCube(w, sel, 4, (x, y, z, c) =>
        {
            TestAssert.True(c is not null, "cell resolves");
            count++;
        });
        TestAssert.Equal(8, count, "2x2x2 cells visited");
    }
}
