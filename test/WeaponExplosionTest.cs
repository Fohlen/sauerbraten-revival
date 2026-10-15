using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class WeaponExplosionTest : GoTestBase
{
    public WeaponExplosionTest(Node testScene) : base(testScene) { }

    private class TestExplosionTarget : IExplosionTarget
    {
        public PhysEnt CollisionEntity { get; }
        public PlayerState State { get; } = new();
        public bool IsAlive => State.Health > 0;
        public int Weight => 100;
        public Vector3 LastPushVel { get; private set; }

        public TestExplosionTarget(Vector3 pos)
        {
            CollisionEntity = new PhysEnt
            {
                O = pos,
                Radius = 4.1f,
                EyeHeight = 14f,
                AboveEye = 1f
            };
            State.Health = 100;
            State.Armour = 0;
        }

        public void ApplyHitPush(Vector3 pushVel)
        {
            LastPushVel = pushVel;
            CollisionEntity.Vel += pushVel;
        }

        public int TakeDamage(int damage) => State.DoDamage(damage);
    }

    [Test]
    public void ConstantsMatchEngineMacros()
    {
        // src/fpsgame/game.h:344-346
        // #define EXP_SELFDAMDIV 2
        // #define EXP_SELFPUSH 2.5f
        // #define EXP_DISTSCALE 1.5f
        TestAssert.Equal(2, WeaponExplosion.ExpSelfDamDiv, "EXP_SELFDAMDIV must equal 2");
        TestAssert.Near(2.5f, WeaponExplosion.ExpSelfPush, 0.0001f, "EXP_SELFPUSH must equal 2.5f");
        TestAssert.Near(1.5f, WeaponExplosion.ExpDistScale, 0.0001f, "EXP_DISTSCALE must equal 1.5f");
    }

    [Test]
    public void ProjDistCalculatesMidpointAndDirection()
    {
        var ent = new PhysEnt
        {
            O = new Vector3(10f, 20f, 30f),
            EyeHeight = 14f,
            AboveEye = 1f
        };

        // Midpoint Z = 30 + (1 - 14)/2 = 30 - 6.5 = 23.5
        // Explosion at (10, 20, 20) -> dist = 3.5 along +Z
        Vector3 expPos = new(10f, 20f, 20f);
        float dist = WeaponExplosion.ProjDist(ent, expPos, out Vector3 dir);

        TestAssert.Near(3.5f, dist, 0.0001f, "ProjDist should be 3.5f");
        TestAssert.Near(0f, dir.X, 0.0001f, "dir X should be 0");
        TestAssert.Near(0f, dir.Y, 0.0001f, "dir Y should be 0");
        TestAssert.Near(1f, dir.Z, 0.0001f, "dir Z should be 1");
    }

    [Test]
    public void RadialDamageMatchesGoldenValues()
    {
        // Rocket Launcher: base damage = 120, exprad = 40
        // At dist = 3.5:
        // damage = (int)(120 * (1 - 3.5 / (1.5 * 40)))
        // 3.5 / 60 = 0.05833333... -> 1 - 0.05833333 = 0.9416666...
        // 120 * 0.9416666... = 113.0 -> 113
        int enemyDamage = WeaponExplosion.CalculateRadialDamage(3.5f, 40f, 120, isSelf: false);
        TestAssert.Equal(113, enemyDamage, "Enemy damage expected 113");

        // Self damage: 113 / 2 = 56
        int selfDamage = WeaponExplosion.CalculateRadialDamage(3.5f, 40f, 120, isSelf: true);
        TestAssert.Equal(56, selfDamage, "Self damage expected 56");

        // Grenade Launcher: base damage = 90, exprad = 45
        // At dist = 0: 90 and 45
        TestAssert.Equal(90, WeaponExplosion.CalculateRadialDamage(0f, 45f, 90, false), "GL dist 0 enemy damage expected 90");
        TestAssert.Equal(45, WeaponExplosion.CalculateRadialDamage(0f, 45f, 90, true), "GL dist 0 self damage expected 45");

        // Boundary: at dist >= exprad, damage must be 0
        TestAssert.Equal(0, WeaponExplosion.CalculateRadialDamage(40f, 40f, 120, false), "Damage at exprad boundary must be 0");
        TestAssert.Equal(0, WeaponExplosion.CalculateRadialDamage(45f, 45f, 90, false), "Damage at exprad boundary must be 0");
        TestAssert.Equal(0, WeaponExplosion.CalculateRadialDamage(50f, 40f, 120, false), "Damage outside exprad must be 0");
    }

    [Test]
    public void RocketJumpImpulseMechanics()
    {
        // Rocket jump feet position:
        // Player at (10, 20, 30), eyeheight = 14, feet = 16
        // Explosion at feet (10, 20, 16):
        // Midpoint = 23.5, dist = 23.5 - 16 = 7.5
        // 7.5 / (1.5 * 40) = 7.5 / 60 = 0.125
        // 120 * (1 - 0.125) = 120 * 0.875 = 105
        // Self damage = 105 / 2 = 52
        int rjDamage = WeaponExplosion.CalculateRadialDamage(7.5f, 40f, 120, isSelf: true);
        TestAssert.Equal(52, rjDamage, "RJ self damage expected 52");

        // Rocket Launcher hitpush = 160, weight = 100
        // Self push multiplier = 2.5f (EXP_SELFPUSH)
        // push = 2.5 * 160 * 52 / 100 = 400 * 52 / 100 = 208.0
        Vector3 pushDir = new(0f, 0f, 1f);
        Vector3 pushVel = WeaponExplosion.CalculateExplosionPush(
            pushDir,
            rjDamage,
            weight: 100,
            hitPush: 160,
            expRad: 40f,
            isSelf: true);

        TestAssert.Near(208.0f, pushVel.Z, 0.0001f, "RJ vertical push expected 208.0f");

        // For an enemy taking 105 damage at the same distance:
        // Enemy push = 1.0 * 160 * 105 / 100 = 168.0
        Vector3 enemyPushVel = WeaponExplosion.CalculateExplosionPush(
            pushDir,
            105,
            weight: 100,
            hitPush: 160,
            expRad: 40f,
            isSelf: false);

        TestAssert.Near(168.0f, enemyPushVel.Z, 0.0001f, "Enemy push expected 168.0f");
    }

    [Test]
    public void ResolveExplosionAppliesDamageAndPushToCandidates()
    {
        var rl = WeaponCatalog.Get(Gun.RocketLauncher);
        var shooter = new TestExplosionTarget(new Vector3(0f, 0f, 30f));
        var enemy = new TestExplosionTarget(new Vector3(10f, 0f, 30f));
        var outOfRange = new TestExplosionTarget(new Vector3(100f, 0f, 30f));

        var candidates = new List<TestExplosionTarget> { shooter, enemy, outOfRange };

        // Explode midway between shooter and enemy: (5, 0, 23.5)
        Vector3 expPos = new(5f, 0f, 23.5f);
        var result = WeaponExplosion.ResolveExplosion(
            expPos,
            rl,
            quadDamage: false,
            shooter: shooter,
            candidates: candidates);

        // Shooter and enemy should be hit; outOfRange rejected
        TestAssert.Equal(2, result.Hits.Count, "Expected 2 hits");

        var shooterHit = result.Hits.Find(h => h.Target == shooter);
        var enemyHit = result.Hits.Find(h => h.Target == enemy);

        TestAssert.True(shooterHit != null, "Shooter must be in explosion hits");
        TestAssert.True(enemyHit != null, "Enemy must be in explosion hits");

        TestAssert.True(shooterHit!.IsSelf, "Shooter hit must be flagged IsSelf");
        TestAssert.False(enemyHit!.IsSelf, "Enemy hit must not be flagged IsSelf");

        // Both are equidistant from expPos (dist = 5):
        // base damage = (int)(120 * (1 - 5 / 60)) = (int)(120 * (11/12)) = 110
        // enemy damage = 110
        // shooter damage = 110 / 2 = 55
        TestAssert.Equal(110, enemyHit.Damage, "Enemy damage expected 110");
        TestAssert.Equal(55, shooterHit.Damage, "Shooter damage expected 55");

        // Direction vectors point away from explosion:
        // Shooter at X=0, exp at X=5 -> PushDir.X should be -1
        // Enemy at X=10, exp at X=5 -> PushDir.X should be +1
        TestAssert.True(shooterHit.PushDir.X < -0.99f, "Shooter push dir must point towards negative X");
        TestAssert.True(enemyHit.PushDir.X > 0.99f, "Enemy push dir must point towards positive X");

        // Push magnitudes:
        // Shooter (self): 2.5 * 160 * 55 / 100 = 220
        // Enemy: 1.0 * 160 * 110 / 100 = 176
        TestAssert.Near(220f, shooterHit.PushVel.Length(), 0.0001f, "Shooter push expected 220");
        TestAssert.Near(176f, enemyHit.PushVel.Length(), 0.0001f, "Enemy push expected 176");
    }

    [Test]
    public void SafeTargetIsExcludedFromExplosion()
    {
        var rl = WeaponCatalog.Get(Gun.RocketLauncher);
        var directHitVictim = new TestExplosionTarget(new Vector3(10f, 0f, 30f));
        var nearbyEnemy = new TestExplosionTarget(new Vector3(15f, 0f, 30f));
        var candidates = new List<TestExplosionTarget> { directHitVictim, nearbyEnemy };

        // In Cube 2, projsplash passes the directly hit dynent as `safe` so they don't get double damaged
        Vector3 expPos = new(10f, 0f, 23.5f);
        var result = WeaponExplosion.ResolveExplosion(
            expPos,
            rl,
            quadDamage: false,
            shooter: null,
            candidates: candidates,
            safe: directHitVictim);

        TestAssert.Equal(1, result.Hits.Count, "Expected 1 hit (safe excluded)");
        TestAssert.True(ReferenceEquals(nearbyEnemy, result.Hits[0].Target), "Nearby enemy should be the only hit target");
    }
}
