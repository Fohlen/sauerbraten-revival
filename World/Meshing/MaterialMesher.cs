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

    public static List<MaterialSurfaceMesh> Build(Octa octa, List<CubeFace>? emitted = null)
    {
        var builders = new Dictionary<ushort, ArrayMeshBuilder>();
        OctaFaceWalker.Walk(octa,
            include: c => (c.Material & Octa.MATF_VOLUME) != Octa.MAT_AIR,
            hideSameMaterial: true,
            emit: face =>
            {
                if (!Visible(octa, face)) return;
                ushort volume = (ushort)(face.Cube.Material & Octa.MATF_VOLUME);
                // Water/lava hide their bottom face in-game (edit-only surface).
                if (face.Orient == 4 && volume != Octa.MAT_GLASS) return;
                emitted?.Add(face);
                Emit(volume, face, builders);
            });

        var result = new List<MaterialSurfaceMesh>();
        foreach (var pair in builders)
            if (pair.Value.TriangleCount > 0)
                result.Add(new MaterialSurfaceMesh { Material = pair.Key, Mesh = pair.Value.Build() });
        return result;
    }

    /// <summary>
    /// engine/octa.cpp:1013 <c>visibleface()</c>, reduced to what material surfaces
    /// need: a water/lava/glass face is hidden when its neighbour is outside the
    /// world, entirely solid, or the same volume — regardless of neighbour size.
    /// The conservative <see cref="OctaFaceWalker.FaceVisible"/> only hides those at
    /// equal-or-coarser size, so a large water cube keeps its vertical faces where it
    /// meets finer terrain: bt_falls then renders its whole basin as a closed box,
    /// and the waterfall sheet bleeds through the level.
    /// </summary>
    private static bool Visible(Octa octa, in CubeFace face)
    {
        int dim = face.Orient >> 1;
        int dir = (face.Orient & 1) == 0 ? -1 : 1;
        int ncoord = face.Co[dim] + dir * face.Size;
        if (ncoord < 0 || ncoord >= octa.WorldSize) return false; // octa.cpp: neighbourcube -> same cube -> hidden

        var centre = face.Co;
        centre[dim] = ncoord + face.Size / 2;
        centre[(dim + 1) % 3] = face.Co[(dim + 1) % 3] + face.Size / 2;
        centre[(dim + 2) % 3] = face.Co[(dim + 2) % 3] + face.Size / 2;

        var nb = octa.LeafCube(centre);
        if (nb == null) return false;
        if (nb.IsEntirelySolid()) return false;
        return (nb.Material & Octa.MATF_VOLUME) != (face.Cube.Material & Octa.MATF_VOLUME);
    }

    private static void Emit(ushort material, in CubeFace face, Dictionary<ushort, ArrayMeshBuilder> builders)
    {
        if (!builders.TryGetValue(material, out var builder))
        {
            builder = new ArrayMeshBuilder();
            builders[material] = builder;
        }

        System.Span<Vector3I> v = stackalloc Vector3I[4];
        CubeGeometry.SolidFaceVerts(face.Orient, v);
        Vector3 p0 = MeshFace.ToWorld(v[0], face.Co, face.Size);
        Vector3 p1 = MeshFace.ToWorld(v[1], face.Co, face.Size);
        Vector3 p2 = MeshFace.ToWorld(v[2], face.Co, face.Size);
        Vector3 p3 = MeshFace.ToWorld(v[3], face.Co, face.Size);

        int dim = face.Orient >> 1;
        int cAxis = Octa.C[dim], rAxis = Octa.R[dim];
        Vector2 uv0 = PlanarUv(p0, cAxis, rAxis);
        Vector2 uv1 = PlanarUv(p1, cAxis, rAxis);
        Vector2 uv2 = PlanarUv(p2, cAxis, rAxis);
        Vector2 uv3 = PlanarUv(p3, cAxis, rAxis);

        MeshFace.Quad(builder, 0, face.Orient, p0, p1, p2, p3, uv0, uv1, uv2, uv3);
    }

    private static Vector2 PlanarUv(Vector3 p, int cAxis, int rAxis)
    {
        float[] comp = { p.X, p.Y, p.Z };
        return new Vector2(comp[cAxis] / TexScale, comp[rAxis] / TexScale);
    }
}
