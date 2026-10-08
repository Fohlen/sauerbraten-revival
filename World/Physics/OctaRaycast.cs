using Godot;

namespace SauerbratenRevival.World.Physics;

// A C# port of src/engine/physics.cpp:raycube — the hierarchical octree ray
// cast. The original is a macro-heavy DDA (INITRAYCUBE / CHECKINSIDEWORLD /
// DOWNOCTREE / FINDCLOSEST / UPOCTREE) plus a deformed-cube slab test
// (raycubeintersect: clip planes intersected with the cube's tight AABB). This
// is the algorithm the editor cursor uses (via rayent) and the future chunk
// visibility / hitscan machinery will share.
//
// Identifiers mirror the engine tokens; the macros are expanded into explicit
// C# control flow so the correspondence stays greppable. Ground truth is locked
// by tools/rayprobe (copies of the same statics, linked against octa.cpp).

/// <summary>iengine.h: <c>RAY_*</c> mode flags.</summary>
public static class OctaRaycast
{
    public const int RayBb = 1;          // entities use bounding boxes
    public const int RayPoly = 3;        // entity polygons
    public const int RayAlphaPoly = 7;   // alpha polygons too
    public const int RayEnts = 9;        // check entities at all
    public const int RayClipMat = 16;    // hit clipped (glass) material volumes
    public const int RaySkipFirst = 32;  // skip the cube the ray starts in
    public const int RayEditMat = 64;    // hit any non-air material volume
    public const int RayShadow = 128;    // shadow-ray early-out (not ported)
    public const int RayPass = 256;      // pass through grid-size cubes
    public const int RaySkipSky = 512;   // (shadow-ray only)
    public const int RaySkyTex = 1024;   // (shadow-ray only)

