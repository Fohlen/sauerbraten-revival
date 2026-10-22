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
    public readonly int Vis;

    public CubeFace(Cube cube, int orient, Vector3I co, int size, int vis = 3)
    {
        Cube = cube;
        Orient = orient;
        Co = co;
        Size = size;
        Vis = vis;
    }
}

/// <summary>
/// Shared octree traversal for the meshers (the face walk of octarender.cpp). It
/// descends to the leaf cubes, skips cubes the caller does not care about, tests
/// each face against its neighbour, and hands the surviving faces to a callback.
/// Per-face <em>policy</em> (which texture/material, which UVs) stays with the
/// caller — this only owns the traversal and the visibility cull.
/// </summary>
public static class OctaFaceWalker
{
    /// <param name="include">Cubes to mesh (e.g. non-empty, or material volumes).</param>
    /// <param name="hideSameMaterial">Use material-volume occlusion (<see cref="OctaEdit.VisibleFace"/>) instead of <see cref="OctaVisible.VisibleTris"/>.</param>
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
            {
                int vis = FaceVisibleTris(octa, c, orient, childCo, size, hideSameMaterial);
                if (vis != 0)
                    emit(new CubeFace(c, orient, childCo, size, vis));
            }
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
            {
                int vis = FaceVisibleTris(octa, c, orient, childCo, size, hideSameMaterial);
                if (vis != 0)
                    emit(new CubeFace(c, orient, childCo, size, vis));
            }
        }
    }

    /// <summary>
    /// Exact visibility mask for one leaf face: <see cref="OctaVisible.VisibleTris"/>
    /// (<c>octa.cpp:1094 visibletris</c>) for world geometry, or
    /// <see cref="OctaEdit.VisibleFace"/> (<c>octa.cpp:1013 visibleface</c>) for
    /// material volume boundaries (<paramref name="hideSameMaterial"/>).
    /// Returns 0 when completely occluded/collapsed; bits 0..1 select the visible
    /// triangles and bit 2 (<c>4</c>) flips the quad diagonal order.
    /// </summary>
    public static int FaceVisibleTris(Octa octa, Cube c, int orient, Vector3I co, int size, bool hideSameMaterial = false)
    {
        if (hideSameMaterial)
        {
            ushort volume = (ushort)(c.Material & Octa.MATF_VOLUME);
            return OctaEdit.VisibleFace(octa, c, orient, co, size, volume, Octa.MAT_AIR, Octa.MATF_VOLUME) ? 3 : 0;
        }
        return OctaVisible.VisibleTris(octa, c, orient, co, size);
    }

    /// <summary>Whether any triangle of the face survives occlusion culling.</summary>
    public static bool FaceVisible(Octa octa, Cube c, int orient, Vector3I co, int size, bool hideSameMaterial)
        => FaceVisibleTris(octa, c, orient, co, size, hideSameMaterial) != 0;
}
