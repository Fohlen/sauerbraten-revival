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

        foreach (var surface in surfaces) surface.Mesh.Free();
    }

    /// <summary>
    /// Regression for the bt_falls "waterfall through the level" artifact: a
    /// material surface face must only ever border air (or a different volume) —
    /// never solid, never its own volume, never outside the world. This mirrors
    /// engine/octa.cpp visibleface(); the old conservative cull let a large water
    /// cube keep its vertical faces over finer terrain, so the whole basin drew as
    /// a closed box.
    /// </summary>
    [Test]
    public void MaterialFacesStopAtSolidAndWorldEdge()
    {
        var octa = TestData.ReadMap("bt_falls").ToOcta();
        var faces = new List<CubeFace>();
        var surfaces = MaterialMesher.Build(octa, faces);

        TestAssert.True(faces.Count > 0, "bt_falls emits material faces");
        foreach (var f in faces)
        {
            ushort volume = (ushort)(f.Cube.Material & Octa.MATF_VOLUME);
            int dim = f.Orient >> 1;
            int dir = (f.Orient & 1) == 0 ? -1 : 1;
            int ncoord = f.Co[dim] + dir * f.Size;
            TestAssert.False(ncoord < 0 || ncoord >= octa.WorldSize,
                $"material face at {f.Co} (orient {f.Orient}) faces out of world");

            var centre = f.Co;
            centre[dim] = ncoord + f.Size / 2;
            centre[(dim + 1) % 3] = f.Co[(dim + 1) % 3] + f.Size / 2;
            centre[(dim + 2) % 3] = f.Co[(dim + 2) % 3] + f.Size / 2;
            var nb = octa.LeafCube(centre);

            TestAssert.True(nb != null, $"material face at {f.Co} has no neighbour");
            TestAssert.False(nb!.IsEntirelySolid(), $"material face at {f.Co} borders solid");
            TestAssert.True((nb.Material & Octa.MATF_VOLUME) != volume,
                $"material face at {f.Co} borders the same volume");
        }

        foreach (var surface in surfaces) surface.Mesh.Free();
    }
}
