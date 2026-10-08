using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Editor;

/// <summary>
/// The editor's world ray cast — now a thin seam over the real
/// <c>physics.cpp:raycube</c> port (<see cref="OctaRaycast"/>). It casts with the
/// editor's mode (<c>RAY_SKIPFIRST</c>, size = grid size, matching
/// <c>octaedit.cpp:rendereditcursor</c> → <c>rayent</c>) and maps the struck surface
/// normal back to an axis-aligned orientation (0..5) for the cursor; a deformed-face
/// plane normal has no single axis and yields −1.
///
/// Ground truth for <see cref="OctaRaycast.RayCube"/> is locked by tools/rayprobe.
/// </summary>
public static class OctaRay
{
    /// <summary>Cast without a grid constraint (size 0): only entirely-solid / material hits.</summary>
    public static float RayCube(Octa octa, Vector3 o, Vector3 dir, float maxDist, out int orient) =>
        RayCube(octa, o, dir, maxDist, 0, out orient);

    /// <summary>
    /// Cast the camera eye ray through the octree and return the hit distance
    /// (or <paramref name="maxDist"/> for open air). <paramref name="gridSize"/>
    /// is the editor grid: a non-empty cube exactly that size is a hit, so the
    /// cursor selects at the current grid resolution.
    /// </summary>
    public static float RayCube(Octa octa, Vector3 o, Vector3 dir, float maxDist, int gridSize, out int orient)
    {
        float dist = OctaRaycast.RayCube(octa, o, dir, maxDist, OctaRaycast.RaySkipFirst, gridSize, out Vector3 hit);
        orient = OrientOf(hit);
        return dist;
    }

    private static int OrientOf(Vector3 n)
    {
        int axes = 0, idx = -1;
        for (int i = 0; i < 3; i++)
        {
            if (Component(n, i) != 0f) { axes++; idx = i; }
        }
        if (axes != 1) return -1;
        return idx * 2 + (Component(n, idx) > 0f ? 1 : 0);
    }

    private static float Component(Vector3 v, int i) => i switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}
