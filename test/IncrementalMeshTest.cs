using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Exercises incremental chunk meshing (M2): per-chunk rebuilds, dirty tracking,
/// and the per-frame budget. The key invariant is that an incrementally-rebuilt
/// world is identical to a freshly built one.
///
/// Kept deliberately small: tests use a synthetic world or a coarse chunk size so
/// the suite stays fast. church51's final mesh is ~107k triangles (a ~660 ms full
/// build); building it more than once or twice per suite is the difference between
/// seconds and minutes.
/// </summary>
public class IncrementalMeshTest : GoTestBase
{
    public IncrementalMeshTest(Node testScene) : base(testScene) { }

    /// <summary>A small subdivided world with one distinct cuboid of solid geometry.</summary>
    private static Octa SmallWorld(int worldscale = 6)
    {
        var w = new Octa(worldscale);
        // Carve a hollow: empty the root cube at (0,0,0) region to create faces.
        var sel = new Block3(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), 8, 0);
        OctaEdit.DeleteCube(w, sel);
        return w;
    }

    private static int Triangles(ArrayMesh mesh)
    {
        int tris = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var indices = mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Index].AsInt32Array();
            tris += indices.Length / 3;
        }
        return tris;
    }

    [Test]
    public void BuildChunkMatchesBuildChunked()
    {
        // Every chunk from BuildChunk(origin) must equal the whole-world pass.
        var octa = SmallWorld();
        var mesher = new OctaMesher(octa);
        const int chunkSize = 16;
        var full = mesher.BuildChunked(chunkSize);

        long sum = 0;
        foreach (var chunk in full.Chunks)
        {
            var single = mesher.BuildChunk(chunk.Origin, chunkSize);
            TestAssert.Equal(Triangles(chunk.Mesh), Triangles(single), $"chunk {chunk.Origin} triangle count");
            sum += Triangles(single);
        }
        TestAssert.Equal(full.TriangleCount, sum, "per-chunk triangles sum to the whole");
    }

    [Test]
    public void RendererInitialBuildMatchesWholeWorld()
    {
        var octa = SmallWorld();
        var renderer = new WorldChunkRenderer { ChunkSize = 16 };
        renderer.Setup(octa, materials: null);

        var expected = new OctaMesher(octa).BuildChunked(16);
        TestAssert.Equal(expected.Chunks.Count, renderer.ChunkCount, "chunk node count");
        TestAssert.Equal(expected.TriangleCount, renderer.TriangleCount, "total triangles");
        renderer.Free();
    }

    [Test]
    public void IncrementalRebuildEqualsFreshBuild()
    {
        // Mutate, mark the affected region, rebuild incrementally, compare to fresh.
        var octa = SmallWorld();
        var renderer = new WorldChunkRenderer { ChunkSize = 16 };
        renderer.Setup(octa, materials: null);

        var sel = new Block3(new Vector3I(8, 0, 0), new Vector3I(1, 1, 1), 8, 0);
        OctaEdit.DeleteCube(octa, sel);
        renderer.MarkDirty(new Aabb(new Vector3(8, 0, 0), new Vector3(8, 8, 8)), margin: 8);
        renderer.FlushDirty();

        var fresh = new OctaMesher(octa).BuildChunked(16);
        TestAssert.Equal(fresh.TriangleCount, renderer.TriangleCount, "incremental == fresh total triangles");

        long liveSum = 0;
        foreach (var child in renderer.GetChildren())
            if (child is MeshInstance3D m && m.Mesh is ArrayMesh am) liveSum += Triangles(am);
        TestAssert.Equal(fresh.TriangleCount, liveSum, "live chunk triangles match fresh build");
        renderer.Free();
    }

    [Test]
    public void MarkDirtyMarksOverlappingChunks()
    {
        var renderer = new WorldChunkRenderer { ChunkSize = 512 };
        renderer.Setup(new Octa(10), materials: null);

        renderer.MarkDirty(new Aabb(new Vector3(10, 10, 10), new Vector3(8, 8, 8)));
        TestAssert.Equal(1, renderer.PendingCount, "one chunk marked");

        // A region starting before the 512 boundary and ending after it touches
        // two chunks on X (and one each on Y/Z) -> 2 chunks total.
        renderer.MarkDirty(new Aabb(new Vector3(500, 10, 10), new Vector3(24, 8, 8)));
        TestAssert.Equal(2, renderer.PendingCount, "boundary region marks both X chunks");
        renderer.Free();
    }

    [Test]
    public void MarkDirtyClampsToWorld()
    {
        var renderer = new WorldChunkRenderer { ChunkSize = 512 };
        renderer.Setup(new Octa(10), materials: null); // worldsize 1024

        // A region far outside the world marks nothing.
        renderer.MarkDirty(new Aabb(new Vector3(-100000, -100000, -100000), new Vector3(8, 8, 8)));
        TestAssert.Equal(0, renderer.PendingCount, "out-of-world region marks nothing");
        renderer.Free();
    }

    [Test]
    public void BudgetLimitsRebuildsPerFrame()
    {
        var octa = SmallWorld(8); // worldsize 256
        var renderer = new WorldChunkRenderer { ChunkSize = 64, BudgetPerFrame = 2 };
        renderer.Setup(octa, materials: null);

        // Dirty a region spanning several chunks (but far smaller than the world),
        // so the pending set is small and the test stays fast.
        renderer.MarkDirty(new Aabb(new Vector3(0, 0, 0), new Vector3(160, 160, 160)), margin: 8);
        int pending = renderer.PendingCount;
        TestAssert.True(pending > 2, "several chunks dirtied");

        renderer._Process(0.016);
        TestAssert.Equal(pending - 2, renderer.PendingCount, "budget spent exactly 2 rebuilds");
        TestAssert.Equal(2, renderer.RebuildCount, "rebuild count tracks the budget");
        renderer.Free();
    }

    [Test]
    public void RebuildIsIdempotent()
    {
        var octa = SmallWorld();
        var renderer = new WorldChunkRenderer { ChunkSize = 16 };
        renderer.Setup(octa, materials: null);
        long before = renderer.TriangleCount;

        foreach (var chunk in new OctaMesher(octa).BuildChunked(16).Chunks)
            renderer.RebuildChunk(chunk.Origin);

        TestAssert.Equal(before, renderer.TriangleCount, "rebuild without mutation is a no-op");
        renderer.Free();
    }
}
