using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Meshing;

/// <summary>One material surface mesh (water/lava/glass) — a set of boundary faces.</summary>
public sealed class MaterialSurfaceMesh
{
    public ushort Material;
    public ArrayMesh Mesh = null!;
}

/// <summary>
/// Meshes the empty-space material surfaces (<c>MAT_WATER</c>/<c>MAT_LAVA</c>/
/// <c>MAT_GLASS</c>): the faces where a material cube borders air (or a different
/// material). Ported from <c>engine/material.cpp genmatsurfs</c>, simplified to a
/// conservative boundary test (no cube-edge merging or partial-face occlusion).
/// Decoupled from the chunk mesher; the viewer applies the water/lava material.
/// </summary>
public static class MaterialMesher
{
    private const float TexScale = 32.0f;

    public static List<MaterialSurfaceMesh> Build(Octa octa)
    {
        var builders = new Dictionary<ushort, ArrayMeshBuilder>();
        Walk(octa.WorldRoot, Vector3I.Zero, octa.WorldSize >> 1, octa, builders);

        var result = new List<MaterialSurfaceMesh>();
        foreach (var pair in builders)
            if (pair.Value.TriangleCount > 0)
                result.Add(new MaterialSurfaceMesh { Material = pair.Key, Mesh = pair.Value.Build() });
        return result;
    }

    private static void Walk(Cube[] cubes, Vector3I co, int size, Octa octa, Dictionary<ushort, ArrayMeshBuilder> builders)
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
                Walk(c.Children, childCo, size >> 1, octa, builders);
                continue;
            }

            ushort volume = (ushort)(c.Material & Octa.MATF_VOLUME);
            if (volume == Octa.MAT_AIR) continue;

            for (int orient = 0; orient < 6; orient++)
            {
                // Water/lava hide their bottom face in-game (edit-only surface).
                if (orient == 4 && volume != Octa.MAT_GLASS) continue;
                if (!Visible(octa, c, orient, childCo, size)) continue;
                Emit(volume, orient, childCo, size, builders);
            }
        }
    }

    private static bool Visible(Octa octa, Cube c, int orient, Vector3I co, int size)
    {
        int dim = orient >> 1;
        int dir = (orient & 1) == 0 ? -1 : 1;
        int ncoord = co[dim] + dir * size;
        if (ncoord < 0 || ncoord >= octa.WorldSize) return true; // world boundary

        var centre = co;
        centre[dim] = ncoord + size / 2;
        centre[(dim + 1) % 3] = co[(dim + 1) % 3] + size / 2;
        centre[(dim + 2) % 3] = co[(dim + 2) % 3] + size / 2;

        var nb = octa.LeafCube(centre, out int nsize);
        if (nb == null) return true;
        if (nb.IsEntirelySolid() && nsize >= size) return false; // rock blocks the surface
        if ((nb.Material & Octa.MATF_VOLUME) == (c.Material & Octa.MATF_VOLUME) && nsize >= size)
            return false; // interior of the same material
        return true;
    }

    private static void Emit(ushort material, int orient, Vector3I co, int size, Dictionary<ushort, ArrayMeshBuilder> builders)
    {
        if (!builders.TryGetValue(material, out var builder))
        {
            builder = new ArrayMeshBuilder();
            builders[material] = builder;
        }

        System.Span<Vector3I> v = stackalloc Vector3I[4];
        CubeGeometry.SolidFaceVerts(orient, v);
        Vector3 p0 = World(v[0], co, size), p1 = World(v[1], co, size);
        Vector3 p2 = World(v[2], co, size), p3 = World(v[3], co, size);

        int dim = orient >> 1;
        int cAxis = Octa.C[dim], rAxis = Octa.R[dim];
        Vector2 uv0 = PlanarUv(p0, cAxis, rAxis);
        Vector2 uv1 = PlanarUv(p1, cAxis, rAxis);
        Vector2 uv2 = PlanarUv(p2, cAxis, rAxis);
        Vector2 uv3 = PlanarUv(p3, cAxis, rAxis);

        Vector3 normal = (p1 - p0).Cross(p2 - p0);
        normal = normal.LengthSquared() < 1e-9f ? AxisNormal(orient) : normal.Normalized();

        builder.AddQuad(0, p0, p1, p2, p3, uv0, uv1, uv2, uv3, normal);
    }

    private static Vector3 World(Vector3I v, Vector3I co, int size) => new(
        co.X + v.X * (size / 8.0f),
        co.Y + v.Y * (size / 8.0f),
        co.Z + v.Z * (size / 8.0f));

    private static Vector2 PlanarUv(Vector3 p, int cAxis, int rAxis)
    {
        float[] comp = { p.X, p.Y, p.Z };
        return new Vector2(comp[cAxis] / TexScale, comp[rAxis] / TexScale);
    }

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
