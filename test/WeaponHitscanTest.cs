using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class WeaponHitscanTest : GoTestBase
{
    public WeaponHitscanTest(Node testScene) : base(testScene) { }

    private class TestTarget : IHitscanTarget
    {
        public PhysEnt CollisionEntity { get; }
        public PlayerState State { get; } = new();
        public bool IsAlive => State.Health > 0;
        public int Weight => 100;
        public Vector3 LastPushVel { get; private set; }

        public TestTarget(Vector3 pos)
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
    public void RifleDirectHitDealsDamageAndPushes()
    {
        var rifle = WeaponCatalog.Get(Gun.Rifle);
        var target = new TestTarget(new Vector3(100f, 0f, 10f));
        var candidates = new List<TestTarget> { target };

        Vector3 from = new(0f, 0f, 10f);
        Vector3 aim = new(200f, 0f, 10f);

        var result = WeaponHitscan.Fire(
            world: null,
            from: from,
            targetPos: aim,
            gun: rifle,
            quadDamage: false,
            candidates: candidates);

        // Shooter kickback: -dir * kickamount * 2.5f -> rifle kick is 30
        // -Vector3(1, 0, 0) * 30 * 2.5f = (-75, 0, 0)
        TestAssert.Near(-75f, result.KickbackVel.X, 0.01f, "shooter kickback X");

        // Target hit resolution
        TestAssert.Equal(1, result.Hits.Count, "one target hit");
        var hit = result.Hits[0];
        TestAssert.True(ReferenceEquals(target, hit.Target), "target is our test entity");
        TestAssert.Equal(100, hit.Damage, "rifle damage is 100");
        TestAssert.Equal(1, hit.Rays, "rifle is 1 ray");

        // HitPush: dir * hitpush * damage / weight
        // dir = (1, 0, 0), hitpush = 80, damage = 100, weight = 100 -> push = 80
        TestAssert.Near(80f, hit.PushVel.X, 0.01f, "target push vel X");

        // Ray shortened to cylinder hit position
        TestAssert.Equal(1, result.RayEndpoints.Count, "1 ray endpoint");
        // Target is at 100, radius 4.1, entry is 95.9
        TestAssert.Near(95.9f, result.RayEndpoints[0].X, 0.01f, "shortened endpoint X");
    }

    [Test]
    public void RifleBlockedByOctreeDealsNoDamage()
    {
        // World with empty cubes except root[0] solid at origin spanning (0,0,0) to (8,8,8)
        var octa = new Octa(16);
        foreach (var cube in octa.WorldRoot) cube.EmptyFaces();
        octa.WorldRoot[0].SolidFaces();

        var rifle = WeaponCatalog.Get(Gun.Rifle);
        // Target is at x = 12 (behind the cube at 0..8)
        var target = new TestTarget(new Vector3(12f, 4f, 4f));
        var candidates = new List<TestTarget> { target };

        Vector3 from = new(-5f, 4f, 4f);
        Vector3 aim = new(20f, 4f, 4f);

        var result = WeaponHitscan.Fire(
            world: octa,
            from: from,
            targetPos: aim,
            gun: rifle,
            quadDamage: false,
            candidates: candidates);

        // Target is behind the solid wall, should receive 0 hits
        TestAssert.Equal(0, result.Hits.Count, "target behind octree wall is occluded");
        TestAssert.True(result.To.X <= 8.1f, "ray stopped at octree wall");
    }

    [Test]
    public void ShotgunPelletsAggregateOnTarget()
    {
        var shotgun = WeaponCatalog.Get(Gun.Shotgun);
        // Place target close so multiple spread pellets hit
        var target = new TestTarget(new Vector3(20f, 0f, 10f));
        var candidates = new List<TestTarget> { target };

        Vector3 from = new(0f, 0f, 10f);
        Vector3 aim = new(100f, 0f, 10f);

        var rng = new WeaponRay.Mt19937(42);
        var result = WeaponHitscan.Fire(
            world: null,
            from: from,
            targetPos: aim,
            gun: shotgun,
            quadDamage: false,
            candidates: candidates,
            rng: rng);

        TestAssert.Equal(20, result.RayEndpoints.Count, "shotgun fires 20 pellets");
        TestAssert.Equal(1, result.Hits.Count, "pellets aggregated into single target hit");

        var hit = result.Hits[0];
        TestAssert.True(hit.Rays > 0, "at least 1 pellet hit");
        TestAssert.Equal(hit.Rays * shotgun.Damage, hit.Damage, "total damage matches rays * base damage");
    }

    [Test]
    public void QuadDamageMultipliesDamageByFour()
    {
        var rifle = WeaponCatalog.Get(Gun.Rifle);
        var target = new TestTarget(new Vector3(100f, 0f, 10f));
        var candidates = new List<TestTarget> { target };

        var result = WeaponHitscan.Fire(
            world: null,
            from: new Vector3(0f, 0f, 10f),
            targetPos: new Vector3(200f, 0f, 10f),
            gun: rifle,
            quadDamage: true,
            candidates: candidates);

        TestAssert.Equal(1, result.Hits.Count, "target hit");
        TestAssert.Equal(400, result.Hits[0].Damage, "quad damage is 4x base damage (400)");
    }
}
