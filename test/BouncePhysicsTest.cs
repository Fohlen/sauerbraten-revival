using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// ENT_BOUNCE physics (src/engine/physics.cpp <c>bounce</c>, r6889). Exercised
/// against the editor's empty-map octree (<see cref="World.Octa.EmptyMap"/>), whose
/// lower half is a solid floor at z = worldsize/2. A golden differential against the
/// original would need a bounce extension to tools/refharness (its collision is a
/// flat-floor stand-in), so these assert the ported mechanics directly.
/// </summary>
public class BouncePhysicsTest : GoTestBase
{
    public BouncePhysicsTest(Node testScene) : base(testScene) { }

    /// <summary>A bouncer (ENT_BOUNCE) above the empty-map floor.</summary>
    private static PhysEnt NewBouncer(float z, float vz) => new()
    {
        O = new Vector3(256, 256, z),
        Vel = new Vector3(0, 0, vz),
        Type = Phys.EntBounce,
        PhysState = Phys.PhysFall,
        Radius = 4.1f,
        EyeHeight = 14.0f,
        AboveEye = 1.0f,
        NewPos = new Vector3(256, 256, z),
    };

    [Test]
    public void ReflectsOffFloorWithElasticity()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        const float floorZ = 512.0f; // worldsize 1024, lower half solid
        const float secs = 0.3f, v0 = -60.0f;
        var d = NewBouncer(floorZ + 20.0f, v0);

        // The substep applies gravity before the collision, so the inbound speed is
        // |v0| + g*secs. One substep of 0.3 s drops 20 u at 60 u/s then reflects.
        phys.Bounce(d, secs, 0.5f, 0.0f, 1.0f);

        float inbound = -v0 + Phys.Gravity * secs; // 120
        float expected = inbound * 0.5f;           // reflected = inbound * elasticity
        TestAssert.True(d.Vel.Z > 0, $"bounced upward (vz={d.Vel.Z})");
        TestAssert.True(Mathf.Abs(d.Vel.Z - expected) < 1.0f,
            $"reflected at elasticity 0.5 (vz={d.Vel.Z}, expect {expected})");
        TestAssert.Equal(Phys.PhysBounce, d.PhysState, "physstate -> PHYS_BOUNCE");
        // Did not tunnel through the floor.
        TestAssert.True(d.O.Z - d.EyeHeight >= floorZ - 1.0f, $"above floor (z={d.O.Z})");
    }

    [Test]
    public void ElasticityScalesReflection()
    {
        const float secs = 0.3f, v0 = -60.0f;
        float inbound = -v0 + Phys.Gravity * secs; // 120 (gravity applied before collide)
        foreach (float elasticity in new[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f })
        {
            var octa = new World.Octa();
            octa.EmptyMap(10);
            var phys = new OctaPhysics(octa);
            var d = NewBouncer(512.0f + 20.0f, v0);

            phys.Bounce(d, secs, elasticity, 0.0f, 1.0f);

            float expected = inbound * elasticity;
            TestAssert.True(Mathf.Abs(d.Vel.Z - expected) < 1.0f,
                $"elasticity {elasticity}: reflected {d.Vel.Z} ~ {expected}");
        }
    }

    [Test]
    public void ZeroElasticitySettlesOnFloor()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);
        var d = NewBouncer(512.0f + 20.0f, -60.0f);

        // Repeated substeps: with zero elasticity the ball must come to rest on the
        // floor rather than sinking or climbing.
        for (int i = 0; i < 100; i++) phys.Bounce(d, 0.05f, 0.0f, 0.0f, 1.0f);

        TestAssert.True(d.O.Z - d.EyeHeight >= 512.0f - 1.5f, $"resting on the floor (z={d.O.Z})");
        TestAssert.True(Mathf.Abs(d.Vel.Z) < 5.0f, $"settled (vz={d.Vel.Z})");
    }

    [Test]
    public void GravityAcceleratesWhenFalling()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        // High above the floor, one substep of pure falling: velocity grows by -g*secs.
        var d = NewBouncer(512.0f + 400.0f, 0.0f);
        phys.Bounce(d, 0.1f, 0.5f, 0.0f, 1.0f);

        TestAssert.True(Mathf.Abs(d.Vel.Z - (-Phys.Gravity * 0.1f)) < 0.01f,
            $"gravity applied (vz={d.Vel.Z}, expect {-Phys.Gravity * 0.1f})");
    }

    [Test]
    public void WaterDampsVelocity()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        // Fill the upper half of the world with water (the empty-map top octants are
        // all air), so the bouncer's mid-body material lookup reports liquid.
        for (int i = 4; i < 8; i++) octa.WorldRoot[i].Material = Phys.MatWater;

        float z = 512.0f + 400.0f;
        var d = NewBouncer(z, 0.0f);
        phys.Bounce(d, 0.1f, 0.5f, 8.0f, 1.0f);

        // Water gravity is g/16, then the whole velocity is damped by (1 - secs/waterfric).
        float expected = (-Phys.Gravity / 16 * 0.1f) * (1.0f - 0.1f / 8.0f);
        TestAssert.True(Mathf.Abs(d.Vel.Z - expected) < 0.01f,
            $"water damping applied (vz={d.Vel.Z}, expect {expected})");
    }
}
