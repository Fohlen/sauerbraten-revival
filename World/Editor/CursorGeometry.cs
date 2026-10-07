using Godot;
using SauerbratenRevival.World;

namespace SauerbratenRevival.World.Editor;

/// <summary>
/// Pure geometry helpers for the editor cursor — ports of the ray math the original
/// uses to place the selection box (src/shared/geom.cpp, src/engine/octaedit.cpp).
/// Float vectors are Godot <see cref="Vector3"/>; cube coordinates are
/// <see cref="Vector3I"/> (integer octree space).
/// </summary>
public static class CursorGeometry
{
    /// <summary>
    /// geom.cpp:66 <c>rayboxintersect(b, s, o, ray, dist, orient)</c> — intersect a
    /// ray from <paramref name="o"/> with the axis-aligned box at
    /// <paramref name="b"/> of size <paramref name="s"/>. Returns the nearest slab
    /// hit and the face orientation it struck (<c>2*d + dc</c>), or false.
    /// </summary>
    public static bool RayBoxIntersect(Vector3 b, Vector3 s, Vector3 o, Vector3 ray,
        out float dist, out int orient)
    {
        dist = 0f;
        orient = 0;
        float best = float.MaxValue;
        bool found = false;
        for (int d = 0; d < 3; d++)
        {
            float rd = Component(ray, d);
            if (rd == 0f) continue;
            int dc = rd < 0f ? 1 : 0;
            float bd = Component(b, d), sd = Component(s, d);
            float pdist = (bd + sd * dc - Component(o, d)) / rd;
            var v = ray * pdist + o;
            int rr = Octa.R[d], cc = Octa.C[d];
            if (Component(v, rr) >= Component(b, rr) && Component(v, rr) <= Component(b, rr) + Component(s, rr)
             && Component(v, cc) >= Component(b, cc) && Component(v, cc) <= Component(b, cc) + Component(s, cc))
            {
                if (pdist < best)
                {
                    best = pdist;
                    orient = 2 * d + dc;
                    found = true;
                }
            }
        }
        if (found) dist = best;
        return found;
    }

    /// <summary>
    /// octaedit.cpp:350 <c>editmoveplane(o, ray, d, off, handle, dest, first)</c> —
    /// project the cursor ray onto the plane <c>axis d == off</c>, giving the drag
    /// destination. <paramref name="planeOrigin"/> is the selection origin (used
    /// only to capture the grab <paramref name="handle"/>); <paramref name="rayOrigin"/>
    /// is the eye. The handle is captured on the first call and removed from
    /// <paramref name="dest"/> thereafter.
    /// </summary>
    public static bool EditMovePlane(Vector3 planeOrigin, Vector3 rayOrigin, Vector3 ray, int d, float off,
        ref Vector3 handle, out Vector3 dest, bool first)
    {
        float denom = Component(ray, d);
        if (denom == 0f) { dest = Vector3.Zero; return false; }
        float dist = (off - Component(rayOrigin, d)) / denom;
        dest = ray * dist + rayOrigin;
        if (first) handle = dest - planeOrigin;
        dest -= handle;
        return true;
    }

    private static float Component(Vector3 v, int i) => i switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };
}
