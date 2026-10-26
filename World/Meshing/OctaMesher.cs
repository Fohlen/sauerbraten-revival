using System;
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

    /// <summary>Whether to compute and apply T-joints during meshing (octarender.cpp filltjoints).</summary>
    public bool FillTJoints { get; set; } = true;

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

    /// <summary>Mesh one chunk region into an <see cref="ArrayMeshBuilder"/> for direct <see cref="RenderingServer"/> upload.</summary>
    public ArrayMeshBuilder BuildChunkBuilder(Vector3I chunkOrigin, int chunkSize)
    {
        if (FillTJoints && _octa.TJoints.Count == 0)
            OctaTJoints.FindTJoints(_octa);

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
        return builder;
    }

    /// <summary>Mesh one chunk region into a single <see cref="ArrayMesh"/>. Used by tests and ArrayMesh callers.</summary>
    public ArrayMesh BuildChunk(Vector3I chunkOrigin, int chunkSize) =>
        BuildChunkBuilder(chunkOrigin, chunkSize).Build(_materials);

    /// <summary>Mesh the world into per-chunk <see cref="ArrayMeshBuilder"/>s ordered deterministically by chunk origin.</summary>
    public List<(Vector3I Origin, ArrayMeshBuilder Builder)> BuildChunkedBuilders(int chunkSize = 512)
    {
        if (chunkSize <= 0) chunkSize = 512;
        if (FillTJoints && _octa.TJoints.Count == 0)
            OctaTJoints.FindTJoints(_octa);

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

        var keys = new List<Vector3I>(builders.Keys);
        keys.Sort(CompareKeys); // deterministic order

        var result = new List<(Vector3I Origin, ArrayMeshBuilder Builder)>(keys.Count);
        foreach (var key in keys)
        {
            var builder = builders[key];
            if (builder.TriangleCount == 0) continue;
            result.Add((key * chunkSize, builder));
        }
        return result;
    }

    /// <summary>Mesh the world into chunks of <paramref name="chunkSize"/> world units (power of two).</summary>
    public ChunkedWorldMesh BuildChunked(int chunkSize = 512)
    {
        if (chunkSize <= 0) chunkSize = 512;
        var result = new ChunkedWorldMesh { ChunkSize = chunkSize };
        foreach (var (origin, builder) in BuildChunkedBuilders(chunkSize))
        {
            result.Chunks.Add(new MeshChunk { Origin = origin, Mesh = builder.Build(_materials) });
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
        bool alpha = (face.Cube.Material & Octa.MAT_ALPHA) != 0;

        int tj = FillTJoints && face.Cube.Ext != null ? face.Cube.Ext.TJoints : -1;
        while (tj >= 0 && _octa.TJoints[tj].Edge < face.Orient * (Octa.MAXFACEVERTS + 1))
            tj = _octa.TJoints[tj].Next;
        int hastj = (tj >= 0 && _octa.TJoints[tj].Edge < (face.Orient + 1) * (Octa.MAXFACEVERTS + 1)) ? tj : -1;

        if ((face.Cube.Merged & (1 << face.Orient)) != 0)
        {
            EmitMergedFace(face, origin, builder, vslot, dim, local, alpha, hastj);
            return;
        }

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

        // octarender.cpp gencubeverts emits the top layer when the surface has no
        // surfaceinfo at all (`!surf.numverts`) or carries LAYER_TOP. A face whose
        // surfaceinfo marks only LAYER_BOTTOM is bottom-only in the original.
        var surfInfo = face.Cube.Ext?.Surfaces[face.Orient];
        bool hasSurf = surfInfo is { NumVerts: not 0 };
        if (!hasSurf || (surfInfo!.Value.NumVerts & Octa.LAYER_TOP) != 0)
            EmitLayer(builder, vslot, dim, face.Orient, face.Vis, alpha, l0, l1, l2, l3, a0, a1, a2, a3, hastj);

        // Bottom layer: the same face re-textured with the vslot's layer. The
        // engine passes `layer ? *layer : vslot`, but a bottom layer only has a
        // distinct texture when `vslot.layer` is set, so require one. A MAT_ALPHA
        // cube disables the layer lookup entirely (octarender.cpp:1075), so the
        // bottom layer, if any, uses the base vslot.
        int layer = alpha ? 0 : _materials?.LayerFor(vslot) ?? 0;
        if (layer != 0 && hasSurf && (surfInfo!.Value.NumVerts & Octa.LAYER_BOTTOM) != 0)
            EmitLayer(builder, layer, dim, face.Orient, face.Vis, alpha, l0, l1, l2, l3, a0, a1, a2, a3, hastj);
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
        Vector3 a0, Vector3 a1, Vector3 a2, Vector3 a3, int hastj)
    {
        int key = _materials?.Key(vslot, alpha) ?? vslot;
        int tris = vis & 3;

        if (hastj >= 0)
        {
            System.Span<Vector3> w = stackalloc Vector3[4];
            System.Span<Vector3> l = stackalloc Vector3[4];
            System.Span<Vector2> uv = stackalloc Vector2[4];
            int numverts = 0;

            if (tris == 3)
            {
                numverts = 4;
                w[0] = a0; w[1] = a1; w[2] = a2; w[3] = a3;
                l[0] = l0; l[1] = l1; l[2] = l2; l[3] = l3;
                uv[0] = _materials?.Uv(vslot, dim, a0) ?? Vector2.Zero;
                uv[1] = _materials?.Uv(vslot, dim, a1) ?? Vector2.Zero;
                uv[2] = _materials?.Uv(vslot, dim, a2) ?? Vector2.Zero;
                uv[3] = _materials?.Uv(vslot, dim, a3) ?? Vector2.Zero;
            }
            else if (tris == 1)
            {
                numverts = 3;
                w[0] = a0; w[1] = a1; w[2] = a2;
                l[0] = l0; l[1] = l1; l[2] = l2;
                uv[0] = _materials?.Uv(vslot, dim, a0) ?? Vector2.Zero;
                uv[1] = _materials?.Uv(vslot, dim, a1) ?? Vector2.Zero;
                uv[2] = _materials?.Uv(vslot, dim, a2) ?? Vector2.Zero;
            }
            else if (tris == 2)
            {
                numverts = 3;
                w[0] = a0; w[1] = a2; w[2] = a3;
                l[0] = l0; l[1] = l2; l[2] = l3;
                uv[0] = _materials?.Uv(vslot, dim, a0) ?? Vector2.Zero;
                uv[1] = _materials?.Uv(vslot, dim, a2) ?? Vector2.Zero;
                uv[2] = _materials?.Uv(vslot, dim, a3) ?? Vector2.Zero;
            }

            if (numverts >= 3)
            {
                AddTris(builder, key, orient, l[..numverts], w[..numverts], uv[..numverts], numverts, hastj, _octa.TJoints);
                return;
            }
        }

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

    private void EmitMergedFace(in CubeFace face, Vector3I origin, ArrayMeshBuilder builder,
                               int vslot, int dim, Vector3 local, bool alpha, int hastj)
    {
        if (face.Cube.Ext == null) return;
        var surfInfo = face.Cube.Ext.Surfaces[face.Orient];
        int numverts = surfInfo.NumVerts & Octa.MAXFACEVERTS;
        if (numverts < 3) return;

        var vo = new Vector3I((face.Co.X & ~0xFFF), (face.Co.Y & ~0xFFF), (face.Co.Z & ~0xFFF));
        System.Span<Vector3> worldPos = stackalloc Vector3[numverts];
        System.Span<Vector3> localPos = stackalloc Vector3[numverts];
        int vertOffset = surfInfo.Verts;
        var verts = face.Cube.Ext.Verts;

        for (int k = 0; k < numverts; k++)
        {
            var vi = verts[vertOffset + k];
            worldPos[k] = new Vector3(vo.X + vi.X / 8.0f, vo.Y + vi.Y / 8.0f, vo.Z + vi.Z / 8.0f);
            localPos[k] = worldPos[k] - local;
        }

        bool hasSurf = surfInfo.NumVerts != 0;
        if (!hasSurf || (surfInfo.NumVerts & Octa.LAYER_TOP) != 0)
            EmitMergedLayer(builder, vslot, dim, face.Orient, alpha, localPos, worldPos, hastj);

        int layer = alpha ? 0 : _materials?.LayerFor(vslot) ?? 0;
        if (layer != 0 && hasSurf && (surfInfo.NumVerts & Octa.LAYER_BOTTOM) != 0)
        {
            if ((surfInfo.NumVerts & Octa.LAYER_DUP) != 0 && verts.Length >= vertOffset + numverts * 2)
            {
                System.Span<Vector3> worldPosDup = stackalloc Vector3[numverts];
                System.Span<Vector3> localPosDup = stackalloc Vector3[numverts];
                for (int k = 0; k < numverts; k++)
                {
                    var vi = verts[vertOffset + numverts + k];
                    worldPosDup[k] = new Vector3(vo.X + vi.X / 8.0f, vo.Y + vi.Y / 8.0f, vo.Z + vi.Z / 8.0f);
                    localPosDup[k] = worldPosDup[k] - local;
                }
                EmitMergedLayer(builder, layer, dim, face.Orient, alpha, localPosDup, worldPosDup, hastj);
            }
            else
            {
                EmitMergedLayer(builder, layer, dim, face.Orient, alpha, localPos, worldPos, hastj);
            }
        }
    }

    private void EmitMergedLayer(ArrayMeshBuilder builder, int vslot, int dim, int orient, bool alpha,
                                System.ReadOnlySpan<Vector3> localPos, System.ReadOnlySpan<Vector3> worldPos, int hastj)
    {
        int key = _materials?.Key(vslot, alpha) ?? vslot;
        System.Span<Vector2> uvs = stackalloc Vector2[worldPos.Length];
        for (int k = 0; k < worldPos.Length; k++)
            uvs[k] = _materials?.Uv(vslot, dim, worldPos[k]) ?? Vector2.Zero;

        if (hastj >= 0)
        {
            AddTris(builder, key, orient, localPos, worldPos, uvs, localPos.Length, hastj, _octa.TJoints);
            return;
        }

        MeshFace.Polygon(builder, key, orient, localPos, uvs);
    }

    /// <summary>
    /// Port of Sauerbraten's addtris from src/engine/octarender.cpp. Emits triangles
    /// fanned across the face polygon and splits along edges where T-joints occur.
    /// </summary>
    private void AddTris(ArrayMeshBuilder builder, int key, int orient,
                         System.ReadOnlySpan<Vector3> localVerts,
                         System.ReadOnlySpan<Vector3> worldVerts,
                         System.ReadOnlySpan<Vector2> uvs,
                         int numverts, int tj,
                         System.Collections.Generic.IReadOnlyList<TJoint> tjoints)
    {
        Vector3 normal = MeshFace.AxisNormal(orient);
        System.Span<int> index = stackalloc int[numverts];
        System.Span<Vector3> addedVerts = stackalloc Vector3[Octa.MAXFACEVERTS + 16];
        System.Span<int> addedIndices = stackalloc int[Octa.MAXFACEVERTS + 16];
        int addedCount = 0;

        static int GetOrAddVert(ArrayMeshBuilder builder, int key, Vector3 normal,
                                Vector3 localPos, Vector2 uv,
                                System.Span<Vector3> addedVerts,
                                System.Span<int> addedIndices,
                                ref int addedCount)
        {
            for (int v = 0; v < addedCount; v++)
            {
                if (addedVerts[v].DistanceSquaredTo(localPos) < 1e-6f)
                    return addedIndices[v];
            }
            int idx = builder.AddVertex(key, localPos, uv, normal);
            if (addedCount < addedVerts.Length)
            {
                addedVerts[addedCount] = localPos;
                addedIndices[addedCount] = idx;
                addedCount++;
            }
            return idx;
        }

        for (int k = 0; k < numverts; k++)
            index[k] = GetOrAddVert(builder, key, normal, localVerts[k], uvs[k], addedVerts, addedIndices, ref addedCount);

        int edge = orient * (Octa.MAXFACEVERTS + 1);
        for (int i = 0; i < numverts - 2; i++)
        {
            if (index[0] == index[i + 1] || index[i + 1] == index[i + 2] || index[i + 2] == index[0])
                continue;

            int left = index[0], mid = index[i + 1], right = index[i + 2], start = left, i0 = left, i1 = -1;
            for (int k = 0; k < 4; k++)
            {
                int i2 = -1, ctj = -1, cedge = -1;
                switch (k)
                {
                    case 1: i1 = i2 = mid; cedge = edge + i + 1; break;
                    case 2:
                        if (i1 != mid || i0 == left) { i0 = i1; i1 = right; }
                        i2 = right;
                        if (i + 1 == numverts - 2) cedge = edge + i + 2;
                        break;
                    case 3:
                        if (i0 == start) { i0 = i1; i1 = left; }
                        i2 = left;
                        if (i == 0) cedge = edge;
                        break;
                    default:
                        if (i == 0) cedge = edge;
                        break;
                }

                if (i1 != i2)
                {
                    builder.AddIndexTriangle(key, i0, i1, i2, normal);
                    i1 = i2;
                }

                if (cedge >= 0)
                {
                    for (ctj = tj; ;)
                    {
                        if (ctj < 0) break;
                        if (tjoints[ctj].Edge < cedge) { ctj = tjoints[ctj].Next; continue; }
                        if (tjoints[ctj].Edge != cedge) ctj = -1;
                        break;
                    }
                }

                if (ctj >= 0)
                {
                    int e1 = cedge % (Octa.MAXFACEVERTS + 1), e2 = (e1 + 1) % numverts;
                    Vector3 w1 = worldVerts[e1], w2 = worldVerts[e2];
                    Vector3 l1 = localVerts[e1], l2 = localVerts[e2];
                    Vector2 uv1 = uvs[e1], uv2 = uvs[e2];

                    var d = new Vector3I(
                        (int)Math.Round((w2.X - w1.X) * 8.0f),
                        (int)Math.Round((w2.Y - w1.Y) * 8.0f),
                        (int)Math.Round((w2.Z - w1.Z) * 8.0f));

                    int absX = Math.Abs(d.X), absY = Math.Abs(d.Y), absZ = Math.Abs(d.Z);
                    int axis = absX > absY ? (absX > absZ ? 0 : 2) : (absY > absZ ? 1 : 2);
                    if (d[axis] < 0) d = -d;
                    OctaMerges.ReduceSlope(ref d);

                    int minCoord = Math.Min((int)Math.Round(w1[axis] * 8.0f), (int)Math.Round(w2[axis] * 8.0f));
                    int origin = minCoord & ~0x7FFF;
                    int offset1 = ((int)Math.Round(w1[axis] * 8.0f) - origin) / d[axis];
                    int offset2 = ((int)Math.Round(w2[axis] * 8.0f) - origin) / d[axis];
                    float doffset = 1.0f / (offset2 - offset1);

                    if (i1 < 0)
                    {
                        for (; ; )
                        {
                            var t = tjoints[ctj];
                            if (t.Next < 0 || tjoints[t.Next].Edge != cedge) break;
                            ctj = t.Next;
                        }
                    }

                    while (ctj >= 0)
                    {
                        var t = tjoints[ctj];
                        if (t.Edge != cedge) break;

                        float offset = (t.Offset - offset1) * doffset;
                        Vector3 vtLocal = l1.Lerp(l2, offset);
                        Vector2 vtUv = uv1.Lerp(uv2, offset);
                        i2 = GetOrAddVert(builder, key, normal, vtLocal, vtUv, addedVerts, addedIndices, ref addedCount);

                        if (i1 >= 0)
                        {
                            builder.AddIndexTriangle(key, i0, i1, i2, normal);
                            i1 = i2;
                        }
                        else
                        {
                            start = i0 = i2;
                        }
                        ctj = t.Next;
                    }
                }
            }
        }
    }
}
