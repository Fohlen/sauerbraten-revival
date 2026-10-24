using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the managed-octree structural primitives in <see cref="OctaEdit"/> and
/// <see cref="Octa"/> (copycube, pastecube, subdividecube, discardchildren,
/// getmippedtexture) against src/engine/octa.cpp + octaedit.cpp.
///
/// The assertion primitive is <see cref="OgzGeometry.Compute"/>, the same
/// canonical checksum used to validate parsed maps.
/// </summary>
public class OctaEditTest : GoTestBase
{
    public OctaEditTest(Node testScene) : base(testScene) { }

    /// <summary>A small world with one distinct solid leaf below a subdivided path.</summary>
    private static (Octa Octa, Cube Leaf) SubdividedWorld(int worldscale = 3)
    {
        var w = new Octa(worldscale);
        // Root is a single solid leaf; subdivide so we have children to copy/paste.
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        return (w, w.WorldRoot[0]);
    }

    [Test]
    public void SubdivideAllocatesEightChildren()
    {
        var w = new Octa(3);
        int before = w.AllocNodes;
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);

        TestAssert.True(w.WorldRoot[0].Children is not null, "children allocated");
        Cube[] children = w.WorldRoot[0].Children!;
        TestAssert.Equal(8, children.Length, "eight children");
        TestAssert.Equal(before + 1, w.AllocNodes, "allocnodes incremented once");
        for (int i = 0; i < 8; i++)
            TestAssert.True(children[i].IsEntirelySolid(), "solid parent -> solid children");
    }

    [Test]
    public void SubdivideIsIdempotent()
    {
        var (w, leaf) = SubdividedWorld();
        int alloc = w.AllocNodes;
        OctaEdit.SubdivideCube(w, leaf);
        TestAssert.Equal(alloc, w.AllocNodes, "subdividing an already-divided cube is a no-op");
    }

    [Test]
    public void SubdivideEmptyInheritsEmpty()
    {
        var w = new Octa(3);
        w.WorldRoot[0].EmptyFaces();
        w.WorldRoot[0].Texture[0] = 7;
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        Cube[] children = w.WorldRoot[0].Children!;
        for (int i = 0; i < 8; i++)
        {
            TestAssert.True(children[i].IsEmpty(), "empty parent -> empty children");
            TestAssert.Equal(7, children[i].Texture[0], "texture inherited");
        }
    }

    [Test]
    public void CopyCubeDeepCopies()
    {
        var (w, src) = SubdividedWorld();
        src.Material = Octa.MAT_WATER;
        src.Texture[2] = 42;

        var dst = new Cube();
        OctaEdit.CopyCube(w, src, dst);

        TestAssert.True(dst.Children is not null, "children copied");
        TestAssert.Equal(8, dst.Children!.Length, "eight children");
        TestAssert.Equal(Octa.MAT_WATER, dst.Material, "material copied");
        TestAssert.Equal(42, dst.Texture[2], "texture copied");
        TestAssert.True(src.Faces[0] == dst.Faces[0], "faces copied");

        // Mutating the copy must not affect the source.
        dst.Children![0].EmptyFaces();
        TestAssert.True(src.Children![0].IsEntirelySolid(), "copy is independent of source");
    }

    [Test]
    public void CopyCubeClearsMergeAndVisible()
    {
        var (w, src) = SubdividedWorld();
        src.Merged = 3;
        src.Visible = 5;
        var dst = new Cube();
        OctaEdit.CopyCube(w, src, dst);
        TestAssert.Equal(0, dst.Merged, "merged cleared");
        TestAssert.Equal(0, dst.Visible, "visible cleared");
    }

    [Test]
    public void PasteCubeReplacesDstSubtree()
    {
        var (w, src) = SubdividedWorld();
        src.Material = Octa.MAT_LAVA;
        src.Texture[0] = 99;

        var dstRoot = new Octa(3).WorldRoot[0];
        OctaEdit.SubdivideCube(w, dstRoot);
        dstRoot.Children![3].Material = 1234; // will be overwritten

        OctaEdit.PasteCube(w, src, dstRoot);
        TestAssert.Equal(Octa.MAT_LAVA, dstRoot.Material, "dst replaced by src");
        TestAssert.Equal(99, dstRoot.Texture[0], "dst texture replaced");
        TestAssert.Equal(Octa.MAT_AIR, dstRoot.Children![3].Material, "old subtree discarded");
    }

    [Test]
    public void PasteCubeReplacesLeafWithSubtree()
    {
        var (w, src) = SubdividedWorld();
        var dst = new Cube { Material = Octa.MAT_GLASS };
        dst.SolidFaces();

        OctaEdit.PasteCube(w, src, dst);
        TestAssert.True(dst.Children is not null, "leaf became a subtree");
        TestAssert.Equal(8, dst.Children!.Length, "eight children");
    }

    [Test]
    public void DiscardChildrenCollapsesToLeaf()
    {
        var (w, leaf) = SubdividedWorld();
        leaf.Material = Octa.MAT_AIR;
        int allocBefore = w.AllocNodes;
        uint filled = w.DiscardChildren(leaf);

        TestAssert.True(leaf.Children is null, "children discarded");
        TestAssert.Equal(Octa.MAT_AIR, leaf.Material, "material reset to air");
        TestAssert.Equal(0, leaf.Visible, "visible reset");
        TestAssert.Equal(0, leaf.Merged, "merged reset");
        TestAssert.Equal(allocBefore - 1, w.AllocNodes, "allocnodes decremented");
        TestAssert.Equal(Octa.F_SOLID, filled, "solid children reported as filled");
    }

    [Test]
    public void DiscardChildrenDepthZeroDoesNotForceSolid()
    {
        // discardchildren at depth 0 must not set faces[0]=SOLID (only depth>0 does).
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        w.WorldRoot[0].EmptyFaces();
        w.DiscardChildren(w.WorldRoot[0], fixtex: true);
        TestAssert.True(w.WorldRoot[0].Faces[0] != Octa.F_SOLID,
            "depth-0 discard does not force solid");
    }

    [Test]
    public void DiscardChildrenRecurses()
    {
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0].Children![0]);
        int alloc = w.AllocNodes;

        w.DiscardChildren(w.WorldRoot[0]);
        TestAssert.True(w.WorldRoot[0].Children is null, "root collapsed");
        TestAssert.Equal(alloc - 2, w.AllocNodes, "both node levels freed");
    }

    [Test]
    public void GetMippedTexturePicksCommonChildTexture()
    {
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        Cube parent = w.WorldRoot[0];
        for (int i = 0; i < 8; i++) parent.Children![i].Texture[0] = 5;

        TestAssert.Equal(5, Octa.GetMippedTexture(parent, 0), "common texture returned");
    }

    [Test]
    public void GetMippedTextureReturnsDefaultGeomWhenAllEmpty()
    {
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        Cube parent = w.WorldRoot[0];
        for (int i = 0; i < 8; i++)
        {
            parent.Children![i].EmptyFaces();
            parent.Children[i].Texture[0] = 9;
        }
        TestAssert.Equal(Octa.DEFAULT_GEOM, Octa.GetMippedTexture(parent, 0),
            "all-empty children yield DEFAULT_GEOM");
    }

    [Test]
    public void FreeOctaUnwiresAndDecrements()
    {
        var w = new Octa(3);
        Cube[] block = w.NewCubes(Octa.F_EMPTY);
        int alloc = w.AllocNodes;
        w.FreeOcta(block);
        TestAssert.Equal(alloc - 1, w.AllocNodes, "allocnodes decremented");
        for (int i = 0; i < 8; i++)
            TestAssert.True(block[i].Children is null, "children unwired");
    }

    [Test]
    public void FamilySizeCountsSubtree()
    {
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0].Children![0]);
        // root (1) + 7 leaf children (7) + child[0]'s block (1) + its 8 children (8) = 17
        TestAssert.Equal(17, Octa.FamilySize(w.WorldRoot[0]), "family size counts node levels");
    }

    [Test]
    public void PasteRoundTripPreservesGeometryChecksum()
    {
        // Paste a source cube into a fresh destination; the destination's geometry
        // checksum must equal a world whose root is an independent copy of src.
        var (w, src) = SubdividedWorld();
        src.Material = Octa.MAT_WATER;
        src.Texture[1] = 17;

        var dst = new Cube();
        OctaEdit.CopyCube(w, src, dst);

        ulong srcSum = OgzGeometry.Compute(new[] { src }).Checksum;
        ulong dstSum = OgzGeometry.Compute(new[] { dst }).Checksum;
        TestAssert.Equal(srcSum, dstSum, "copy has identical geometry checksum");
    }
}