    /// <summary>
    /// Cast from <paramref name="o"/> along <paramref name="ray"/> through the
    /// octree. Returns the hit distance, or <paramref name="radius"/> if nothing
    /// was hit within range (the engine's convention; <paramref name="radius"/> == 0
    /// means unbounded). <paramref name="hitSurface"/> receives the struck surface
    /// normal — the cube face normal, a deformed-face plane normal, or
    /// <see cref="Vector3.Zero"/> when the quick-hit path did not record one.
    /// </summary>
    public static float RayCube(Octa octa, Vector3 o, Vector3 ray, float radius, int mode, int size, out Vector3 hitSurface)
    {
        hitSurface = Vector3.Zero;
        if (ray.X == 0f && ray.Y == 0f && ray.Z == 0f) return 0f;

        // INITRAYCUBE
        float dist = 0f, dent = radius > 0f ? radius : 1e16f;
        Vector3 v = o;
        Vector3 invray = new(
            ray.X != 0f ? 1f / ray.X : 1e16f,
            ray.Y != 0f ? 1f / ray.Y : 1e16f,
            ray.Z != 0f ? 1f / ray.Z : 1e16f);
        var levels = new Cube[octa.WorldScale + 1][];
        levels[octa.WorldScale] = octa.WorldRoot;
        int lshift = octa.WorldScale;
        int elvl = (mode & RayBb) != 0 ? octa.WorldScale : 0;
        var lsizemask = new Vector3I(invray.X > 0f ? 1 : 0, invray.Y > 0f ? 1 : 0, invray.Z > 0f ? 1 : 0);

        // CHECKINSIDEWORLD
        if (!InsideWorld(o, octa.WorldSize))
        {
            float disttoworld = 0f, exitworld = 1e16f;
            for (int i = 0; i < 3; i++)
            {
                float c = Component(v, i);
                if (c < 0f || c >= octa.WorldSize)
                {
                    float d = ((Component(invray, i) > 0f ? 0 : octa.WorldSize) - c) * Component(invray, i);
                    if (d < 0f) return radius > 0f ? radius : -1f;
                    disttoworld = Mathf.Max(disttoworld, 0.1f + d);
                }
                float e = ((Component(invray, i) > 0f ? octa.WorldSize : 0) - c) * Component(invray, i);
                exitworld = Mathf.Min(exitworld, e);
            }
            if (disttoworld > exitworld) return radius > 0f ? radius : -1f;
            v += ray * disttoworld;
            dist += disttoworld;
        }

        int closest = -1, x = (int)v.X, y = (int)v.Y, z = (int)v.Z;
        for (;;)
        {
            // DOWNOCTREE: descend from the cached node at lshift to the leaf.
            Cube[] lcArr = levels[lshift];
            Cube lc;
            for (;;)
            {
                lshift--;
                lc = lcArr[Octa.OctaStep(x, y, z, lshift)];
                // Entity hook (lc->ext->ents && lshift < elvl) is stubbed: the MVP
                // has no entity octree. disttoent lives here when entities land.
                if (lc.Children == null) break;
                lcArr = lc.Children;
                levels[lshift] = lcArr;
            }

            int lsize = 1 << lshift;
            Cube c = lc;

            // Quick hit: solid/materials/entity before the precise clip-plane test.
            if ((dist > 0f || (mode & RaySkipFirst) == 0) &&
                (((mode & RayClipMat) != 0 && Phys.IsClipped(c.Material)) ||
                 ((mode & RayEditMat) != 0 && c.Material != Octa.MAT_AIR) ||
                 ((mode & RayPass) == 0 && lsize == size && !c.IsEmpty()) ||
                 c.IsEntirelySolid() ||
                 dent < dist))
            {
                if (closest >= 0)
                {
                    hitSurface = Vector3.Zero;
                    SetComponent(ref hitSurface, closest, Component(ray, closest) > 0f ? -1f : 1f);
                }
                return Mathf.Min(dent, dist);
            }

            int loMask = ~((1 << lshift) - 1);
            var lo = new Vector3I(x & loMask, y & loMask, z & loMask);

            // Deformed-cube clip planes.
            if (!c.IsEmpty())
            {
                ClipPlanes p = OctaClipping.GetRenderClipPlanes(octa, c, lo, lsize);
                if (RayCubeIntersect(p, v, ray, invray, out float f, out hitSurface) &&
                    (dist + f > 0f || (mode & RaySkipFirst) == 0))
                    return Mathf.Min(dent, dist + f);
            }

            // FINDCLOSEST: advance to the next leaf boundary along the nearest axis.
            float dx = (lo.X + (lsizemask.X << lshift) - v.X) * invray.X;
            float dy = (lo.Y + (lsizemask.Y << lshift) - v.Y) * invray.Y;
            float dz = (lo.Z + (lsizemask.Z << lshift) - v.Z) * invray.Z;
            float disttonext = dx;
            closest = 0;
            if (dy < disttonext) { disttonext = dy; closest = 1; }
            if (dz < disttonext) { disttonext = dz; closest = 2; }
            disttonext += 0.1f;
            v += ray * disttonext;
            dist += disttonext;

            if (radius > 0f && dist >= radius) return Mathf.Min(dent, dist);

            // UPOCTREE: ascend to the level where the old and new cells share a node.
            x = (int)v.X;
            y = (int)v.Y;
            z = (int)v.Z;
            uint diff = (uint)(lo.X ^ x) | (uint)(lo.Y ^ y) | (uint)(lo.Z ^ z);
            if (diff >= (uint)octa.WorldSize) return Mathf.Min(dent, radius > 0f ? radius : dist);
            diff >>= lshift;
            if (diff == 0) return Mathf.Min(dent, radius > 0f ? radius : dist);
            do
            {
                lshift++;
                diff >>= 1;
            } while (diff != 0);
        }
    }

    /// <summary>
    /// The editor's entry point: <c>physics.cpp:rayent</c>. World cast first, then
    /// entities. Entity ray checks are not ported yet (no entity octree), so
    /// <paramref name="orient"/>/<paramref name="ent"/> are always −1 and only the
    /// world hit distance + surface are meaningful.
    /// </summary>
    public static float RayEnt(Octa octa, Vector3 o, Vector3 ray, float radius, int mode, int size,
                               out int orient, out int ent, out Vector3 hitSurface)
    {
        orient = -1;
        ent = -1;
        return RayCube(octa, o, ray, radius, mode, size, out hitSurface);
    }

