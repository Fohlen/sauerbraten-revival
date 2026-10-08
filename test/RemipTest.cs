using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers <c>remip</c> (octa.cpp:463) and <see cref="OctaEdit.RemipAll"/>. Expected
/// values are the original engine's exact output from <c>tools/octaprobe</c> (which
/// links the engine's <c>remip</c> directly).
///
/// remip is a pure deterministic function of <c>(octree, mipvis)</c> — see
/// docs/editor-and-commands.md §5.4 for why that lets it replicate pass-through.
/// </summary>
public class RemipTest : GoTestBase
{
    public RemipTest(Node testScene) : base(testScene) { }

    /// <summary>Run remip over the whole world the way mpremip does.</summary>
    private static void RemipAll(Octa w, bool mipvis = false) => OctaEdit.RemipAll(w, mipvis);

    private static (int Before, int After, bool HasChildren) Run(Octa w, Cube root)
    {
        int before = Octa.FamilySize(root);
        RemipAll(w);
        return (before, Octa.FamilySize(root), root.Children != null);
    }

    [Test]
    public void CollapsesConsistentSolidChildren()
    {
        // octaprobe: remip collapse before=9 after=1 children=0
        var w = new Octa(4);
        Cube root = w.WorldRoot[0];
        OctaEdit.SubdivideCube(w, root);
        for (int i = 0; i < 8; i++)
        {
            root.Children![i].SolidFaces();
            for (int l = 0; l < 6; l++) root.Children[i].Texture[l] = 1;
            root.Children[i].Children = null;
        }

        var (before, after, hasChildren) = Run(w, root);
        TestAssert.Equal(9, before, "before: one node + eight children");
        TestAssert.Equal(1, after, "after: collapsed to the node");
        TestAssert.True(!hasChildren, "no children remain");
    }

    [Test]
    public void CollapsesSolidChildrenEvenWithDifferingTextures()
    {
        // octaprobe: remip keep before=9 after=1. Differing textures on *solid*
        // cubes are invisible, so remip still merges them.
        var w = new Octa(4);
        Cube root = w.WorldRoot[0];
        OctaEdit.SubdivideCube(w, root);
        for (int i = 0; i < 8; i++)
        {
            root.Children![i].SolidFaces();
            for (int l = 0; l < 6; l++) root.Children[i].Texture[l] = (ushort)(i == 0 ? 7 : 1);
        }

        var (before, after, hasChildren) = Run(w, root);
        TestAssert.Equal(9, before, "before");
        TestAssert.Equal(1, after, "still merges (textures invisible)");
        TestAssert.True(!hasChildren, "no children remain");
    }

    [Test]
    public void KeepsDeformedChildren()
    {
        // octaprobe: remip deformed before=9 after=9 children=1. A deformed child
        // changes the faces a fresh subdivision would produce, so remip refuses.
        var w = new Octa(4);
        Cube root = w.WorldRoot[0];
        OctaEdit.SubdivideCube(w, root);
        for (int i = 0; i < 8; i++)
        {
            root.Children![i].SolidFaces();
            for (int l = 0; l < 6; l++) root.Children[i].Texture[l] = 1;
        }
        root.Children![3].SetCubeEdge(0, 0, 0, 4);

        var (before, after, hasChildren) = Run(w, root);
        TestAssert.Equal(9, before, "before");
        TestAssert.Equal(9, after, "deformed child prevents collapse");
        TestAssert.True(hasChildren, "children retained");
    }

    [Test]
    public void CollapsesNestedConsistentSubtree()
    {
        // octaprobe: remip nested before=17 after=1 children=0. Both node levels
        // collapse when everything is consistent.
        var w = new Octa(4);
        Cube root = w.WorldRoot[0];
        OctaEdit.SubdivideCube(w, root);
        Cube[] rootChildren = root.Children!;
        for (int i = 0; i < 8; i++)
        {
            rootChildren[i].SolidFaces();
            for (int l = 0; l < 6; l++) rootChildren[i].Texture[l] = 1;
        }
        OctaEdit.SubdivideCube(w, rootChildren[0]);
        Cube[] nestedChildren = rootChildren[0].Children!;
        for (int i = 0; i < 8; i++)
        {
            nestedChildren[i].SolidFaces();
            for (int l = 0; l < 6; l++) nestedChildren[i].Texture[l] = 1;
        }

        var (before, after, hasChildren) = Run(w, root);
        TestAssert.Equal(17, before, "before: two node levels");
        TestAssert.Equal(1, after, "both levels collapsed");
        TestAssert.True(!hasChildren, "no children remain");
    }

    [Test]
    public void RemipIsIdempotent()
    {
        var w = new Octa(4);
        Cube root = w.WorldRoot[0];
        OctaEdit.SubdivideCube(w, root);
        for (int i = 0; i < 8; i++)
        {
            root.Children![i].SolidFaces();
            for (int l = 0; l < 6; l++) root.Children[i].Texture[l] = 1;
        }

        RemipAll(w);
        int once = Octa.FamilySize(root);
        RemipAll(w);
        TestAssert.Equal(once, Octa.FamilySize(root), "remip is idempotent");
    }

    [Test]
    public void RemipPreservesRenderedGeometry()
    {
        // Collapsing a node whose children are consistent must not change the
        // rendered mesh: the merged cube describes the same solid volume with the
        // same visible textures. Compare triangle counts before and after.
        var w = new Octa(4);
        Cube root = w.WorldRoot[0];
        OctaEdit.SubdivideCube(w, root);
        for (int i = 0; i < 8; i++)
        {
            root.Children![i].SolidFaces();
            for (int l = 0; l < 6; l++) root.Children[i].Texture[l] = 1;
            root.Children[i].Children = null;
        }

        // Make the region boundary interesting: carve one child empty so the
        // parent is not uniformly solid and faces are genuinely visible.
        root.Children![5].EmptyFaces();

        int before = new OctaMesher(w).BuildChunked(w.WorldSize).TriangleCount;
        RemipAll(w);
        int after = new OctaMesher(w).BuildChunked(w.WorldSize).TriangleCount;

        TestAssert.Equal(before, after, "remip does not change rendered geometry");
    }
}
