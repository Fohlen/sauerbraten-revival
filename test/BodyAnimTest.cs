using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies <see cref="MapPlayer.SelectBodyAnim"/> mirrors the engine's
/// <c>renderclient</c> secondary-animation selection (rendermodel.cpp). Pure state
/// logic — no physics needed.
/// </summary>
public class BodyAnimTest : GoTestBase
{
    public BodyAnimTest(Node testScene) : base(testScene) { }

    private static MapPlayer Player()
    {
        var p = new MapPlayer();
        p.Entity.State = PhysEnt.CsAlive;
        p.Entity.PhysState = Phys.PhysFloor;
        return p;
    }

    [Test]
    public void IdleWhenStill()
    {
        var p = Player();
        TestAssert.Equal("idle", p.SelectBodyAnim(), "still → idle");
        p.Free();
    }

    [Test]
    public void WalksByDirection()
    {
        var p = Player();

        p.Entity.Move = 1; p.Entity.Strafe = 0;
        TestAssert.Equal("forward", p.SelectBodyAnim(), "move>0 → forward");

        p.Entity.Move = -1; p.Entity.Strafe = 0;
        TestAssert.Equal("backward", p.SelectBodyAnim(), "move<0 → backward");

        p.Entity.Move = 0; p.Entity.Strafe = 1;
        TestAssert.Equal("left", p.SelectBodyAnim(), "strafe-right → left");

        p.Entity.Move = 0; p.Entity.Strafe = -1;
        TestAssert.Equal("right", p.SelectBodyAnim(), "strafe-left → right");

        // move>0 wins over strafe (renderclient picks forward first).
        p.Entity.Move = 1; p.Entity.Strafe = 1;
        TestAssert.Equal("forward", p.SelectBodyAnim(), "forward+strafe → forward");
        p.Free();
    }

    [Test]
    public void JumpsWhenAirborne()
    {
        var p = Player();
        p.Entity.PhysState = Phys.PhysFall;
        p.Entity.TimeInAir = 101;
        TestAssert.Equal("jump", p.SelectBodyAnim(), "in air >100ms → jump");

        p.Entity.TimeInAir = 100; // not yet (engine: strict > 100)
        TestAssert.Equal("idle", p.SelectBodyAnim(), "air<=100ms → not jump");
        p.Free();
    }

    [Test]
    public void WaterSwimsOrSinks()
    {
        var p = Player();
        p.Entity.PhysState = Phys.PhysFall;
        p.Entity.InWater = Phys.MatWater;
        p.Entity.Move = 1;
        TestAssert.Equal("swim", p.SelectBodyAnim(), "in water moving → swim");

        p.Entity.Move = 0; p.Entity.Vel = new Vector3(0, 0, -10);
        TestAssert.Equal("sink", p.SelectBodyAnim(), "in water still+falling → sink");
        p.Free();
    }

    [Test]
    public void FloatEdits()
    {
        var p = Player();
        p.ToggleFloat();
        TestAssert.Equal("edit", p.SelectBodyAnim(), "spectator/edit float → edit");
        p.Free();
    }
}
