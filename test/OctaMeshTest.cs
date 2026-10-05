using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Exercises the Godot-native mesher: build an <see cref="ArrayMesh"/> from the
/// runtime octree and check its structure is sound.
/// </summary>
public class OctaMeshTest : TestClass
{
    public OctaMeshTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsGeometryOnlyMesh()
    {
        var map = TestData.ReadMap();
        var octa = map.ToOcta();
        var mesh = new OctaMesher(octa).Build();

        int surfaces = mesh.GetSurfaceCount();
        long triangles = 0, vertices = 0;
        float worldSize = octa.WorldSize;

        for (int s = 0; s < surfaces; s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();

            TestAssert.Equal(positions.Length, normals.Length, $"surface {s} normal count");
            TestAssert.Equal(0, indices.Length % 3, $"surface {s} indices are triangles");
            for (int i = 0; i < indices.Length; i++)
                TestAssert.True(indices[i] >= 0 && indices[i] < positions.Length, $"surface {s} index {i} in range");
            foreach (var p in positions)
                TestAssert.True(p.X >= 0 && p.X <= worldSize && p.Y >= 0 && p.Y <= worldSize && p.Z >= 0 && p.Z <= worldSize,
                    $"surface {s} position in world bounds");

            vertices += positions.Length;
            triangles += indices.Length / 3;
        }

        GD.Print($"[mesh] surfaces={surfaces} triangles={triangles} vertices={vertices}");
        TestAssert.True(surfaces > 0, "at least one surface");
        TestAssert.True(triangles > 0, "at least one triangle");

        // Regression baseline for the current geometry-only mesher; update when the
        // mesher gains fidelity (visibletris, merges, sky, material surfaces, ...).
        TestAssert.Equal(121, surfaces, "surface count baseline");
        TestAssert.Equal(686392L, triangles, "triangle count baseline");
    }
}
