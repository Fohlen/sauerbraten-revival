using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class RocketProjectileTest : GoTestBase
{
    public RocketProjectileTest(Node testScene) : base(testScene) { }

    private class TestProjectileTarget : IExplosionTarget
    {
        public PhysEnt CollisionEntity { get; }
        public PlayerState State { get; } = new();
        public bool IsAlive => State.Health > 0;
        public int Weight => 100;
        public Vector3 LastPushVel { get; private set; }

        public TestProjectileTarget(Vector3 pos)
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
    public void MovesLinearlyAtSpeed320()
    {
        var rl = WeaponCatalog.Get(Gun.RocketLauncher);
        Vector3 start = new(0f, 0f, 0f);
        Vector3 end = new(320f, 0f, 0f);

        var proj = new RocketProjectile(start, end, rl, owner: null);

        TestAssert.Near(320f, proj.Speed, 0.001f, "RL projectile speed must be 320");

        // Advance 200 steps of 5 ms = 1000 ms (1 second)
        for (int i = 0; i < 200; i++)
        {
            proj.Advance(5, world: null, candidates: null);
        }

        // Must arrive at 320 units
        TestAssert.True(proj.Exploded, "Projectile should explode on reaching endpoint (dist < 4)");
        TestAssert.Near(320f, proj.O.X, 4.0f, "Projectile must travel ~320 units in 1 second");
    }

    [Test]
    public void SweptCollisionDirectHitsCandidateAndSplashesNearby()
    {
        var rl = WeaponCatalog.Get(Gun.RocketLauncher);
        var shooter = new TestProjectileTarget(new Vector3(0f, 0f, 0f));

        // Direct hit target at X = 80
        var directTarget = new TestProjectileTarget(new Vector3(80f, 0f, 0f));

        // Bystander at X = 80, Y = 10 (inside 40 blast radius)
        var splashTarget = new TestProjectileTarget(new Vector3(80f, 10f, 0f));

        var candidates = new List<IExplosionTarget> { shooter, directTarget, splashTarget };

        Vector3 from = new(0f, 0f, -6.5f); // aligned with midpoint
        Vector3 to = new(200f, 0f, -6.5f);

        var proj = new RocketProjectile(from, to, rl, owner: shooter);

        bool detonatedCallbackCalled = false;
        proj.Detonated += (impactPos, directVictim, splashResult) =>
        {
            detonatedCallbackCalled = true;
            TestAssert.True(ReferenceEquals(directVictim, directTarget), "Direct victim must be directTarget");
            TestAssert.Equal(1, splashResult.Hits.Count, "Splash result hits should only have splashTarget (directTarget excluded)");
            TestAssert.True(ReferenceEquals(splashResult.Hits[0].Target, splashTarget), "Splash target must be splashTarget");
        };

        // Advance 200 ms (40 steps of 5ms) -> travels 64 units, next steps hit at X ~ 75.9 (80 - 4.1)
        for (int i = 0; i < 60; i++)
        {
            if (proj.Advance(5, world: null, candidates))
                break;
        }

        TestAssert.True(proj.Exploded, "Projectile should have exploded on candidate impact");
        TestAssert.True(detonatedCallbackCalled, "Detonated callback should be triggered");

        // Direct target takes full direct damage (120): health was 100, so health becomes -20
        TestAssert.Equal(-20, directTarget.State.Health, "Direct target took 120 damage and died (health -20)");

        // Splash target at dist = sqrt(10^2 + 4.1^2) = 10.81:
        // damage = (int)(120 * (1 - 10.81 / 60)) = (int)(120 * 0.8198) = 98 damage
        // 100 - 98 = 2 health remaining
        TestAssert.Equal(2, splashTarget.State.Health, "Splash target took 98 splash damage (health 2)");
    }

    [Test]
    public void DetonatesAgainstOctreeGeometry()
    {
        // Scale 4 world (worldsize = 2^4 = 16) with solid block at root[0] (0..8)
        var octa = new Octa(4);
        foreach (var cube in octa.WorldRoot) cube.EmptyFaces();
        octa.WorldRoot[0].SolidFaces(); // solid at origin [0..8, 0..8, 0..8]

        var rl = WeaponCatalog.Get(Gun.RocketLauncher);
        Vector3 from = new(14f, 4f, 4f);
        Vector3 to = new(2f, 4f, 4f); // aimed through solid block at X=8

        var proj = new RocketProjectile(from, to, rl, owner: null, world: octa);

        for (int i = 0; i < 50; i++)
        {
            if (proj.Advance(5, octa, candidates: null))
                break;
        }

        TestAssert.True(proj.Exploded, "Projectile must explode against solid world octree");
        // In Sauerbraten weapon.cpp:550, dist < 4 triggers projsplash at v.
        // With step 5ms (1.6 units) and To = 8.1, O reaches 10.8 (dist = 2.7 < 4), detonating at v.X = 9.2.
        TestAssert.Near(9.2f, proj.O.X, 0.2f, "Detonation point in step where dist < 4 triggers");
    }
}