    /// <summary>physics.cpp:443 <c>raycubepos</c> — hit position (clamped to <paramref name="radius"/>).</summary>
    public static Vector3 RayCubePos(Octa octa, Vector3 o, Vector3 ray, float radius, int mode, int size, out float dist)
    {
        dist = RayCube(octa, o, ray, radius, mode, size, out _);
        if (radius > 0f && dist >= radius) dist = radius;
        return ray * dist + o;
    }

    /// <summary>physics.cpp:452 <c>raycubelos</c> — line of sight between two points.</summary>
    public static bool RayCubeLos(Octa octa, Vector3 o, Vector3 dest, out Vector3 hitPos)
    {
        Vector3 ray = dest - o;
        float mag = ray.Length();
        ray /= mag;
        hitPos = RayCubePos(octa, o, ray, mag, RayClipMat | RayPoly, 0, out float distance);
        return distance >= mag;
    }

    /// <summary>physics.cpp:93 <c>raycubeintersect</c> — slab test: clip planes ∩ tight AABB.</summary>
    private static bool RayCubeIntersect(ClipPlanes p, Vector3 v, Vector3 ray, Vector3 invray,
                                         out float dist, out Vector3 hitSurface)
    {
        int entry = -1, bbentry = -1;
        float enterdist = -1e16f, exitdist = 1e16f;

        // INTERSECTPLANES
        for (int i = 0; i < p.Size; i++)
        {
            float pdist = p.P[i].Dist(v);
            float facing = ray.Dot(p.P[i].N);
            if (facing < 0f)
            {
                pdist /= -facing;
                if (pdist > enterdist)
                {
                    if (pdist > exitdist) { dist = 0f; hitSurface = Vector3.Zero; return false; }
                    enterdist = pdist;
                    entry = i;
                }
            }
            else if (facing > 0f)
            {
                pdist /= -facing;
                if (pdist < exitdist)
                {
                    if (pdist < enterdist) { dist = 0f; hitSurface = Vector3.Zero; return false; }
                    exitdist = pdist;
                }
            }
            else if (pdist > 0f) { dist = 0f; hitSurface = Vector3.Zero; return false; }
        }

        // INTERSECTBOX
        for (int i = 0; i < 3; i++)
        {
            if (Component(ray, i) != 0f)
            {
                float prad = Mathf.Abs(Component(p.R, i) * Component(invray, i));
                float pdist = (Component(p.O, i) - Component(v, i)) * Component(invray, i);
                float pmin = pdist - prad, pmax = pdist + prad;
                if (pmin > enterdist)
                {
                    if (pmin > exitdist) { dist = 0f; hitSurface = Vector3.Zero; return false; }
                    enterdist = pmin;
                    bbentry = i;
                }
                if (pmax < exitdist)
                {
                    if (pmax < enterdist) { dist = 0f; hitSurface = Vector3.Zero; return false; }
                    exitdist = pmax;
                }
            }
            else if (Component(v, i) < Component(p.O, i) - Component(p.R, i) ||
                     Component(v, i) > Component(p.O, i) + Component(p.R, i))
            { dist = 0f; hitSurface = Vector3.Zero; return false; }
        }

        if (exitdist < 0f) { dist = 0f; hitSurface = Vector3.Zero; return false; }
        dist = Mathf.Max(enterdist + 0.1f, 0f);
        if (bbentry >= 0)
        {
            hitSurface = Vector3.Zero;
            SetComponent(ref hitSurface, bbentry, Component(ray, bbentry) > 0f ? -1f : 1f);
        }
        else
        {
            hitSurface = p.P[entry].N;
        }
        return true;
    }

    private static bool InsideWorld(Vector3 o, int worldSize) =>
        o.X >= 0f && o.X < worldSize && o.Y >= 0f && o.Y < worldSize && o.Z >= 0f && o.Z < worldSize;

    private static float Component(Vector3 v, int i) => i switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static void SetComponent(ref Vector3 v, int i, float value)
    {
        switch (i)
        {
            case 0: v.X = value; break;
            case 1: v.Y = value; break;
            default: v.Z = value; break;
        }
    }
}
