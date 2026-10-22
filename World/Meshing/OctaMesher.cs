using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Meshing;

/// <summary>
/// Godot-native mesher: walks the runtime <see cref="Octa"/> and emits
/// <see cref="ArrayMesh"/>es (one surface per texture). This is the stage-B
/// replacement for octarender.cpp — merges, T-joints, sky, grass, lightmaps and
/// blending are deliberate follow-ups.
///
/// The world is split into fixed-size chunks of <c>chunkSize</c> world units.
/// A chunk's vertices are stored relative to its origin; place the chunk node at
/// <see cref="MeshChunk.Origin"/>. Positions are in Sauerbraten coordinates
/// (Z-up); the parent node carries the Z-up → Y-up rotation.
///
/// When a <see cref="MaterialSet"/> is supplied, each surface gets its vslot's
/// material and per-face world-space UVs (octarender.cpp calctexgen). UVs are
/// computed from absolute world positions even though vertices are chunk-local,
/// because the texture mapping tiles in world space.
///
/// Chunk assignment is by the leaf cube's origin, so a cube larger than a chunk
/// may spill outside its nominal bounds. That is correct for rendering (Godot
/// culls by the mesh AABB); an incremental-update pass should invalidate every
/// chunk a changed cube's AABB overlaps.
/// </summary>
public sealed class OctaMesher
{
    private readonly Octa _octa;
    private readonly MaterialSet? _materials;

    public OctaMesher(Octa octa, MaterialSet? materials = null)
    {
        _octa = octa;
        _materials = materials;
    }

    /// <summary>Mesh the whole world as a single chunk.</summary>
    public ArrayMesh Build() => BuildChunked(_octa.WorldSize).Chunks is { Count: > 0 } chunks
        ? chunks[0].Mesh
        : new ArrayMesh();

    /// <summary>The chunk key (origin / chunkSize) that owns a leaf at <paramref name="co"/>.</summary>
    public static Vector3I ChunkKey(Vector3I co, int chunkSize) =>
        new(co.X / chunkSize, co.Y / chunkSize, co.Z / chunkSize);

    /// <summary>Mesh one chunk region into a single <see cref="ArrayMesh"/>. Used by incremental rebuilds.</summary>
    public ArrayMesh BuildChunk(Vector3I chunkOrigin, int chunkSize)
    {
        var builder = new ArrayMeshBuilder();
        var min = chunkOrigin;
        var max = chunkOrigin + new Vector3I(chunkSize, chunkSize, chunkSize);
        OctaFaceWalker.WalkRegion(_octa, min, max,
            include: c => !c.IsEmpty() && ((c.Material & Octa.MATF_VOLUME) == 0 || c.IsEntirelySolid()),
            hideSameMaterial: false,
            emit: face =>
            {
                if (ChunkKey(face.Co, chunkSize) * chunkSize != chunkOrigin) return;
                EmitFace(face, chunkOrigin, builder);
            });
        return builder.Build(_materials);
    }

    /// <summary>Mesh the world into chunks of <paramref name="chunkSize"/> world units (power of two).</summary>
    public ChunkedWorldMesh BuildChunked(int chunkSize = 512)
    {
        if (chunkSize <= 0) chunkSize = 512;

        var builders = new Dictionary<Vector3I, ArrayMeshBuilder>();
        OctaFaceWalker.Walk(_octa,
            include: c => !c.IsEmpty() && ((c.Material & Octa.MATF_VOLUME) == 0 || c.IsEntirelySolid()),
            hideSameMaterial: false,
            emit: face =>
            {
                var key = new Vector3I(face.Co.X / chunkSize, face.Co.Y / chunkSize, face.Co.Z / chunkSize);
                if (!builders.TryGetValue(key, out var builder))
                {
                    builder = new ArrayMeshBuilder();
                    builders[key] = builder;
                }
                EmitFace(face, key * chunkSize, builder);
            });

        var result = new ChunkedWorldMesh { ChunkSize = chunkSize };
        var keys = new List<Vector3I>(builders.Keys);
        keys.Sort(CompareKeys); // deterministic order

        foreach (var key in keys)
        {
            var builder = builders[key];
            if (builder.TriangleCount == 0) continue;
            result.Chunks.Add(new MeshChunk { Origin = key * chunkSize, Mesh = builder.Build(_materials) });
            result.TriangleCount += builder.TriangleCount;
            result.SurfaceCount += builder.TextureCount;
        }
        return result;
    }

    private static int CompareKeys(Vector3I a, Vector3I b)
    {
        int c = a.Z.CompareTo(b.Z);
        if (c != 0) return c;
        c = a.Y.CompareTo(b.Y);
        return c != 0 ? c : a.X.CompareTo(b.X);
    }

