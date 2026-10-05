using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Meshing;

/// <summary>
/// Godot-native mesher: walks the runtime <see cref="Octa"/> and emits
/// <see cref="ArrayMesh"/>es (one surface per texture). This is the stage-B
/// replacement for octarender.cpp — geometry only for now; merges, T-joints,
/// sky, grass, lightmaps and material layers are deliberate follow-ups.
///
/// The world is split into fixed-size chunks of <c>chunkSize</c> world units.
/// A chunk's vertices are stored relative to its origin; place the chunk node at
/// <see cref="MeshChunk.Origin"/>. Positions are in Sauerbraten coordinates
/// (Z-up); the parent node carries the Z-up → Y-up rotation.
///
/// Chunk assignment is by the leaf cube's origin, so a cube larger than a chunk
/// may spill outside its nominal bounds. That is correct for rendering (Godot
/// culls by the mesh AABB); an incremental-update pass should invalidate every
/// chunk a changed cube's AABB overlaps.
/// </summary>
public sealed class OctaMesher
{
    private readonly Octa _octa;

    public OctaMesher(Octa octa) => _octa = octa;

    /// <summary>Mesh the whole world as a single chunk.</summary>
    public ArrayMesh Build() => BuildChunked(_octa.WorldSize).Chunks is { Count: > 0 } chunks
        ? chunks[0].Mesh
        : new ArrayMesh();

    /// <summary>Mesh the world into chunks of <paramref name="chunkSize"/> world units (power of two).</summary>
    public ChunkedWorldMesh BuildChunked(int chunkSize = 512)
    {
        if (chunkSize <= 0) chunkSize = 512;

        var builders = new Dictionary<Vector3I, ArrayMeshBuilder>();
        Walk(_octa.WorldRoot, Vector3I.Zero, _octa.WorldSize >> 1, chunkSize, builders);

        var result = new ChunkedWorldMesh { ChunkSize = chunkSize };
        var keys = new List<Vector3I>(builders.Keys);
        keys.Sort(CompareKeys); // deterministic order

        foreach (var key in keys)
        {
            var builder = builders[key];
            if (builder.TriangleCount == 0) continue;
            result.Chunks.Add(new MeshChunk { Origin = key * chunkSize, Mesh = builder.Build() });
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

    private void Walk(Cube[] cubes, Vector3I co, int size, int chunkSize, Dictionary<Vector3I, ArrayMeshBuilder> chunks)
    {
        for (int i = 0; i < 8; i++)
        {
            var childCo = new Vector3I(
                co.X + ((i & 1) >> 0) * size,
                co.Y + ((i & 2) >> 1) * size,
                co.Z + ((i & 4) >> 2) * size);
            var c = cubes[i];

            if (c.Children != null)
            {
                Walk(c.Children, childCo, size >> 1, chunkSize, chunks);
                continue;
            }

            if (c.IsEmpty()) continue; // TODO: sky faces and material (water/lava) surfaces

            for (int orient = 0; orient < 6; orient++)
            {
                if (!FaceVisible(c, orient, childCo, size)) continue;

                var key = new Vector3I(childCo.X / chunkSize, childCo.Y / chunkSize, childCo.Z / chunkSize);
                if (!chunks.TryGetValue(key, out var builder))
                {
                    builder = new ArrayMeshBuilder();
                    chunks[key] = builder;
                }
                EmitFace(c, orient, childCo - key * chunkSize, size, builder);
            }
        }
    }

    /// <summary>
    /// Conservative face cull: hide a face only when the neighbour leaf is a solid
    /// cube at least as large as this face (so it covers it entirely). Anything
    /// finer or empty is drawn, which can overdraw but never leaves a hole.
    /// </summary>
    private bool FaceVisible(Cube c, int orient, Vector3I co, int size)
    {
        int dim = orient >> 1;
        int dir = (orient & 1) == 0 ? -1 : 1;
        int ncoord = co[dim] + dir * size;
        if (ncoord < 0 || ncoord >= _octa.WorldSize) return true; // world boundary

        var centre = co;
        centre[dim] = ncoord + size / 2;
        centre[(dim + 1) % 3] = co[(dim + 1) % 3] + size / 2;
        centre[(dim + 2) % 3] = co[(dim + 2) % 3] + size / 2;

        var nb = _octa.LeafCube(centre, out int nsize);
        return nb == null || !(nb.IsEntirelySolid() && nsize >= size);
    }

    private static void EmitFace(Cube c, int orient, Vector3I co, int size, ArrayMeshBuilder builder)
    {
        System.Span<Vector3I> v = stackalloc Vector3I[4];
        c.GenFaceVerts(orient, v);

        int order = c.FaceConvexity(orient) < 0 ? 1 : 0;
        Vector3 p0 = ToWorld(v[(order + 0) & 3], co, size);
        Vector3 p1 = ToWorld(v[(order + 1) & 3], co, size);
        Vector3 p2 = ToWorld(v[(order + 2) & 3], co, size);
        Vector3 p3 = ToWorld(v[(order + 3) & 3], co, size);

        Vector3 normal = (p1 - p0).Cross(p2 - p0);
        normal = normal.LengthSquared() < 1e-9f ? AxisNormal(orient) : normal.Normalized();
        builder.AddQuad(c.Texture[orient], p0, p1, p2, p3, normal);
    }

    private static Vector3 ToWorld(Vector3I v, Vector3I co, int size) => new(
        co.X + v.X * (size / 8.0f),
        co.Y + v.Y * (size / 8.0f),
        co.Z + v.Z * (size / 8.0f));

    private static Vector3 AxisNormal(int orient) => orient switch
    {
        0 => new Vector3(-1, 0, 0),
        1 => new Vector3(1, 0, 0),
        2 => new Vector3(0, -1, 0),
        3 => new Vector3(0, 1, 0),
        4 => new Vector3(0, 0, -1),
        _ => new Vector3(0, 0, 1),
    };
}
