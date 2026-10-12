using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Exercises the Godot-native mesher: build an <see cref="ArrayMesh"/> from the
/// runtime octree and check its structure is sound.
/// </summary>
public class OctaMeshTest : GoTestBase
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
        // 118 surfaces: surfaces are grouped by texture vslot (previously 229 with
        // (texture, lmid) baked-lightmap groupings before R2 removal).
        TestAssert.Equal(118, surfaces, "surface count baseline");
        TestAssert.Equal(619448L, triangles, "triangle count baseline");
    }

    [Test]
    public void ChunkedMeshMatchesWholeMap()
    {
        var map = TestData.ReadMap();
        var octa = map.ToOcta();
        var mesher = new OctaMesher(octa);

        long wholeTriangles = MeshAssert.Triangles(mesher.Build());
        var chunked = mesher.BuildChunked(512);

        TestAssert.Equal(wholeTriangles, (long)chunked.TriangleCount, "chunked triangles == whole triangles");
        TestAssert.True(chunked.Chunks.Count > 1, "world split into multiple chunks");

        const int chunkSize = 512;
        float worldSize = octa.WorldSize;
        foreach (var chunk in chunked.Chunks)
        {
            TestAssert.Equal(0, chunk.Origin.X % chunkSize, $"chunk {chunk.Origin} x aligned");
            TestAssert.Equal(0, chunk.Origin.Y % chunkSize, $"chunk {chunk.Origin} y aligned");
            TestAssert.Equal(0, chunk.Origin.Z % chunkSize, $"chunk {chunk.Origin} z aligned");
            TestAssert.True(chunk.Mesh.GetSurfaceCount() > 0, $"chunk {chunk.Origin} has surfaces");

            for (int s = 0; s < chunk.Mesh.GetSurfaceCount(); s++)
            {
                var positions = chunk.Mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                foreach (var p in positions)
                    TestAssert.True(
                        p.X >= 0 && p.X <= worldSize && p.Y >= 0 && p.Y <= worldSize && p.Z >= 0 && p.Z <= worldSize,
                        $"chunk {chunk.Origin} position in world bounds");
            }
        }

        TestAssert.Equal(chunked.Chunks.Count, mesher.BuildChunked(512).Chunks.Count, "deterministic chunking");
        TestAssert.Equal(8, chunked.Chunks.Count, "chunk count baseline");
        GD.Print($"[mesh] chunks={chunked.Chunks.Count} chunkSurfaces={chunked.SurfaceCount} chunkTriangles={chunked.TriangleCount}");
    }
}
