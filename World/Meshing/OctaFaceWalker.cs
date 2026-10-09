using System;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Meshing;

/// <summary>One leaf-cube face, in Sauerbraten world coordinates.</summary>
public readonly struct CubeFace
{
    public readonly Cube Cube;
    public readonly int Orient;
    public readonly Vector3I Co;
    public readonly int Size;

    public CubeFace(Cube cube, int orient, Vector3I co, int size)
    {
        Cube = cube;
        Orient = orient;
        Co = co;
        Size = size;
    }
}

/// <summary>
/// Shared octree traversal for the meshers (the face walk of octarender.cpp). It
/// descends to the leaf cubes, skips cubes the caller does not care about, tests
/// each face against its neighbour, and hands the surviving faces to a callback.
/// Per-face <em>policy</em> (which texture/material, which UVs) stays with the
/// caller — this only owns the traversal and the conservative cull.
/// </summary>
public static class OctaFaceWalker
{
    /// <param name="include">Cubes to mesh (e.g. non-empty, or material volumes).</param>
    /// <param name="hideSameMaterial">Also hide faces whose neighbour is the same material volume (material surfaces).</param>
    public static void Walk(Octa octa, Func<Cube, bool> include, bool hideSameMaterial, Action<CubeFace> emit)
        => Walk(octa.WorldRoot, Vector3I.Zero, octa.WorldSize >> 1, octa, include, hideSameMaterial, emit);

    /// <summary>
    /// Walk only the part of the world overlapping <paramref name="region"/> (a
    /// world-space AABB in Sauerbraten coordinates). Used for incremental chunk
    /// rebuilds so a single chunk does not traverse the whole octree. A face is
    /// emitted when its leaf origin lies inside the region; the region should be the
    /// chunk expanded to catch faces of leaves straddling the boundary.
    /// </summary>
    public static void WalkRegion(Octa octa, Vector3I min, Vector3I max,
                                  Func<Cube, bool> include, bool hideSameMaterial, Action<CubeFace> emit)
        => WalkRegion(octa.WorldRoot, Vector3I.Zero, octa.WorldSize >> 1, octa, min, max, include, hideSameMaterial, emit);

    private static void WalkRegion(Cube[] cubes, Vector3I co, int size, Octa octa,
                                   Vector3I min, Vector3I max,
                                   Func<Cube, bool> include, bool hideSameMaterial, Action<CubeFace> emit)
    {
        for (int i = 0; i < 8; i++)
        {
            var childCo = new Vector3I(
                co.X + ((i & 1) >> 0) * size,
                co.Y + ((i & 2) >> 1) * size,
                co.Z + ((i & 4) >> 2) * size);
            // Skip this octant if it does not overlap [min, max).
            if (childCo.X + size <= min.X || childCo.X >= max.X ||
                childCo.Y + size <= min.Y || childCo.Y >= max.Y ||
                childCo.Z + size <= min.Z || childCo.Z >= max.Z) continue;

            var c = cubes[i];
            if (c.Children != null)
            {
                WalkRegion(c.Children, childCo, size >> 1, octa, min, max, include, hideSameMaterial, emit);
                continue;
            }

            if (!include(c)) continue;
            for (int orient = 0; orient < 6; orient++)
                if (FaceVisible(octa, c, orient, childCo, size, hideSameMaterial))
                    emit(new CubeFace(c, orient, childCo, size));
        }
    }

    private static void Walk(Cube[] cubes, Vector3I co, int size, Octa octa,
                             Func<Cube, bool> include, bool hideSameMaterial, Action<CubeFace> emit)
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
                Walk(c.Children, childCo, size >> 1, octa, include, hideSameMaterial, emit);
                continue;
            }

            if (!include(c)) continue;

            for (int orient = 0; orient < 6; orient++)
                if (FaceVisible(octa, c, orient, childCo, size, hideSameMaterial))
                    emit(new CubeFace(c, orient, childCo, size));
        }
    }

    /// <summary>
    /// Conservative cull: hide a face when its neighbour occludes it. A neighbour
    /// entirely solid, or solid across the shared face (engine
    /// <c>visibletris</c> - "touchingface" with all-solid edges), hides the face,
    /// as does (with <paramref name="hideSameMaterial"/>) the same material volume.
    /// The neighbour leaf must be at least as large as this face; anything finer or
    /// empty is drawn, which can overdraw but never leaves a hole.
    /// </summary>
    public static bool FaceVisible(Octa octa, Cube c, int orient, Vector3I co, int size, bool hideSameMaterial)
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
        // octa.cpp visibletris: occluded when entirely solid, OR when the neighbour
        // is solid across the shared face (touchingface + all-solid edges) even if
        // not wholly solid. Omitting the second clause draws coplanar faces the
        // engine culls.
        int opp = orient ^ 1;
        if (nsize >= size &&
            (nb.IsEntirelySolid() || (nb.TouchingFace(opp) && OctaVisible.FaceEdges(nb, opp) == Octa.F_SOLID)))
            return false;
        if (hideSameMaterial && (nb.Material & Octa.MATF_VOLUME) == (c.Material & Octa.MATF_VOLUME) && nsize >= size)
            return false;
        return true;
    }
}
