using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Meshing;

/// <summary>
/// Owns the world's chunk <see cref="MeshInstance3D"/> nodes and performs
/// **incremental** re-meshing: after an octree mutation, only the chunks that
/// overlap the change are rebuilt, and at most <see cref="BudgetPerFrame"/> of
/// them per frame, spreading large edits across frames to avoid hitches.
///
/// This is the render-side counterpart of the command system
/// (docs/editor-and-commands.md §8): a mutation calls <see cref="MarkDirty"/>,
/// and this node does the bounded rebuild. It sits under the Z-up world rotation
/// like the rest of the geometry, so chunk origins are in Sauerbraten coordinates.
///
/// Invalidation is conservative: a changed cube can affect the culling of its
/// neighbours' faces (which live in *their* chunks) and, being larger than a chunk
/// potentially, can spill faces far from its origin. <see cref="MarkDirty(Aabb)"/>
/// therefore marks every chunk the AABB overlaps, expanded by the region's own
/// extent on each side — a superset of the chunks that can change. Over-marking is
/// safe (a rebuild is idempotent); under-marking leaves stale geometry.
/// </summary>
public partial class WorldChunkRenderer : Node3D
{
    /// <summary>
    /// Chunk edge length in world units (must be a power of two).
    ///
    /// Measured on church51 (107,356 triangles in the final mesh): a full rebuild
    /// is ~660 ms and is independent of chunk size, but *per-chunk* rebuild cost
    /// falls sharply with smaller chunks — ~108 ms at 512, ~46 ms at 256,
    /// **~1.4 ms at 128**. Small chunks are what make incremental rebuilds fit in a
    /// frame. 128 is the default for that reason; it costs more chunk nodes
    /// (91 vs 8 on church51).
    /// </summary>
    [Export] public int ChunkSize { get; set; } = 128;

    /// <summary>
    /// Maximum chunks rebuilt per frame. With 128-unit chunks (~1.4 ms each on
    /// church51), a budget of 4 keeps worst-case meshing under ~6 ms/frame. This is
    /// coupled to <see cref="ChunkSize"/>: large chunks make even one rebuild too
    /// slow to do several per frame.
    /// </summary>
    [Export] public int BudgetPerFrame { get; set; } = 4;

    /// <summary>
    /// Optional radiance volume to invalidate and tick incrementally alongside geometry chunks.
    /// </summary>
    public Lighting.RadianceVolume? Radiance { get; set; }

    /// <summary>
    /// Fallback material applied to chunk meshes when materials is null.
    /// </summary>
    public Material? FallbackMaterial { get; set; }

    private Octa _octa = null!;
    private MaterialSet? _materials;
    private readonly Dictionary<Vector3I, MeshInstance3D> _nodes = new();
    private readonly Dictionary<Vector3I, int> _chunkTriangles = new();
    private readonly List<Vector3I> _dirty = new();
    private readonly HashSet<Vector3I> _dirtySet = new();

    /// <summary>Chunks rebuilt so far (diagnostics/tests).</summary>
    public int RebuildCount { get; private set; }

    /// <summary>Chunks currently pending a rebuild.</summary>
    public int PendingCount => _dirty.Count;

    /// <summary>Number of live chunk nodes.</summary>
    public int ChunkCount => _nodes.Count;

    /// <summary>Total triangles across all live chunks.</summary>
    public long TriangleCount { get; private set; }

    /// <summary>
    /// Initialise against a world and build every chunk once. Call before adding
    /// mutations.
    /// </summary>
    public void Setup(Octa octa, MaterialSet? materials) => Setup(octa, materials, prebuilt: null);