    private void EmitFace(in CubeFace face, Vector3I origin, ArrayMeshBuilder builder)
    {
        int vslot = face.Cube.Texture[face.Orient];
        int dim = face.Orient >> 1;
        var local = new Vector3(origin.X, origin.Y, origin.Z);

        System.Span<Vector3I> v = stackalloc Vector3I[4];
        face.Cube.GenFaceVerts(face.Orient, v);
        int convex = face.Cube.FlatAxisFace(face.Orient) ? 0 : CubeGeometry.FaceConvexity(v);
        int order = (face.Vis & 4) != 0 || convex < 0 ? 1 : 0;
        // Absolute positions drive the UVs (the texture mapping tiles in world
        // space); the emitted vertices are chunk-local.
        Vector3 a0 = MeshFace.ToWorld(v[(order + 0) & 3], face.Co, face.Size);
        Vector3 a1 = MeshFace.ToWorld(v[(order + 1) & 3], face.Co, face.Size);
        Vector3 a2 = MeshFace.ToWorld(v[(order + 2) & 3], face.Co, face.Size);
        Vector3 a3 = MeshFace.ToWorld(v[(order + 3) & 3], face.Co, face.Size);
        Vector3 l0 = a0 - local, l1 = a1 - local, l2 = a2 - local, l3 = a3 - local;

        bool alpha = (face.Cube.Material & Octa.MAT_ALPHA) != 0;

        // octarender.cpp gencubeverts emits the top layer when the surface has no
        // surfaceinfo at all (`!surf.numverts`) or carries LAYER_TOP. A face whose
        // surfaceinfo marks only LAYER_BOTTOM is bottom-only in the original.
        var surfInfo = face.Cube.Ext?.Surfaces[face.Orient];
        bool hasSurf = surfInfo is { NumVerts: not 0 };
        if (!hasSurf || (surfInfo!.Value.NumVerts & Octa.LAYER_TOP) != 0)
            EmitLayer(builder, vslot, dim, face.Orient, face.Vis, alpha, l0, l1, l2, l3, a0, a1, a2, a3);

        // Bottom layer: the same face re-textured with the vslot's layer. The
        // engine passes `layer ? *layer : vslot`, but a bottom layer only has a
        // distinct texture when `vslot.layer` is set, so require one. A MAT_ALPHA
        // cube disables the layer lookup entirely (octarender.cpp:1075), so the
        // bottom layer, if any, uses the base vslot.
        int layer = alpha ? 0 : _materials?.LayerFor(vslot) ?? 0;
        if (layer != 0 && hasSurf && (surfInfo!.Value.NumVerts & Octa.LAYER_BOTTOM) != 0)
            EmitLayer(builder, layer, dim, face.Orient, face.Vis, alpha, l0, l1, l2, l3, a0, a1, a2, a3);
    }

    /// <summary>
    /// Emit one layer of a face: the surviving triangles (<paramref name="vis"/>
    /// from <c>visibletris</c>), with the given vslot's material and world-space
    /// UVs (octarender.cpp calctexgen is applied per-layer, so the top and bottom
    /// textures tile independently). When <paramref name="alpha"/> the surface is
    /// keyed to the transparent material variant (MAT_ALPHA cubes).
    /// </summary>
    private void EmitLayer(ArrayMeshBuilder builder, int vslot, int dim, int orient, int vis, bool alpha,
        Vector3 l0, Vector3 l1, Vector3 l2, Vector3 l3,
        Vector3 a0, Vector3 a1, Vector3 a2, Vector3 a3)
    {
        int key = _materials?.Key(vslot, alpha) ?? vslot;
        int tris = vis & 3;
        if (tris == 3)
        {
            Vector2 uv0 = _materials?.Uv(vslot, dim, a0) ?? Vector2.Zero;
            Vector2 uv1 = _materials?.Uv(vslot, dim, a1) ?? Vector2.Zero;
            Vector2 uv2 = _materials?.Uv(vslot, dim, a2) ?? Vector2.Zero;
            Vector2 uv3 = _materials?.Uv(vslot, dim, a3) ?? Vector2.Zero;
            MeshFace.Quad(builder, key, orient, l0, l1, l2, l3, uv0, uv1, uv2, uv3);
        }
        else if (tris == 1)
        {
            Vector2 uv0 = _materials?.Uv(vslot, dim, a0) ?? Vector2.Zero;
            Vector2 uv1 = _materials?.Uv(vslot, dim, a1) ?? Vector2.Zero;
            Vector2 uv2 = _materials?.Uv(vslot, dim, a2) ?? Vector2.Zero;
            MeshFace.Triangle(builder, key, orient, l0, l1, l2, uv0, uv1, uv2);
        }
        else if (tris == 2)
        {
            Vector2 uv0 = _materials?.Uv(vslot, dim, a0) ?? Vector2.Zero;
            Vector2 uv2 = _materials?.Uv(vslot, dim, a2) ?? Vector2.Zero;
            Vector2 uv3 = _materials?.Uv(vslot, dim, a3) ?? Vector2.Zero;
            MeshFace.Triangle(builder, key, orient, l0, l2, l3, uv0, uv2, uv3);
        }
    }
}
