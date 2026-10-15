using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// A hit detected during hitscan resolution (port of engine <c>hitmsg</c> / target resolution).
/// </summary>
public sealed class HitscanTargetHit<TTarget> where TTarget : class
{
    public TTarget Target { get; set; } = null!;
    public int Damage { get; set; }
    public int Rays { get; set; }
    public Vector3 HitPos { get; set; }
    public Vector3 PushDir { get; set; }
    public Vector3 PushVel { get; set; }
}

/// <summary>
/// Result of a full hitscan attack execution, containing hit targets and final ray endpoints.
/// </summary>
public sealed class HitscanResult<TTarget> where TTarget : class
{
    public Vector3 From { get; set; }
    public Vector3 To { get; set; }
    public Vector3 KickbackVel { get; set; }
    public List<Vector3> RayEndpoints { get; } = new();
    public List<HitscanTargetHit<TTarget>> Hits { get; } = new();
}

/// <summary>
/// A candidate target for hitscan rays and explosions.
/// </summary>
public interface IHitscanTarget : IExplosionTarget
{
}

/// <summary>
/// Complete hitscan ray tracing and hit resolution engine ported from
/// <c>src/fpsgame/weapon.cpp:shoot/raydamage/hitpush</c>.
/// </summary>
public static class WeaponHitscan
{
    /// <summary>
    /// Executes a hitscan shot from <paramref name="from"/> towards <paramref name="targetPos"/>.
    /// Handles world octree collision, spread/multi-ray generation, cylinder target testing,
    /// ray shortening, shooter kickback, and target hitpush/damage calculation.
    /// </summary>
    public static HitscanResult<TTarget> Fire<TTarget>(
        Octa? world,
        Vector3 from,
        Vector3 targetPos,
        WeaponInfo gun,
        bool quadDamage,
        IReadOnlyList<TTarget> candidates,
        WeaponRay.Mt19937? rng = null)
        where TTarget : class, IHitscanTarget
    {
        rng ??= new WeaponRay.Mt19937();
        var result = new HitscanResult<TTarget>
        {
            From = from
        };

        Vector3 to = targetPos;
        Vector3 dir = (to - from).Normalized();
        float dist = from.DistanceTo(to);

        // Shooter recoil / kickback (weapon.cpp:798)
        // vec kickback = vec(dir).mul(guns[d->gunselect].kickamount*-2.5f);
        result.KickbackVel = dir * (gun.KickAmount * -2.5f);

        // Range clamp (weapon.cpp:801)
        float shorten = 0f;
        if (gun.Range > 0 && dist > gun.Range)
            shorten = gun.Range;

        // World barrier raycast (weapon.cpp:803)
        // raycube(d->o, dir, dist, RAY_CLIPMAT|RAY_ALPHAPOLY)
        if (world != null)
        {
            float barrier = OctaRaycast.RayCube(
                world,
                from,
                dir,
                dist,
                OctaRaycast.RayClipMat | OctaRaycast.RayAlphaPoly,
                0,
                out _);

            if (barrier > 0f && barrier < dist && (shorten == 0f || barrier < shorten))
                shorten = barrier;
        }

        if (shorten > 0f)
            to = from + dir * shorten;

        result.To = to;

        // Spread & Multi-ray generation (weapon.cpp:808-810)
        int rayCount = Math.Max(1, gun.Rays);
        var rayEnds = new Vector3[rayCount];

        if (gun.Rays > 1)
        {
            for (int i = 0; i < gun.Rays; i++)
                rayEnds[i] = WeaponRay.CalcRayOffset(from, to, gun.Spread, rng);
        }
        else if (gun.Spread > 0)
        {
            rayEnds[0] = WeaponRay.CalcRayOffset(from, to, gun.Spread, rng);
        }
        else
        {
            rayEnds[0] = to;
        }

        // Ray damage and intersection (weapon.cpp:738 raydamage)
        int qdam = gun.Damage * (quadDamage ? 4 : 1);

        if (gun.Rays > 1)
        {
            var hits = new TTarget?[rayCount];
            var hitDists = new float[rayCount];

            for (int i = 0; i < rayCount; i++)
            {
                hits[i] = IntersectClosest(from, rayEnds[i], candidates, out float hitDist);
                hitDists[i] = hitDist;
                if (hits[i] != null)
                {
                    // shorten(from, rays[i], dist)
                    rayEnds[i] = from + (rayEnds[i] - from) * Math.Min(1.0f, hitDist);
                }
            }

            // Aggregate multiple rays hitting the same target (weapon.cpp:754-765)
            for (int i = 0; i < rayCount; i++)
            {
                if (hits[i] == null) continue;
                TTarget target = hits[i]!;
                hits[i] = null;
                int numHits = 1;

                for (int j = i + 1; j < rayCount; j++)
                {
                    if (hits[j] == target)
                    {
                        hits[j] = null;
                        numHits++;
                    }
                }

                int totalDamage = numHits * qdam;
                Vector3 hitPushDir = (to - from).Normalized();
                int weight = Math.Max(1, target.Weight);
                Vector3 pushVel = hitPushDir * (gun.HitPush * totalDamage / weight);

                var hitRecord = new HitscanTargetHit<TTarget>
                {
                    Target = target,
                    Damage = totalDamage,
                    Rays = numHits,
                    HitPos = rayEnds[i],
                    PushDir = hitPushDir,
                    PushVel = pushVel
                };

                result.Hits.Add(hitRecord);
            }
        }
        else
        {
            TTarget? target = IntersectClosest(from, rayEnds[0], candidates, out float hitDist);
            if (target != null)
            {
                rayEnds[0] = from + (rayEnds[0] - from) * Math.Min(1.0f, hitDist);
                Vector3 hitPushDir = (to - from).Normalized();
                int weight = Math.Max(1, target.Weight);
                Vector3 pushVel = hitPushDir * (gun.HitPush * qdam / weight);

                var hitRecord = new HitscanTargetHit<TTarget>
                {
                    Target = target,
                    Damage = qdam,
                    Rays = 1,
                    HitPos = rayEnds[0],
                    PushDir = hitPushDir,
                    PushVel = pushVel
                };

                result.Hits.Add(hitRecord);
            }
        }

        for (int i = 0; i < rayCount; i++)
            result.RayEndpoints.Add(rayEnds[i]);

        return result;
    }

    /// <summary>
    /// Finds the closest candidate target intersected by line segment [<paramref name="from"/>, <paramref name="to"/>].
    /// Port of <c>src/fpsgame/weapon.cpp:714 intersectclosest</c>.
    /// </summary>
    public static TTarget? IntersectClosest<TTarget>(
        Vector3 from,
        Vector3 to,
        IReadOnlyList<TTarget> candidates,
        out float bestDist)
        where TTarget : class, IHitscanTarget
    {
        TTarget? best = null;
        bestDist = 1e16f;

        for (int i = 0; i < candidates.Count; i++)
        {
            TTarget candidate = candidates[i];
            if (!candidate.IsAlive) continue;

            if (!WeaponRay.IntersectEntity(candidate.CollisionEntity, from, to, out float dist))
                continue;

            if (dist < bestDist)
            {
                best = candidate;
                bestDist = dist;
            }
        }

        return best;
    }
}
