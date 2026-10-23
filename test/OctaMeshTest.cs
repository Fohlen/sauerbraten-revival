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
        // mesher gains fidelity (merges, T-joints, sky, ...).
        // 105 surfaces / 107356 triangles with visibletris + merges + T-joints
        // (87035 without T-joints, 326943 with visibletris only, 560314 unculled).
        TestAssert.Equal(105, surfaces, "surface count baseline");
        TestAssert.Equal(107356L, triangles, "triangle count baseline with T-joints");

        var unTJointMesh = new OctaMesher(octa) { FillTJoints = false }.Build();
        long unTJointTriangles = MeshAssert.Triangles(unTJointMesh);
        TestAssert.Equal(87035L, unTJointTriangles, "triangle count baseline without T-joints");
    }

    [Test]
    public void VisibleTrisMatchesEngineBakedSurfaces()
    {
        // For every unmerged face in church51 whose surfaceinfo was baked by the
        // original engine's calclight (which calls visibletris(c, orient, co, size)
        // in lightmap.cpp / worldio.cpp), our VisibleTris must produce the exact
        // same surviving vertex count (3 or 4) and 1/8-unit corner coordinates.
        var octa = TestData.ReadMap().ToOcta();
        int checkedFaces = 0, partialFaces = 0, mismatched = 0;

        OctaFaceWalker.Walk(octa,
            include: c => !c.IsEmpty(),
            hideSameMaterial: false,
            emit: face =>
            {
                if ((face.Cube.Merged & (1 << face.Orient)) != 0) return;
                if (face.Cube.Ext == null) return;
                var surf = face.Cube.Ext.Surfaces[face.Orient];
                int layerverts = surf.NumVerts & Octa.MAXFACEVERTS;
                if (layerverts == 0) return;

                checkedFaces++;
                if (layerverts == 3) partialFaces++;

                System.Span<Vector3I> v = stackalloc Vector3I[4];
                face.Cube.GenFaceVerts(face.Orient, v);
                int convex = face.Cube.FlatAxisFace(face.Orient) ? 0 : CubeGeometry.FaceConvexity(v);
                int order = (face.Vis & 4) != 0 || convex < 0 ? 1 : 0;
                var vo = new Vector3I((face.Co.X & 0xFFF) << 3, (face.Co.Y & 0xFFF) << 3, (face.Co.Z & 0xFFF) << 3);

                System.Span<Vector3I> actual = stackalloc Vector3I[4];
                int n = 0;
                actual[n++] = v[(order + 0) & 3] * face.Size + vo;
                if ((face.Vis & 1) != 0) actual[n++] = v[(order + 1) & 3] * face.Size + vo;
                actual[n++] = v[(order + 2) & 3] * face.Size + vo;
                if ((face.Vis & 2) != 0) actual[n++] = v[(order + 3) & 3] * face.Size + vo;

                if (n != layerverts) { mismatched++; return; }
                for (int k = 0; k < n; k++)
                {
                    var ev = face.Cube.Ext.Verts[surf.Verts + k];
                    if (actual[k].X != ev.X || actual[k].Y != ev.Y || actual[k].Z != ev.Z)
                    {
                        mismatched++;
                        break;
                    }
                }
            });

        GD.Print($"[visibletris] checked={checkedFaces} partial={partialFaces} mismatched={mismatched}");
        TestAssert.Equal(18811, checkedFaces, "checked all unmerged baked surfaces in church51");
        TestAssert.Equal(3484, partialFaces, "exercised single-triangle partial faces");
        TestAssert.Equal(0, mismatched, "VisibleTris matches engine-baked surface vertices on every face");
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
