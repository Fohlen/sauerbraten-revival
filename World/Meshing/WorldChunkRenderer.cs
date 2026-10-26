using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Meshing;

/// <summary>
/// Owns the world's chunk <see cref="RenderingServer"/> mesh and instance RIDs and
/// performs **incremental** re-meshing: after an octree mutation, only the chunks
/// that overlap the change are rebuilt, and at most <see cref="BudgetPerFrame"/> of
/// them per frame, spreading large edits across frames to avoid hitches.
///
/// Chunks stream directly to <see cref="RenderingServer"/> (<c>MeshCreate</c>,
/// <c>MeshClear</c>, <c>MeshAddSurfaceFromArrays</c>, <c>InstanceCreate</c>),
/// bypassing the Godot scene tree (<see cref="MeshInstance3D"/>) for world geometry.
///
/// This is the render-side counterpart of the command system
/// (docs/editor-and-commands.md §8): a mutation calls <see cref="MarkDirty"/>,
/// and this node does the bounded rebuild. It sits under the Z-up world rotation
/// like the rest of the geometry, so chunk origins are in Sauerbraten coordinates.
///
/// Invalidation is conservative: a changed cube can affect the culling of its
/// neighbours' faces (which live in *their* chunks) and, being larger than a chunk
/// potentially, can spill faces far from its origin. <see cref="MarkDirty(Aabb, float)"/>
/// therefore marks every chunk the AABB overlaps, expanded by the region's own
/// extent on each side — a superset of the chunks that can change. Over-marking is
/// safe (a rebuild is idempotent); under-marking leaves stale geometry.
/// </summary>
public partial class WorldChunkRenderer : Node3D
{
    private sealed class ChunkEntry
    {
        public Rid InstanceRid;
        public Rid OwnedMeshRid;
        public ArrayMesh? SharedMesh;
        public int Triangles;
        public Aabb LocalAabb;

        public Rid MeshRid => OwnedMeshRid.IsValid ? OwnedMeshRid : SharedMesh?.GetRid() ?? default;
    }

    /// <summary>
    /// Chunk edge length in world units (must be a power of two).
    ///
    /// Measured on church51 (107,356 triangles in the final mesh): a full rebuild
    /// is ~660 ms and is independent of chunk size, but *per-chunk* rebuild cost
    /// falls sharply with smaller chunks — ~108 ms at 512, ~46 ms at 256,
    /// **~1.4 ms at 128**. Small chunks are what make incremental rebuilds fit in a
    /// frame. 128 is the fallback default for that reason; it costs more chunk
    /// instances (91 vs 8 on church51). The current load paths override it with 512
    /// (MapWorld/MapLevel/OgzMapViewer); EmptyMap uses 128.
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

    private Material? _fallbackMaterial;

    /// <summary>
    /// Fallback material applied to chunk instances when materials is null.
    /// </summary>
    public Material? FallbackMaterial
    {
        get => _fallbackMaterial;
        set
        {
            _fallbackMaterial = value;
            Rid overrideRid = _fallbackMaterial?.GetRid() ?? default;
            foreach (var entry in _chunks.Values)
                RenderingServer.InstanceGeometrySetMaterialOverride(entry.InstanceRid, overrideRid);
        }
    }

    private Octa _octa = null!;
    private MaterialSet? _materials;
    private readonly Dictionary<Vector3I, ChunkEntry> _chunks = new();
    private readonly List<Vector3I> _dirty = new();
    private readonly HashSet<Vector3I> _dirtySet = new();

    public WorldChunkRenderer()
    {
        SetNotifyTransform(true);
    }

    /// <summary>Chunks rebuilt so far (diagnostics/tests).</summary>
    public int RebuildCount { get; private set; }

    /// <summary>Chunks currently pending a rebuild.</summary>
    public int PendingCount => _dirty.Count;

    /// <summary>Number of live chunk instances.</summary>
    public int ChunkCount => _chunks.Count;

    /// <summary>Total triangles across all live chunks.</summary>
    public long TriangleCount { get; private set; }

    /// <summary>Union of all live chunk bounding boxes in Sauerbraten (Z-up) coordinates.</summary>
    public Aabb Bounds { get; private set; } = new(Vector3.Zero, Vector3.One);

