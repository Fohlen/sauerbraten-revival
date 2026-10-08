using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Wiring of the ported ENT_BOUNCE physics: the <see cref="BounceBody"/> runtime
/// node, the dynent cache, and player (<c>plcollide</c>) collision. Exercised
/// against the editor octree (solid floor at z = worldsize/2). See
/// <see cref="BouncePhysicsTest"/> for the pure bounce mechanics.
/// </summary>
public class BounceBodyTest : GoTestBase
{
    public BounceBodyTest(Node testScene) : base(testScene) { }

    private const float FloorZ = 512.0f; // worldsize 1024, lower half solid

    private static OctaPhysics NewWorld()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        return new OctaPhysics(octa);
    }

    [Test]
    public void ReflectsOffFloorAndSyncsTransform()
    {
        var phys = NewWorld();
        var body = new BounceBody { BodyKind = BounceBody.Kind.Grenade };
        body.Setup(phys, new Vector3(256, 256, FloorZ + 30.0f), new Vector3(0, 0, -60.0f));
        TestScene.AddChild(body);

        // Let gravity bring it down and reflect (0.2 s only drops ~16 u; give it room).
        for (int i = 0; i < 150; i++) body.Advance(Phys.PhysFrameTime / 1000.0);

        TestAssert.True(body.Bounces > 0, $"reflected at least once (bounces={body.Bounces})");
        TestAssert.True(body.Entity.O.Z - body.Entity.EyeHeight >= FloorZ - 2.0f,
            $"did not tunnel through the floor (z={body.Entity.O.Z})");
        TestAssert.True(body.Position.IsEqualApprox(body.Entity.O), "node transform synced");

        body.QueueFree();
    }

    [Test]
    public void RegisteredAsDynent()
    {
        var phys = NewWorld();
        var body = new BounceBody();
        body.Setup(phys, new Vector3(256, 256, FloorZ + 30.0f), Vector3.Zero);
        TestScene.AddChild(body);

        TestAssert.True(phys.DynEnts.Contains(body.Entity), "bouncer is a dynent");
        body.QueueFree();
    }

    [Test]
    public void HitsPlayerDynEnt()
    {
        var phys = NewWorld();

        // A stationary player capsule on the bouncer's path.
        var player = new PhysEnt
        {
            O = new Vector3(256, 256, FloorZ + 24.0f),
            Type = Phys.EntPlayer,
            Radius = 4.1f, XRadius = 4.1f, YRadius = 4.1f,
            EyeHeight = 14.0f, AboveEye = 1.0f,
            State = PhysEnt.CsAlive,
        };
        phys.DynEnts.Add(player);

        var body = new BounceBody { BodyKind = BounceBody.Kind.Grenade };
        // Start just above the player, falling straight down onto them.
        body.Setup(phys, new Vector3(256, 256, FloorZ + 60.0f), new Vector3(0, 0, -80.0f));
        TestScene.AddChild(body);

        for (int i = 0; i < 400 && body.LastPlayerHit == null; i++)
            body.Advance(Phys.PhysFrameTime / 1000.0);

        TestAssert.True(body.LastPlayerHit == player, "bouncer reported hitting the player dynent");
        body.QueueFree();
    }

    [Test]
    public void DynentCacheFindsOverlappingEntity()
    {
        var phys = NewWorld();
        var player = new PhysEnt { O = new Vector3(256, 256, FloorZ + 20.0f), Radius = 4.1f, State = PhysEnt.CsAlive };
        phys.DynEnts.Add(player);

        phys.ClearDynentCache();
        phys.UpdateDynentCache(player);

        TestAssert.True(phys.OverlapsDynent(new Vector3(258, 256, FloorZ + 20.0f), 8.0f), "overlap found near the player");
        TestAssert.True(!phys.OverlapsDynent(new Vector3(400, 400, FloorZ + 20.0f), 8.0f), "no overlap far away");
    }

    [Test]
    public void DebrisIsSmallerThanGrenade()
    {
        var phys = NewWorld();
        var grenade = new BounceBody { BodyKind = BounceBody.Kind.Grenade };
        grenade.Setup(phys, new Vector3(100, 100, FloorZ + 20.0f), Vector3.Zero);
        var debris = new BounceBody { BodyKind = BounceBody.Kind.Debris };
        debris.Setup(phys, new Vector3(120, 100, FloorZ + 20.0f), Vector3.Zero);
        TestScene.AddChild(grenade);
        TestScene.AddChild(debris);

        TestAssert.True(Mathf.Abs(grenade.Entity.Radius - 1.5f) < 1e-4f, "grenade radius 1.5");
        TestAssert.True(Mathf.Abs(debris.Entity.Radius - 0.5f) < 1e-4f, "debris radius 0.5");
        TestAssert.Equal(Phys.CollideEllipsePrecise, grenade.Entity.CollideType, "grenade precise ellipse");
        TestAssert.Equal(Phys.CollideEllipse, debris.Entity.CollideType, "debris default ellipse");

        grenade.QueueFree();
        debris.QueueFree();
    }
}
