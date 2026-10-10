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

        // Baked-lightmap link, if this face has one. A face with a CubeExt surface
        // is rendered from its stored vertices in order (octarender.cpp builds
        // pos[k] from verts[k].getxyz() directly, with no convexity reorder), and
        // samples the atlas at that vertex's u/v. Otherwise fall back to the
        // geometry-derived corners (the current unlit path).
        var surf = face.Cube.Ext?.Surfaces[face.Orient] ?? default;
        int lmid = 0;
        bool hasUv2 = false;
        if (face.Cube.Ext != null)
        {
            int total = surf.TotalVerts();
            if (total > 0)
            {
                lmid = surf.Lmid0;
                hasUv2 = true;
            }
        }
        var key = new SurfaceKey(vslot, lmid);

        if (hasUv2)
        {
            EmitLinkedFace(face, key, surf, local, builder);
            return;
        }

        System.Span<Vector3I> v = stackalloc Vector3I[4];
        face.Cube.GenFaceVerts(face.Orient, v);
        int order = face.Cube.FaceConvexity(face.Orient) < 0 ? 1 : 0;
        // Absolute positions drive the UVs (the texture mapping tiles in world
        // space); the emitted vertices are chunk-local.
        Vector3 a0 = MeshFace.ToWorld(v[(order + 0) & 3], face.Co, face.Size);
        Vector3 a1 = MeshFace.ToWorld(v[(order + 1) & 3], face.Co, face.Size);
        Vector3 a2 = MeshFace.ToWorld(v[(order + 2) & 3], face.Co, face.Size);
        Vector3 a3 = MeshFace.ToWorld(v[(order + 3) & 3], face.Co, face.Size);

        Vector2 uv0 = _materials?.Uv(vslot, dim, a0) ?? Vector2.Zero;
        Vector2 uv1 = _materials?.Uv(vslot, dim, a1) ?? Vector2.Zero;
        Vector2 uv2 = _materials?.Uv(vslot, dim, a2) ?? Vector2.Zero;
        Vector2 uv3 = _materials?.Uv(vslot, dim, a3) ?? Vector2.Zero;

        MeshFace.Quad(builder, key, face.Orient,
            a0 - local, a1 - local, a2 - local, a3 - local, uv0, uv1, uv2, uv3);
    }

    /// <summary>
    /// Emit a face that carries baked-lightmap vertices. The corners come from
    /// <see cref="CubeExt.Verts"/> in stored order (the engine's render order), and
    /// <c>TexUV2</c> is the normalized atlas coordinate <c>u / 65536</c>. Partial
    /// faces have 3 corners; the quad is emitted as a degenerate (0,1,2)+(0,2,2)
    /// pair so both meshers share one path.
    /// </summary>
    private void EmitLinkedFace(in CubeFace face, SurfaceKey key, in SurfaceInfo surf, Vector3 local, ArrayMeshBuilder builder)
    {
        int dim = face.Orient >> 1;
        int total = surf.TotalVerts();
        var verts = face.Cube.Ext!.Verts;

        // Read up to four corners; a 3-vertex face repeats its last corner.
        Vector3 a0 = VertWorld(verts, surf.Verts, 0);
        Vector3 a1 = VertWorld(verts, surf.Verts, 1);
        Vector3 a2 = VertWorld(verts, surf.Verts, 2);
        Vector3 a3 = total >= 4 ? VertWorld(verts, surf.Verts, 3) : a2;

        var uv2_0 = VertUv(verts, surf.Verts, 0);
        var uv2_1 = VertUv(verts, surf.Verts, 1);
        var uv2_2 = VertUv(verts, surf.Verts, 2);
        var uv2_3 = total >= 4 ? VertUv(verts, surf.Verts, 3) : uv2_2;

        // The albedo UV is still world-space (calctexgen), like the unlit path.
        Vector2 uv0 = _materials?.Uv(key.Texture, dim, a0) ?? Vector2.Zero;
        Vector2 uv1 = _materials?.Uv(key.Texture, dim, a1) ?? Vector2.Zero;
        Vector2 uv2 = _materials?.Uv(key.Texture, dim, a2) ?? Vector2.Zero;
        Vector2 uv3 = _materials?.Uv(key.Texture, dim, a3) ?? Vector2.Zero;

        MeshFace.Quad(builder, key, face.Orient,
            a0 - local, a1 - local, a2 - local, a3 - local, uv0, uv1, uv2, uv3,
            hasUv2: true, uv2_0, uv2_1, uv2_2, uv2_3);
    }

    /// <summary>World position of the k-th stored vertex (VertInfo xyz are 1/8-world units).</summary>
    private static Vector3 VertWorld(VertInfo[] verts, int baseIndex, int k)
    {
        var v = verts[baseIndex + k];
        return new Vector3(v.X / 8.0f, v.Y / 8.0f, v.Z / 8.0f);
    }

    /// <summary>Normalized atlas coordinate for the k-th stored vertex (u/v are ushort over the 512 atlas).</summary>
    private static Vector2 VertUv(VertInfo[] verts, int baseIndex, int k)
    {
        var v = verts[baseIndex + k];
        return new Vector2(v.U / 65536.0f, v.V / 65536.0f);
    }

    /// <summary>Test seam: emit one face into a fresh builder via the normal path.</summary>
    internal void EmitFaceForTest(in CubeFace face, Vector3I origin, ArrayMeshBuilder builder) =>
        EmitFace(face, origin, builder);
}
