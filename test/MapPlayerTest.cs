using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>Verifies the live <see cref="MapPlayer"/> against the ported octree physics.</summary>
public class MapPlayerTest : TestClass
{
    public MapPlayerTest(Node testScene) : base(testScene) { }

    private static MapPlayer MakePlayer(Octa octa, Vector3 feet, float yaw = 0f)
    {
        var player = new MapPlayer();
        player.Setup(octa, feet, yaw);
        return player;
    }

    private static void Step(MapPlayer p, int steps)
    {
        for (int i = 0; i < steps; i++) p.Advance(Phys.PhysFrameTime / 1000.0);
    }

    [Test]
    public void FallsAndLandsOnFloor()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040));

        Step(player, 400); // 2 s

        GD.Print($"[player] settled at o={player.Entity.O} ps={player.Entity.PhysState} floor={player.Entity.Floor}");
        TestAssert.True(player.Entity.PhysState == Phys.PhysFloor || player.Entity.PhysState == Phys.PhysSlope,
            $"landed (physstate={player.Entity.PhysState})");
        TestAssert.True(player.Entity.O.Z > 0, "did not fall out of the world");
        player.Free();
    }

    [Test]
    public void ForwardMovesAlongLookDirection()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 0f);
        Step(player, 400); // settle

        var before = player.Entity.O;
        player.Entity.Move = 1; // the value the "move_forward" action must produce
        Step(player, 40);
        var after = player.Entity.O;

        GD.Print($"[player] forward: {before} -> {after}");
        TestAssert.True(after.Y > before.Y + 1f, "moved toward the look direction (+Y at yaw 0)");
        TestAssert.True(Mathf.Abs(after.X - before.X) < 1f, "no sideways drift");
        player.Free();
    }

    [Test]
    public void StrafeRightMovesScreenRight()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 0f);
        Step(player, 400); // settle

        var cam = new Camera3D();
        player.AddChild(cam);
        var world = new Node3D { RotationDegrees = new Vector3(-90, 0, 0) }; // Z-up -> Y-up
        world.AddChild(player);
        TestScene.AddChild(world);

        Vector3 screenRight = cam.GlobalTransform.Basis.X; // +X at yaw 0

        var before = player.Entity.O;
        player.Entity.Strafe = 1; // the value the "strafe_right" action must produce
        Step(player, 40);
        Vector3 delta = (player.Entity.O - before).Normalized();

        GD.Print($"[player] strafe-right delta={delta} screenRight={screenRight}");
        TestAssert.True(delta.Dot(screenRight) > 0.9f, "strafe-right moved along screen-right");

        world.QueueFree();
    }

    [Test]
    public void CameraForwardMatchesVecFromYawPitch()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 30f);
        player.SetView(30f, 0f);

        var camera = new Camera3D();
        player.AddChild(camera);
        var world = new Node3D { RotationDegrees = new Vector3(-90, 0, 0) }; // MapWorld Z-up -> Y-up
        world.AddChild(player);
        TestScene.AddChild(world); // GlobalTransform needs a live tree

        Vector3 sauer = Movement.VecFromYawPitch(30f, 0f, 1, 0);
        Vector3 expectedGodot = new(sauer.X, sauer.Z, -sauer.Y);
        Vector3 actual = -camera.GlobalTransform.Basis.Z;

        GD.Print($"[player] cam forward expected={expectedGodot} actual={actual}");
        world.QueueFree();
        TestAssert.True((actual - expectedGodot).Length() < 1e-3f, "camera forward matches vecfromyawpitch");
    }
}