    /// <summary>
    /// Initialise against a world and populate its chunks. When
    /// <paramref name="prebuilt"/> is supplied its meshes are reused verbatim
    /// instead of re-meshing — a warm-start for callers that already hold the
    /// meshes for an identical world (e.g. a cached test seed). Meshes are Godot
    /// resources shared read-only across instances, so reuse is safe.
    /// </summary>
    public void Setup(Octa octa, MaterialSet? materials, ChunkedWorldMesh? prebuilt)
    {
        _octa = octa;
        _materials = materials; _nodes.Clear();
        _chunkTriangles.Clear();
        _dirty.Clear();
        _dirtySet.Clear();
        foreach (var child in GetChildren())
            if (child is MeshInstance3D m) m.QueueFree();

        if (prebuilt != null)
        {
            foreach (var chunk in prebuilt.Chunks) CreateNode(chunk.Origin, chunk.Mesh);
        }
        else
        {
            var mesher = new OctaMesher(_octa, _materials);
            var chunked = mesher.BuildChunked(ChunkSize);
            foreach (var chunk in chunked.Chunks) CreateNode(chunk.Origin, chunk.Mesh);
        }
        RecomputeTriangleCount();
    }
    /// <summary>
    /// Mark every chunk overlapping <paramref name="region"/> dirty. Pass
    /// <paramref name="margin"/> = the edited cube size so the chunks owning
    /// neighbouring faces (whose culling the change may have affected) are
    /// included too. The region is clamped to the world, so a caller cannot
    /// accidentally dirty chunks that do not exist.
    /// </summary>
    public void MarkDirty(Aabb region, float margin = 0f)
    {
        Radiance?.MarkDirty(region, margin);

        if (margin > 0f) region = region.Grow(margin);
        int c = ChunkSize;
        int worldMax = Mathf.CeilToInt((float)_octa.WorldSize);
        int x0 = Mathf.Max(0, Mathf.FloorToInt(region.Position.X / c));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(region.Position.Y / c));
        int z0 = Mathf.Max(0, Mathf.FloorToInt(region.Position.Z / c));
        int x1 = Mathf.Min((worldMax - 1) / c, Mathf.FloorToInt(region.End.X / c));
        int y1 = Mathf.Min((worldMax - 1) / c, Mathf.FloorToInt(region.End.Y / c));
        int z1 = Mathf.Min((worldMax - 1) / c, Mathf.FloorToInt(region.End.Z / c));
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    MarkChunkDirty(new Vector3I(x * c, y * c, z * c));
    }

    /// <summary>Mark a single chunk (by its origin) dirty.</summary>
    public void MarkChunkDirty(Vector3I origin)
    {
        if (_dirtySet.Add(origin)) _dirty.Add(origin);
    }

    /// <summary>Rebuild a specific chunk immediately (used by tests and first build).</summary>
    public void RebuildChunk(Vector3I origin)
    {
        var mesher = new OctaMesher(_octa, _materials);
        var mesh = mesher.BuildChunk(origin, ChunkSize);
        int tris = CountTriangles(mesh);
        int old = _chunkTriangles.TryGetValue(origin, out int prev) ? prev : 0;

        if (mesh.GetSurfaceCount() == 0)
        {
            if (_nodes.TryGetValue(origin, out var existing))
            {
                existing.QueueFree();
                _nodes.Remove(origin);
            }
            _chunkTriangles.Remove(origin);
        }
        else if (_nodes.TryGetValue(origin, out var node))
        {
            node.Mesh = mesh;
            _chunkTriangles[origin] = tris;
        }
        else
        {
            CreateNode(origin, mesh); // sets _chunkTriangles[origin] = tris
        }

        TriangleCount += tris - old;
    }

    private static int CountTriangles(ArrayMesh mesh)
    {
        int tris = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var indices = mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Index].AsInt32Array();
            tris += indices.Length / 3;
        }
        return tris;
    }

    private void RecomputeTriangleCount()
    {
        TriangleCount = 0;
        foreach (var kv in _chunkTriangles) TriangleCount += kv.Value;
    }

    public override void _Process(double delta)
    {
        Radiance?.Process(delta);

        if (_dirty.Count == 0) return;
        int n = Mathf.Min(BudgetPerFrame, _dirty.Count);
        for (int i = 0; i < n; i++)
        {
            var origin = _dirty[0];
            _dirty.RemoveAt(0);
            _dirtySet.Remove(origin);
            RebuildChunk(origin);
            RebuildCount++;
        }
    }

    /// <summary>Flush all pending chunks immediately (tests, or a save/export).</summary>
    public void FlushDirty()
    {
        Radiance?.FlushDirty();

        while (_dirty.Count > 0)
        {
            var origin = _dirty[0];
            _dirty.RemoveAt(0);
            _dirtySet.Remove(origin);
            RebuildChunk(origin);
            RebuildCount++;
        }
    }

    /// <summary>Rebuild every chunk with the same world and materials (after a full-world change).</summary>
    public void ReSetup()
    {
        if (_octa == null) return;
        Setup(_octa, _materials);
    }

    private void CreateNode(Vector3I origin, ArrayMesh mesh)
    {
        var node = new MeshInstance3D
        {
            Name = $"Chunk_{origin.X}_{origin.Y}_{origin.Z}",
            Mesh = mesh,
            Position = new Vector3(origin.X, origin.Y, origin.Z),
            MaterialOverride = FallbackMaterial,
        };
        _nodes[origin] = node;
        _chunkTriangles[origin] = CountTriangles(mesh);
        AddChild(node);
    }
}
