using System.Collections.Generic;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies empty-space material surface meshing (water/lava/glass). bt_falls is
/// the fixture: it has water.
/// </summary>
public class MaterialSurfaceTest : TestClass
{
    public MaterialSurfaceTest(Node testScene) : base(testScene) { }

    [Test]
    public void MeshesBtFallsWater()
    {
        var octa = TestData.ReadMap("bt_falls").ToOcta();
        var surfaces = MaterialMesher.Build(octa);

        TestAssert.True(surfaces.Count > 0, "bt_falls has material surfaces");
        int water = surfaces.Count(s => (s.Material & Octa.MATF_VOLUME) == Octa.MAT_WATER);
        TestAssert.True(water > 0, "which include water");

        long triangles = surfaces.Sum(s => MeshAssert.Triangles(s.Mesh));
        GD.Print($"[material] bt_falls: {surfaces.Count} material meshes, {water} water, {triangles} triangles");
        TestAssert.True(triangles > 0, "material surfaces have geometry");
    }

    [Test]
    public void HidesWaterFacesAgainstGlassAndSolidNeighbours()
    {
        var glassOcta = new Octa(4);
        var water = glassOcta.WorldRoot[0];
        water.Material = Octa.MAT_WATER;
        water.EmptyFaces();

        var glass = glassOcta.WorldRoot[1];
        glass.Material = Octa.MAT_GLASS;
        glass.EmptyFaces();

        TestAssert.True(!OctaFaceWalker.FaceVisible(glassOcta, water, 1, Vector3I.Zero, 8, true),
            "water is hidden against an adjacent glass cube");

        var solidOcta = new Octa(4);
        var solidWater = solidOcta.WorldRoot[0];
        solidWater.Material = Octa.MAT_WATER;
        solidWater.EmptyFaces();

        var solid = solidOcta.WorldRoot[1];
        solid.Material = Octa.MAT_AIR;
        solid.SolidFaces();

        TestAssert.True(!OctaFaceWalker.FaceVisible(solidOcta, solidWater, 1, Vector3I.Zero, 8, true),
            "water is hidden against an adjacent solid cube");
    }

}
