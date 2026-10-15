using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// A candidate target for explosion radial damage and push.
/// Compatible with <see cref="IHitscanTarget"/>.
/// </summary>
public interface IExplosionTarget
{
    PhysEnt CollisionEntity { get; }
    bool IsAlive { get; }
    int Weight { get; }
    void ApplyHitPush(Vector3 pushVel);
    int TakeDamage(int damage);
}

/// <summary>
/// A hit detected during radial explosion resolution (port of engine <c>radialeffect</c>).
/// </summary>
public sealed class ExplosionTargetHit<TTarget> where TTarget : class
{
    public TTarget Target { get; set; } = null!;
    public int Damage { get; set; }
    public float Distance { get; set; }
    public Vector3 PushDir { get; set; }
    public Vector3 PushVel { get; set; }
    public bool IsSelf { get; set; }
}

/// <summary>
/// Result of an explosion resolution containing all affected targets and impulses.
/// </summary>
public sealed class ExplosionResult<TTarget> where TTarget : class
{
    public Vector3 Origin { get; set; }
    public List<ExplosionTargetHit<TTarget>> Hits { get; } = new();
}

/// <summary>
/// Radial explosion damage and impulse calculations ported faithfully from Cube 2: Sauerbraten
/// (<c>src/fpsgame/game.h</c>, <c>src/fpsgame/weapon.cpp:projdist/radialeffect/explode</c>).
/// </summary>
public static class WeaponExplosion
{
    /// <summary>Self damage divider from <c>game.h:344 EXP_SELFDAMDIV</c>.</summary>
    public const int ExpSelfDamDiv = 2;

    /// <summary>Self push multiplier from <c>game.h:345 EXP_SELFPUSH</c>.</summary>
    public const float ExpSelfPush = 2.5f;

    /// <summary>Distance scale denominator from <c>game.h:346 EXP_DISTSCALE</c>.</summary>
    public const float ExpDistScale = 1.5f;

    /// <summary>
    /// Computes the projectile distance and direction vector from explosion center to an entity cylinder.
    /// Port of <c>src/fpsgame/weapon.cpp:403 projdist(dynent *o, vec &amp;dir, const vec &amp;v)</c>.
    /// </summary>
    /// <param name="ent">Target entity.</param>
    /// <param name="explosionPos">World explosion center.</param>
    /// <param name="dir">Normalized direction vector from explosion to entity midpoint.</param>
    /// <returns>Distance between explosion center and entity midpoint.</returns>
    public static float ProjDist(PhysEnt ent, Vector3 explosionPos, out Vector3 dir)
    {
        Vector3 middle = ent.O;
        middle.Z += (ent.AboveEye - ent.EyeHeight) * 0.5f;
        dir = middle - explosionPos;
        float dist = dir.Length();
        if (dist > 0f)
        {
            dir /= dist;
        }
        else
        {
            dir = Vector3.Zero;
        }

        if (dist < 0f) dist = 0f;
        return dist;
    }

    /// <summary>
    /// Computes radial explosion damage given distance, blast radius, base damage, and self flag.
    /// Port of <c>src/fpsgame/weapon.cpp:420-421</c>:
    /// <code>
    /// int damage = (int)(qdam*(1-dist/EXP_DISTSCALE/guns[gun].exprad));
    /// if(o==at) damage /= EXP_SELFDAMDIV;
    /// </code>
    /// </summary>
    public static int CalculateRadialDamage(float dist, float expRad, int baseDamage, bool isSelf)
    {
        if (expRad <= 0f || dist >= expRad) return 0;

        int damage = (int)(baseDamage * (1.0f - dist / (ExpDistScale * expRad)));
        if (damage < 0) damage = 0;

        if (isSelf)
        {
            damage /= ExpSelfDamDiv;
        }

        return damage;
    }

    /// <summary>
    /// Computes velocity push impulse from explosion damage and direction.
    /// Port of <c>src/fpsgame/game.h:576 fpsent::hitpush</c>:
    /// <code>
    /// vec push(dir);
    /// push.mul((actor==this &amp;&amp; guns[gun].exprad ? EXP_SELFPUSH : 1.0f)*guns[gun].hitpush*damage/weight);
    /// vel.add(push);
    /// </code>
    /// </summary>
    public static Vector3 CalculateExplosionPush(
        Vector3 dir,
        int damage,
        int weight,
        int hitPush,
        float expRad,
        bool isSelf)
    {
        if (damage <= 0 || weight <= 0) return Vector3.Zero;

        float pushMultiplier = (isSelf && expRad > 0f) ? ExpSelfPush : 1.0f;
        float magnitude = pushMultiplier * hitPush * damage / weight;
        return dir * magnitude;
    }

    /// <summary>
    /// Bounding box reject test ported from <c>src/shared/geom.h:139 vec::reject</c>:
    /// <code>
    /// bool reject(const vec &amp;o, float r) { return x>o.x+r || x&lt;o.x-r || y>o.y+r || y&lt;o.y-r; }
    /// </code>
    /// </summary>
    public static bool Reject(Vector3 a, Vector3 b, float radius)
    {
        return a.X > b.X + radius || a.X < b.X - radius ||
               a.Y > b.Y + radius || a.Y < b.Y - radius;
    }

    /// <summary>
    /// Resolves radial explosion effect against candidate targets.
    /// Port of <c>src/fpsgame/weapon.cpp:413-456 radialeffect / explode</c>.
    /// </summary>
    public static ExplosionResult<TTarget> ResolveExplosion<TTarget>(
        Vector3 explosionPos,
        WeaponInfo gun,
        bool quadDamage,
        TTarget? shooter,
        IReadOnlyList<TTarget> candidates,
        TTarget? safe = null)
        where TTarget : class, IExplosionTarget
    {
        var result = new ExplosionResult<TTarget>
        {
            Origin = explosionPos
        };

        if (gun.ExpRad <= 0) return result;

        int qdam = gun.Damage * (quadDamage ? 4 : 1);

        for (int i = 0; i < candidates.Count; i++)
        {
            TTarget candidate = candidates[i];
            if (!candidate.IsAlive) continue;
            if (candidate == safe) continue;

            PhysEnt ent = candidate.CollisionEntity;

            // geom.h:139 reject test against horizontal radius + exprad
            if (Reject(ent.O, explosionPos, ent.Radius + gun.ExpRad))
                continue;

            float dist = ProjDist(ent, explosionPos, out Vector3 dir);
            if (dist >= gun.ExpRad)
                continue;

            bool isSelf = (shooter != null && candidate == shooter);
            int damage = CalculateRadialDamage(dist, gun.ExpRad, qdam, isSelf);
            int weight = Math.Max(1, candidate.Weight);
            Vector3 pushVel = CalculateExplosionPush(dir, damage, weight, gun.HitPush, gun.ExpRad, isSelf);

            result.Hits.Add(new ExplosionTargetHit<TTarget>
            {
                Target = candidate,
                Damage = damage,
                Distance = dist,
                PushDir = dir,
                PushVel = pushVel,
                IsSelf = isSelf
            });
        }

        return result;
    }
}
