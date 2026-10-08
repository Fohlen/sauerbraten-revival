using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Physics;

// Deformed-cube collision (src/engine/octa.cpp genclipplanes + physics.cpp
// fuzzycollideplanes). A `Normal` cube's solid geometry is a convex hull of its
// 8 edge-derived corner verts; collision runs the player capsule against that
// hull's planes. Names mirror the engine.
//
// Simplification vs the engine: genclipplanes() here uses every collidable face
// (visibletris() occlusion is not ported), which yields a superset of the hull
// planes. A superset can only make the hull tighter, never looser, so it does not
// let the player through geometry; verify against the A3 replay.

/// <summary>geom.h:1010 <c>plane</c>.</summary>
public struct Plane
{
    public Vector3 N;
    public float Offset;
    public float Dist(Vector3 p) => N.Dot(p) + Offset;
    public void ToPlane(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 n = (b - a).Cross(c - a);
        float m = n.Length();
        if (m == 0) { N = Vector3.Zero; Offset = 0; return; }
        n /= m;
        N = n;
        Offset = -n.Dot(a);
    }
}

/// <summary>octa.h:160 <c>clipplanes</c>.</summary>
public sealed class ClipPlanes
{
    public Vector3 O, R;
    public Vector3[] V = new Vector3[8];
    public Plane[] P = new Plane[12];
    public byte[] Side = new byte[12];
    public byte Size;
    public int Visible;
}

/// <summary>mpr.h:112 <c>EntFuzzy</c>/<c>EntCapsule</c>.</summary>
public sealed class EntCapsule
{
    private readonly PhysEnt _e;
    public EntCapsule(PhysEnt e) { _e = e; }

    public Vector3 Center => new(_e.O.X, _e.O.Y, _e.O.Z + (_e.AboveEye - _e.EyeHeight) / 2);
    public float Left => _e.O.X - _e.Radius;
    public float Right => _e.O.X + _e.Radius;
    public float Back => _e.O.Y - _e.Radius;
    public float Front => _e.O.Y + _e.Radius;
    public float Bottom => _e.O.Z - _e.EyeHeight;
    public float Top => _e.O.Z + _e.AboveEye;

    public Vector3 SupportPoint(Vector3 n)
    {
        Vector3 p = _e.O;
        if (n.Z > 0) p.Z += _e.AboveEye - _e.Radius; else p.Z -= _e.EyeHeight - _e.Radius;
        float mag = n.Length();
        if (mag > 0) p += n * (_e.Radius / mag);
        return p;
    }
}

public static class OctaClipping
{
    // octa.cpp:666 cubecoords / GENCUBEVERTS(0,1,...): corner bits [n] = (x,y,z).
    private static readonly int[][] CubeVert =
    {
        new[] { 1, 1, 0 }, new[] { 0, 1, 0 }, new[] { 0, 1, 1 }, new[] { 1, 1, 1 },
        new[] { 1, 0, 1 }, new[] { 0, 0, 1 }, new[] { 0, 0, 0 }, new[] { 1, 0, 0 },
    };

    // octa.cpp:693 fv[6][4]: corner indices per face orientation.
    private static readonly int[][] Fv =
    {
        new[] { 2, 1, 6, 5 }, new[] { 3, 4, 7, 0 }, new[] { 4, 5, 6, 7 },
        new[] { 1, 2, 3, 0 }, new[] { 6, 1, 0, 7 }, new[] { 5, 4, 3, 2 },
    };

    private static readonly Dictionary<Cube, ClipPlanes> Cache = new();

    /// <summary>octa.cpp:660 <c>gencubevert</c> (0..8 units from the cube edges).</summary>
    public static Vector3I GenCubeVert(Cube c, int i)
    {
        var b = CubeVert[i];
        return new Vector3I(
            CubeGeometry.EdgeGet(c.CubeEdge(0, b[1], b[2]), b[0]),
            CubeGeometry.EdgeGet(c.CubeEdge(1, b[2], b[0]), b[1]),
            CubeGeometry.EdgeGet(c.CubeEdge(2, b[0], b[1]), b[2]));
    }

    /// <summary>octa.cpp:653 <c>calcvert</c> (non-solid path, world position).</summary>
    public static Vector3 CalcVert(Cube c, Vector3I co, int size, int i)
    {
        var v = GenCubeVert(c, i);
        return new Vector3(co.X + v.X * (size / 8f), co.Y + v.Y * (size / 8f), co.Z + v.Z * (size / 8f));
    }

    /// <summary>octa.cpp:1204 <c>genclipplanes</c> (simplified; see file header).</summary>
    public static ClipPlanes GetClipPlanes(Octa octa, Cube c, Vector3I co, int size, int visibleMask)
    {
        if (Cache.TryGetValue(c, out var cached)) return cached;

        var p = new ClipPlanes();
        for (int i = 0; i < 8; i++) p.V[i] = CalcVert(c, co, size, i);
        Vector3 mn = p.V[0], mx = p.V[0];
        for (int i = 1; i < 8; i++) { mn = mn.Min(p.V[i]); mx = mx.Max(p.V[i]); }
        p.R = (mx - mn) * 0.5f;
        p.O = mn + p.R;
        p.Size = 0;
        p.Visible = 0;

        for (int i = 0; i < 6; i++)
        {
            if ((visibleMask & (1 << i)) == 0) continue;
            if (c.FlatAxisFace(i)) { p.Visible |= 1 << i; continue; }

            int vis = OctaVisible.VisibleTris(octa, c, i, co, size, Phys.MatNoClip, Phys.MatFClip);
            if (vis == 0) continue;
            int convex = c.FaceConvexity(i);
            int order = (vis & 4) != 0 || convex < 0 ? 1 : 0;
            Vector3 v0 = p.V[Fv[i][order]], v1 = p.V[Fv[i][(order + 1) & 3]],
                    v2 = p.V[Fv[i][(order + 2) & 3]], v3 = p.V[Fv[i][(order + 3) & 3]];
            if ((vis & 1) != 0) { p.Side[p.Size] = (byte)i; p.P[p.Size++].ToPlane(v0, v1, v2); }
            if ((vis & 2) != 0 && ((vis & 1) == 0 || convex != 0)) { p.Side[p.Size] = (byte)i; p.P[p.Size++].ToPlane(v0, v2, v3); }
        }

        Cache[c] = p;
        return p;
    }

