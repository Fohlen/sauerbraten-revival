using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// A linear flying projectile (e.g. Rocket Launcher or monster fireballs/spells)
/// ported faithfully from Cube 2: Sauerbraten
/// (<c>src/fpsgame/weapon.cpp:projectile/newprojectile/updateprojectiles/projdamage/projsplash</c>).
/// </summary>
public sealed class RocketProjectile
{
    public const int DefaultOffsetMillis = 500; // weapon.cpp:7 OFFSETMILLIS

    public Vector3 O { get; set; }
    public Vector3 To { get; set; }
    public Vector3 Dir { get; set; }
    public Vector3 Offset { get; set; }
    public float Speed { get; set; } = 320f; // guns[GUN_RL].projspeed = 320
    public IExplosionTarget? Owner { get; set; }
    public WeaponInfo Gun { get; set; } = null!;
    public bool QuadDamage { get; set; }
    public bool Local { get; set; } = true;
    public int OffsetMillis { get; set; } = DefaultOffsetMillis;
    public int Id { get; set; }
    public bool Exploded { get; private set; }

    /// <summary>
    /// Event raised when the projectile detonates.
    /// Passes detonation position, direct hit victim (if any), and full explosion result.
    /// </summary>
    public event Action<Vector3, IExplosionTarget?, ExplosionResult<IExplosionTarget>>? Detonated;

    public RocketProjectile(
        Vector3 from,
        Vector3 to,
        WeaponInfo gun,
        IExplosionTarget? owner,
        Octa? world = null,
        bool quadDamage = false,
        float? speed = null,
        Vector3? hudOffset = null,
        bool local = true,
        int id = 0)
    {
        Gun = gun;
        Owner = owner;
        QuadDamage = quadDamage;
        Speed = speed ?? (gun.ProjSpeed > 0 ? gun.ProjSpeed : 320f);
        Local = local;
        Id = id;

        O = from;
        Dir = (to - from).Normalized();
        if (Dir.LengthSquared() < 1e-6f)
            Dir = new Vector3(0f, 1f, 0f);

        float dist = from.DistanceTo(to);
        float shorten = 0f;
        if (gun.Range > 0 && dist > gun.Range)
            shorten = gun.Range;

        if (world != null)
        {
            float barrier = OctaRaycast.RayCube(
                world,
                from,
                Dir,
                dist,
                OctaRaycast.RayClipMat | OctaRaycast.RayAlphaPoly,
                0,
                out _);

            if (barrier > 0f && barrier < dist && (shorten == 0f || barrier < shorten))
                shorten = barrier;
        }

        To = shorten > 0f ? from + Dir * shorten : to;
        Offset = hudOffset ?? Vector3.Zero;
        OffsetMillis = DefaultOffsetMillis;
    }

    /// <summary>
    /// Computes the visual interpolated position taking HUD gun offset convergence into account.
    /// Port of <c>weapon.cpp:561</c> and <c>weapon.cpp:906</c>.
    /// </summary>
    public Vector3 GetVisualPosition()
    {
        float distFactor = Math.Min(O.DistanceTo(To) / 32.0f, 1.0f);
        float offsetFactor = OffsetMillis > 0 ? (OffsetMillis / (float)DefaultOffsetMillis) : 0f;
        return O + Offset * (distFactor * offsetFactor);
    }

