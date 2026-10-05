using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Ported from tools/worldtests/Program.cs — covers the octree primitives in
/// <see cref="Octa"/> against src/engine/octa.h + octa.cpp.
/// </summary>
public class OctaTest : TestClass
{
    public OctaTest(Node testScene) : base(testScene) { }

    [Test]
    public void OctaStep()
    {
        TestAssert.Equal(0, Octa.OctaStep(0, 0, 0, 0), "origin");
        TestAssert.Equal(1, Octa.OctaStep(1, 0, 0, 0), "+x");
        TestAssert.Equal(2, Octa.OctaStep(0, 1, 0, 0), "+y");
        TestAssert.Equal(4, Octa.OctaStep(0, 0, 1, 0), "+z");
        TestAssert.Equal(7, Octa.OctaStep(1, 1, 1, 0), "+++");
    }

    [Test]
    public void SolidAndEmptyFaces()
    {
        var c = new Cube();
        c.SolidFaces();
        TestAssert.True(c.IsEntirelySolid(), "SolidFaces -> IsEntirelySolid");
        TestAssert.True(!c.IsEmpty(), "solid is not empty");
        c.EmptyFaces();
        TestAssert.True(c.IsEmpty(), "EmptyFaces -> IsEmpty");
    }

    [Test]
    public void EdgeHelpers()
    {
        byte e = Cube.EdgeMake(3, 7);
        TestAssert.True(Cube.EdgeGet(e, 0) == 3 && Cube.EdgeGet(e, 1) == 7, "EdgeMake/EdgeGet");

        byte ed = 0;
        Cube.EdgeSet(ref ed, 0, 5);
        TestAssert.Equal(5, Cube.EdgeGet(ed, 0), "EdgeSet low nibble");
        Cube.EdgeSet(ref ed, 1, 9);
        TestAssert.Equal(9, Cube.EdgeGet(ed, 1), "EdgeSet high nibble");
    }

    [Test]
    public void CubeEdgeRoundtrip()
    {
        var c = new Cube();
        c.SetCubeEdge(0, 1, 1, 0xAB);
        TestAssert.Equal(0xAB, c.CubeEdge(0, 1, 1), "CubeEdge/SetCubeEdge roundtrip");
    }

    [Test]
    public void NewWorldAllocation()
    {
        var w = new Octa(3);
        TestAssert.Equal(8, w.WorldSize, "WorldSize == 8 at scale 3");
        TestAssert.Equal(1, w.AllocNodes, "NewMap allocates one node");
        TestAssert.Equal(8, w.WorldRoot.Length, "root node has 8 cubes");
        TestAssert.True(w.WorldRoot[0].IsEntirelySolid(), "root cube is solid");
        TestAssert.Equal(Octa.DEFAULT_GEOM, w.WorldRoot[0].Texture[0], "default geom texture");
        TestAssert.Equal((int)Octa.MAT_AIR, w.LookupMaterial(new Vector3(1, 1, 1)), "LookupMaterial -> MAT_AIR");
        TestAssert.True(!w.InsideWorld(new Vector3I(-1, 0, 0)), "outside world (-1,0,0)");
        TestAssert.True(w.InsideWorld(new Vector3I(7, 7, 7)), "inside world (7,7,7)");
        TestAssert.True(w.LeafCube(new Vector3I(7, 7, 7)) is not null, "LeafCube inside world");
    }
}