    /// <summary>
    /// octa.cpp:1204 <c>genclipplanes</c> with <c>collide=false</c>: the render hull
    /// used by <c>raycube</c>/<c>shadowray</c>. All six faces are tested with the
    /// default <c>visibletris</c> (MAT_ALPHA/MAT_ALPHA) rather than the collision
    /// mask, and a plane is emitted for each surviving triangle. Not cached: the
    /// ray cast visits a handful of leaves per call, and this path depends on the
    /// neighbour geometry at <paramref name="co"/>.
    /// </summary>
    public static ClipPlanes GetRenderClipPlanes(Octa octa, Cube c, Vector3I co, int size)
    {
        var p = new ClipPlanes();
        for (int i = 0; i < 8; i++) p.V[i] = CalcVert(c, co, size, i);
        Vector3 mn = p.V[0], mx = p.V[0];
        for (int i = 1; i < 8; i++) { mn = mn.Min(p.V[i]); mx = mx.Max(p.V[i]); }
        p.R = (mx - mn) * 0.5f;
        p.O = mn + p.R;
        p.Size = 0;
        p.Visible = 0;

        for (int i = 0; i < 6; i++)
        {
            int vis = OctaVisible.VisibleTris(octa, c, i, co, size);
            if (vis == 0) continue;
            if (c.FlatAxisFace(i)) { p.Visible |= 1 << i; continue; }

            int convex = c.FaceConvexity(i);
            int order = (vis & 4) != 0 || convex < 0 ? 1 : 0;
            Vector3 v0 = p.V[Fv[i][order]], v1 = p.V[Fv[i][(order + 1) & 3]],
                    v2 = p.V[Fv[i][(order + 2) & 3]], v3 = p.V[Fv[i][(order + 3) & 3]];
            if ((vis & 1) != 0) { p.Side[p.Size] = (byte)i; p.P[p.Size++].ToPlane(v0, v1, v2); }
            if ((vis & 2) != 0 && ((vis & 1) == 0 || convex != 0)) { p.Side[p.Size] = (byte)i; p.P[p.Size++].ToPlane(v0, v2, v3); }
        }

        return p;
    }

    /// <summary>physics.cpp:795 <c>clampcollide</c>.</summary>
    public static bool ClampCollide(ClipPlanes p, EntCapsule entvol, Plane w, Vector3 pw)
    {
        Vector3 c = entvol.Center;
        if (w.N.X != 0 && (w.N.Y != 0 || w.N.Z != 0) && Mathf.Abs(pw.X - p.O.X) > p.R.X)
        {
            float fv = pw.X < p.O.X ? p.O.X - p.R.X : p.O.X + p.R.X;
            float fdist = (w.N.X * fv + w.N.Y * c.Y + w.N.Z * c.Z + w.Offset) / (w.N.Y * w.N.Y + w.N.Z * w.N.Z);
            var fdir = new Vector3(fv - c.X, -w.N.Y * fdist, -w.N.Z * fdist);
            if ((pw.Y - c.Y - fdir.Y) * w.N.Y + (pw.Z - c.Z - fdir.Z) * w.N.Z >= 0 &&
                entvol.SupportPoint(fdir).DistanceSquaredTo(c) < fdir.LengthSquared()) return true;
        }
        if (w.N.Y != 0 && (w.N.X != 0 || w.N.Z != 0) && Mathf.Abs(pw.Y - p.O.Y) > p.R.Y)
        {
            float fv = pw.Y < p.O.Y ? p.O.Y - p.R.Y : p.O.Y + p.R.Y;
            float fdist = (w.N.X * c.X + w.N.Y * fv + w.N.Z * c.Z + w.Offset) / (w.N.X * w.N.X + w.N.Z * w.N.Z);
            var fdir = new Vector3(-w.N.X * fdist, fv - c.Y, -w.N.Z * fdist);
            if ((pw.X - c.X - fdir.X) * w.N.X + (pw.Z - c.Z - fdir.Z) * w.N.Z >= 0 &&
                entvol.SupportPoint(fdir).DistanceSquaredTo(c) < fdir.LengthSquared()) return true;
        }
        if (w.N.Z != 0 && (w.N.X != 0 || w.N.Y != 0) && Mathf.Abs(pw.Z - p.O.Z) > p.R.Z)
        {
            float fv = pw.Z < p.O.Z ? p.O.Z - p.R.Z : p.O.Z + p.R.Z;
            float fdist = (w.N.X * c.X + w.N.Y * c.Y + w.N.Z * fv + w.Offset) / (w.N.X * w.N.X + w.N.Y * w.N.Y);
            var fdir = new Vector3(-w.N.X * fdist, -w.N.Y * fdist, fv - c.Z);
            if ((pw.X - c.X - fdir.X) * w.N.X + (pw.Y - c.Y - fdir.Y) * w.N.Y >= 0 &&
                entvol.SupportPoint(fdir).DistanceSquaredTo(c) < fdir.LengthSquared()) return true;
        }
        return false;
    }
}

file static class VectorExt
{
    public static float DistanceSquaredTo(this Vector3 a, Vector3 b) => (a - b).LengthSquared();
}