    /// <summary>
    /// Advances the projectile by <paramref name="timeMillis"/> ms.
    /// Checks swept cylinder intersection against candidates and world octree impact.
    /// Port of <c>src/fpsgame/weapon.cpp:523 updateprojectiles</c>.
    /// </summary>
    /// <param name="timeMillis">Elapsed step time in milliseconds (e.g. 5ms fixed step).</param>
    /// <param name="world">Octree world for collision geometry.</param>
    /// <param name="candidates">Candidate targets (e.g. players).</param>
    /// <returns>True if the projectile exploded during this step.</returns>
    public bool Advance(int timeMillis, Octa? world, IReadOnlyList<IExplosionTarget>? candidates)
    {
        if (Exploded) return true;

        OffsetMillis = Math.Max(0, OffsetMillis - timeMillis);

        int qdam = Gun.Damage * (QuadDamage ? 4 : 1);

        Vector3 dv;
        float dist = O.DistanceTo(To);
        if (dist > 0f)
        {
            dv = To - O;
            float denom = Math.Max(dist * 1000f / Speed, (float)timeMillis);
            dv *= (timeMillis / denom);
        }
        else
        {
            dv = Vector3.Zero;
        }

        Vector3 v = O + dv;
        candidates ??= Array.Empty<IExplosionTarget>();

        // 1. Check dynamic entity swept collision (weapon.cpp:537-547)
        if (Local)
        {
            Vector3 halfDv = dv * 0.5f;
            Vector3 bo = O + halfDv;
            float br = Math.Max(Math.Abs(halfDv.X), Math.Abs(halfDv.Y)) + 1f;

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (!candidate.IsAlive) continue;
                if (Owner != null && candidate == Owner) continue;

                PhysEnt ent = candidate.CollisionEntity;
                if (WeaponExplosion.Reject(ent.O, bo, ent.Radius + br))
                    continue;

                // Swept cylinder line segment test from O to v
                if (WeaponRay.IntersectEntity(ent, O, v, out float hitDist))
                {
                    Vector3 hitPos = O + (v - O) * Math.Clamp(hitDist, 0f, 1f);
                    Explode(hitPos, candidate, qdam, candidates);
                    return true;
                }
            }
        }

        // 2. Check world arrival or octree impact (weapon.cpp:550-558)
        if (dist < 4f)
        {
            if (world != null && O != To)
            {
                // raycubepos(p.o, p.dir, p.to, 0, RAY_CLIPMAT|RAY_ALPHAPOLY)
                // If the obstacle is at least 4 units away, reevaluate endpoint and continue flying
                To = OctaRaycast.RayCubePos(
                    world,
                    O,
                    Dir,
                    0f,
                    OctaRaycast.RayClipMat | OctaRaycast.RayAlphaPoly,
                    0,
                    out float barrierDist);

                if (barrierDist >= 4f)
                {
                    O = v;
                    return false;
                }
            }

            Explode(v, null, qdam, candidates);
            return true;
        }

        O = v;
        return false;
    }

    private void Explode(
        Vector3 impactPos,
        IExplosionTarget? directHitVictim,
        int qdam,
        IReadOnlyList<IExplosionTarget> candidates)
    {
        Exploded = true;
        O = impactPos;

        // Resolve radial splash damage on candidate entities, excluding directHitVictim (safe)
        var explosionResult = WeaponExplosion.ResolveExplosion(
            impactPos,
            Gun,
            QuadDamage,
            shooter: Owner,
            candidates: candidates,
            safe: directHitVictim);

        // Apply radial splash damage and push to nearby targets
        for (int i = 0; i < explosionResult.Hits.Count; i++)
        {
            var hit = explosionResult.Hits[i];
            hit.Target.ApplyHitPush(hit.PushVel);
            hit.Target.TakeDamage(hit.Damage);
        }

        // If a direct hit occurred, direct victim takes full qdam and push (weapon.cpp:518-519)
        if (directHitVictim != null)
        {
            WeaponExplosion.ProjDist(directHitVictim.CollisionEntity, impactPos, out Vector3 dir);
            int weight = Math.Max(1, directHitVictim.Weight);
            bool isSelf = (Owner != null && directHitVictim == Owner);
            Vector3 directPush = WeaponExplosion.CalculateExplosionPush(
                dir,
                qdam,
                weight,
                Gun.HitPush,
                Gun.ExpRad,
                isSelf);

            directHitVictim.ApplyHitPush(directPush);
            directHitVictim.TakeDamage(qdam);
        }

        if (Owner is Ogz.MapPlayer ownerPlayer)
        {
            ownerPlayer.OnRocketDetonated(this, impactPos, directHitVictim, explosionResult);
        }

        Detonated?.Invoke(impactPos, directHitVictim, explosionResult);
        Detonated = null;
    }
}