    /// <summary>Origins of all currently active non-empty chunks.</summary>
    public IReadOnlyCollection<Vector3I> ChunkOrigins => _chunks.Keys;

    /// <summary>Returns the <see cref="RenderingServer"/> visual instance RID for the chunk at <paramref name="origin"/>, or default if absent.</summary>
    public Rid GetChunkInstanceRid(Vector3I origin) =>
        _chunks.TryGetValue(origin, out var entry) ? entry.InstanceRid : default;

    /// <summary>Returns the <see cref="RenderingServer"/> mesh RID for the chunk at <paramref name="origin"/>, or default if absent.</summary>
    public Rid GetChunkMeshRid(Vector3I origin) =>
        _chunks.TryGetValue(origin, out var entry) ? entry.MeshRid : default;

    /// <summary>
    /// Initialise against a world and stream every chunk directly to <see cref="RenderingServer"/>.
    /// Call before adding mutations.
    /// </summary>
    public void Setup(Octa octa, MaterialSet? materials) => Setup(octa, materials, prebuilt: null);

    /// <summary>
    /// Initialise against a world and populate its chunks. When
    /// <paramref name="prebuilt"/> is supplied its meshes are bound directly to
    /// <see cref="RenderingServer"/> instances instead of re-meshing — a warm-start
    /// for callers that already hold the meshes for an identical world (e.g. a
    /// cached test seed). When a prebuilt chunk is later edited, it allocates its
    /// own mesh RID on demand so shared seed meshes are never mutated.
    /// </summary>
    public void Setup(Octa octa, MaterialSet? materials, ChunkedWorldMesh? prebuilt)
    {
        _octa = octa;
        _materials = materials;
        FreeAllChunks();
        _dirty.Clear();
        _dirtySet.Clear();

        if (prebuilt != null)
        {
            foreach (var chunk in prebuilt.Chunks)
                CreatePrebuiltChunk(chunk.Origin, chunk.Mesh);
        }
        else
        {
            var mesher = new OctaMesher(_octa, _materials);
            foreach (var (origin, builder) in mesher.BuildChunkedBuilders(ChunkSize))
                CreateStreamedChunk(origin, builder);
        }
        RecomputeTotals();
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

    /// <summary>Rebuild a specific chunk immediately and stream its surfaces to <see cref="RenderingServer"/>.</summary>
    public void RebuildChunk(Vector3I origin)
    {
        var mesher = new OctaMesher(_octa, _materials);
        var builder = mesher.BuildChunkBuilder(origin, ChunkSize);
        int tris = builder.TriangleCount;
        int old = _chunks.TryGetValue(origin, out var existing) ? existing.Triangles : 0;

        if (tris == 0)
        {
            if (existing != null)
            {
                FreeChunkEntry(existing);
                _chunks.Remove(origin);
            }
        }
        else if (existing != null)
        {
            if (!existing.OwnedMeshRid.IsValid)
            {
                existing.OwnedMeshRid = RenderingServer.MeshCreate();
                existing.SharedMesh = null;
                builder.UploadTo(existing.OwnedMeshRid, _materials);
                RenderingServer.InstanceSetBase(existing.InstanceRid, existing.OwnedMeshRid);
            }
            else
            {
                builder.UploadTo(existing.OwnedMeshRid, _materials);
            }
            existing.Triangles = tris;
            existing.LocalAabb = builder.GetAabb();
        }
        else
        {
            CreateStreamedChunk(origin, builder);
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

    private void RecomputeTotals()
    {
        TriangleCount = 0;
        Aabb bounds = default;
        bool hasBounds = false;
        foreach (var (origin, entry) in _chunks)
        {
            TriangleCount += entry.Triangles;
            var aabb = entry.LocalAabb;
            aabb.Position += new Vector3(origin.X, origin.Y, origin.Z);
            bounds = hasBounds ? bounds.Merge(aabb) : aabb;
            hasBounds = true;
        }
        Bounds = hasBounds ? bounds : new Aabb(Vector3.Zero, Vector3.One);
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

    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            case NotificationEnterWorld:
                SyncAllInstancesToWorld();
                break;
            case NotificationTransformChanged:
                UpdateAllTransforms();
                break;
            case NotificationVisibilityChanged:
                UpdateAllVisibility();
                break;
            case NotificationExitWorld:
                DetachAllFromScenario();
                break;
            case NotificationPredelete:
                FreeAllChunks();
                break;
        }
    }

    private void CreateStreamedChunk(Vector3I origin, ArrayMeshBuilder builder)
    {
        Rid meshRid = RenderingServer.MeshCreate();
        builder.UploadTo(meshRid, _materials);

        Rid instanceRid = CreateAndBindInstance(origin, meshRid);
        _chunks[origin] = new ChunkEntry
        {
            InstanceRid = instanceRid,
            OwnedMeshRid = meshRid,
            Triangles = builder.TriangleCount,
            LocalAabb = builder.GetAabb(),
        };
    }

    private void CreatePrebuiltChunk(Vector3I origin, ArrayMesh mesh)
    {
        Rid instanceRid = CreateAndBindInstance(origin, mesh.GetRid());
        _chunks[origin] = new ChunkEntry
        {
            InstanceRid = instanceRid,
            SharedMesh = mesh,
            Triangles = CountTriangles(mesh),
            LocalAabb = mesh.GetAabb(),
        };
    }

    private Rid CreateAndBindInstance(Vector3I origin, Rid meshRid)
    {
        Rid instanceRid = RenderingServer.InstanceCreate();
        RenderingServer.InstanceSetBase(instanceRid, meshRid);
        Rid scenario = CurrentScenario();
        if (scenario.IsValid)
            RenderingServer.InstanceSetScenario(instanceRid, scenario);
        RenderingServer.InstanceSetTransform(instanceRid, ChunkTransform(origin));
        RenderingServer.InstanceSetVisible(instanceRid, CurrentVisibility());
        if (_fallbackMaterial != null)
            RenderingServer.InstanceGeometrySetMaterialOverride(instanceRid, _fallbackMaterial.GetRid());
        return instanceRid;
    }

    private Rid CurrentScenario() =>
        IsInsideTree() && GetWorld3D() is { } world ? world.Scenario : default;

    private bool CurrentVisibility() =>
        IsInsideTree() ? IsVisibleInTree() : Visible;

    private Transform3D ChunkTransform(Vector3I origin)
    {
        var baseTransform = IsInsideTree() ? GlobalTransform : Transform;
        return baseTransform.TranslatedLocal(new Vector3(origin.X, origin.Y, origin.Z));
    }

    private void SyncAllInstancesToWorld()
    {
        Rid scenario = CurrentScenario();
        bool visible = CurrentVisibility();
        foreach (var (origin, entry) in _chunks)
        {
            RenderingServer.InstanceSetScenario(entry.InstanceRid, scenario);
            RenderingServer.InstanceSetTransform(entry.InstanceRid, ChunkTransform(origin));
            RenderingServer.InstanceSetVisible(entry.InstanceRid, visible);
        }
    }

    private void UpdateAllTransforms()
    {
        foreach (var (origin, entry) in _chunks)
            RenderingServer.InstanceSetTransform(entry.InstanceRid, ChunkTransform(origin));
    }

    private void UpdateAllVisibility()
    {
        bool visible = CurrentVisibility();
        foreach (var entry in _chunks.Values)
            RenderingServer.InstanceSetVisible(entry.InstanceRid, visible);
    }

    private void DetachAllFromScenario()
    {
        foreach (var entry in _chunks.Values)
            RenderingServer.InstanceSetScenario(entry.InstanceRid, default);
    }

    private static void FreeChunkEntry(ChunkEntry entry)
    {
        if (entry.InstanceRid.IsValid)
            RenderingServer.FreeRid(entry.InstanceRid);
        if (entry.OwnedMeshRid.IsValid)
            RenderingServer.FreeRid(entry.OwnedMeshRid);
        entry.SharedMesh = null;
    }

    private void FreeAllChunks()
    {
        foreach (var entry in _chunks.Values)
            FreeChunkEntry(entry);
        _chunks.Clear();
        TriangleCount = 0;
    }
}
