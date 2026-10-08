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
}
