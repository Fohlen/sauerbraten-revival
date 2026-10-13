using System;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// Mathematical primitives for weapon rays and hit detection ported from Cube 2: Sauerbraten
/// (<c>src/shared/geom.cpp</c>, <c>src/shared/tools.cpp</c>, <c>src/fpsgame/weapon.cpp</c>).
/// </summary>
public static class WeaponRay
{
    /// <summary>
    /// Port of <c>src/shared/geom.cpp:85 linecylinderintersect</c>.
    /// Tests line segment intersection against an arbitrary finite cylinder defined by
    /// axis segment [<paramref name="start"/>, <paramref name="end"/>] and <paramref name="radius"/>.
    /// Returns <c>true</c> if the segment [<paramref name="from"/>, <paramref name="to"/>] hits the cylinder
    /// within [0, 1] parameter space, with <paramref name="dist"/> receiving the normalized distance [0, 1].
    /// </summary>
    public static bool LineCylinderIntersect(
        Vector3 from,
        Vector3 to,
        Vector3 start,
        Vector3 end,
        float radius,
        out float dist)
    {
        dist = 0f;
        Vector3 d = end - start;
        Vector3 m = from - start;
        Vector3 n = to - from;

        float md = m.Dot(d);
        float nd = n.Dot(d);
        float dd = d.LengthSquared();

        if (md < 0f && md + nd < 0f) return false;
        if (md > dd && md + nd > dd) return false;

        float nn = n.LengthSquared();
        float mn = m.Dot(n);
        float a = dd * nn - nd * nd;
        float k = m.LengthSquared() - radius * radius;
        float c = dd * k - md * md;

        if (MathF.Abs(a) < 0.005f)
        {
            if (c > 0f) return false;
            if (md < 0f) dist = -mn / nn;
            else if (md > dd) dist = (nd - mn) / nn;
            else dist = 0f;
            return dist >= 0f && dist <= 1f;
        }
        else if (c > 0f)
        {
            float b = dd * mn - nd * md;
            float discrim = b * b - a * c;
            if (discrim < 0f) return false;
            dist = (-b - MathF.Sqrt(discrim)) / a;
        }
        else
        {
            dist = 0f;
        }

        float offset = md + dist * nd;
        if (offset < 0f)
        {
            if (nd <= 0f) return false;
            dist = -md / nd;
            if (k + dist * (2f * mn + dist * nn) > 0f) return false;
        }
        else if (offset > dd)
        {
            if (nd >= 0f) return false;
            dist = (dd - md) / nd;
            if (k + dd - 2f * md + dist * (2f * (mn - nd) + dist * nn) > 0f) return false;
        }

        return dist >= 0f && dist <= 1f;
    }

    /// <summary>
    /// Port of <c>src/fpsgame/weapon.cpp:706 intersect(dynent *d, ...)</c>.
    /// Tests line segment against an upright entity cylinder centered at <paramref name="ent.O"/>
    /// spanning <c>[O.Z - EyeHeight, O.Z + AboveEye]</c> with <paramref name="ent.Radius"/>.
    /// </summary>
    public static bool IntersectEntity(PhysEnt ent, Vector3 from, Vector3 to, out float dist)
    {
        Vector3 bottom = ent.O;
        bottom.Z -= ent.EyeHeight;
        Vector3 top = ent.O;
        top.Z += ent.AboveEye;
        return LineCylinderIntersect(from, to, bottom, top, ent.Radius, out dist);
    }

    /// <summary>
    /// Mersenne Twister (MT19937) random generator matching Cube 2's <c>src/shared/tools.cpp:randomMT</c>.
    /// </summary>
    public sealed class Mt19937
    {
        private const int N = 624;
        private const int M = 397;
        private const uint K = 0x9908B0DFU;

        private readonly uint[] _state = new uint[N];
        private int _next = N;

        public Mt19937(uint seed = 5489U)
        {
            Seed(seed);
        }

        public void Seed(uint seed)
        {
            _state[0] = seed;
            for (uint i = 1; i < N; i++)
            {
                seed = 1812433253U * (seed ^ (seed >> 30)) + i;
                _state[i] = seed;
            }
            _next = 0;
        }

        public uint Next()
        {
            int cur = _next;
            if (++_next >= N)
            {
                if (_next > N)
                {
                    Seed(5489U);
                    cur = _next++;
                }
                else
                {
                    _next = 0;
                }
            }

            uint y = (_state[cur] & 0x80000000U) | (_state[_next] & 0x7FFFFFFFU);
            y = _state[cur < N - M ? cur + M : cur + M - N] ^ (y >> 1) ^ ((y & 1U) != 0 ? K : 0U);
            _state[cur] = y;

            y ^= y >> 11;
            y ^= (y << 7) & 0x9D2C5680U;
            y ^= (y << 15) & 0xEFC60000U;
            y ^= y >> 18;
            return y;
        }

        /// <summary>
        /// Matches <c>#define rndscale(x) (float((randomMT()&amp;0x7FFFFFFF)*double(x)/double(0x7FFFFFFF)))</c>.
        /// </summary>
        public float RndScale(double x)
        {
            return (float)((Next() & 0x7FFFFFFFU) * x / 0x7FFFFFFFU);
        }
    }

    /// <summary>
    /// Calculates the perturbed target endpoint for ray spread before collision checking,
    /// ported from <c>src/fpsgame/weapon.cpp:118 offsetray</c>.
    /// </summary>
    public static Vector3 CalcRayOffset(Vector3 from, Vector3 to, int spread, Mt19937 rng)
    {
        Vector3 offset;
        do
        {
            offset = new Vector3(rng.RndScale(1.0), rng.RndScale(1.0), rng.RndScale(1.0)) - new Vector3(0.5f, 0.5f, 0.5f);
        }
        while (offset.LengthSquared() > 0.5f * 0.5f);

        offset *= (to.DistanceTo(from) / 1024f) * spread;
        offset.Z /= 2f;
        return to + offset;
    }
}
