using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

/// <summary>Verifies the live <see cref="MapPlayer"/> against the ported octree physics.</summary>
public class MapPlayerTest : GoTestBase
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
    public void FovConvertsHorizontalToVertical()
    {
        // Sauerbraten fov=100 is horizontal; Godot wants vertical.
        // 16:9: fovy = 2*atan(tan(50deg)/(16/9)) = 2*atan(1.19175/1.7778) ~= 67.67 deg.
        float v = MapPlayer.FovToVertical(100f, 16f / 9f);
        GD.Print($"[player] fov 100 (h, 16:9) -> {v:F3} (v)");
        TestAssert.True(Mathf.Abs(v - 67.67f) < 0.1f, $"16:9 vertical fov ~= 67.67 (got {v})");

        // Square viewport: horizontal == vertical.
        TestAssert.True(Mathf.Abs(MapPlayer.FovToVertical(100f, 1f) - 100f) < 1e-3f, "1:1 keeps fov");
    }

    [Test]
    public void SpectatorFloatIgnoresGravityAndFliesOnPitch()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 0f);
        Step(player, 400); // settle on the floor

        player.ToggleFloat();
        TestAssert.True(player.Floating, "floating after toggle");

        // Fly forward with a downward pitch: the floating branch moves along the view
        // direction and never applies gravity/collision.
        player.SetView(0f, -30f);
        player.Entity.Move = 1;
        var before = player.Entity.O;
        Step(player, 40);
        var after = player.Entity.O;

        GD.Print($"[player] float: {before} -> {after} ps={player.Entity.PhysState}");
        TestAssert.Equal(Phys.PhysFloat, player.Entity.PhysState, "physstate is PHYS_FLOAT");
        TestAssert.True(after.Y > before.Y + 1f, "flew forward");
        TestAssert.True(after.Z < before.Z - 1f, "pitch-down flew downward despite gravity");
        player.Free();
    }

    [Test]
    public void WaterEntersAndSlows()
    {
        // Synthetic 32^3 world: solid floor (z<8), water band (z 8..16), air above.
        // The mid-body sample for eye z=14 is z=11.25, which lands in the band.
        var octa = new Octa(5);
        PaintByZ(octa.WorldRoot, 0, octa.WorldSize);

        var phys = new OctaPhysics(octa);
        var pl = new PhysEnt { Type = Phys.EntPlayer, O = new Vector3(16, 16, 14), PhysState = Phys.PhysFall };

        // First step inside water damps velocity (inwater transitions 0 -> MAT_WATER).
        phys.MovePlayer(pl, Phys.MoveRes, Phys.PhysFrameTime);
        TestAssert.Equal((int)Phys.MatWater, pl.InWater, "water volume sets InWater = MAT_WATER");
        TestAssert.True(pl.PhysState != Phys.PhysFloat, "swimming is not the floating state");

        // Once established in water, the per-frame damping must NOT re-apply (which
        // would pin velocity near zero). Give it horizontal velocity and step.
        pl.Vel = new Vector3(100, 0, 0);
        phys.MovePlayer(pl, Phys.MoveRes, Phys.PhysFrameTime);
        TestAssert.True(pl.Vel.X > 50f, $"velocity persists in water (got {pl.Vel.X})");
        TestAssert.Equal((int)Phys.MatWater, pl.InWater, "still in water");
    }

    /// <summary>Paint the octree into an 8-unit leaf grid: solid below z=8, water 8..16, empty above.</summary>
    private static void PaintByZ(Cube[] c, int oz, int size)
    {
        if (size > 8)
        {
            c[0].Children ??= new Octa(0).NewCubes(Octa.F_EMPTY);
            for (int i = 0; i < 8; i++)
            {
                c[i].Children ??= new Octa(0).NewCubes(Octa.F_EMPTY);
                PaintByZ(c[i].Children!, oz + ((i >> 2) & 1) * (size >> 1), size >> 1);
            }
            return;
        }
        int z = oz + size / 2;
        for (int i = 0; i < 8; i++)
        {
            if (z < 8) { c[i].SetFaces(Octa.F_SOLID); c[i].Material = Octa.MAT_AIR; }
            else if (z < 16) { c[i].SetFaces(Octa.F_EMPTY); c[i].Material = Octa.MAT_WATER; }
            else { c[i].SetFaces(Octa.F_EMPTY); c[i].Material = Octa.MAT_AIR; }
        }
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

    [Test]
    public void ThirdPersonCameraSitsBehindAndAboveEye()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 0f);
        player.SetView(0f, 0f);

        var camera = new Camera3D { Name = "Camera3D" };
        player.AddChild(camera);
        var world = new Node3D { RotationDegrees = new Vector3(-90, 0, 0) }; // MapWorld Z-up -> Y-up
        world.AddChild(player);
        TestScene.AddChild(world); // a live tree lets the node transforms resolve

        // First person: camera at the eye, body hidden.
        TestAssert.Equal(Vector3.Zero, camera.Position, "first-person camera at eye");

        // Third person: the camera shares the player's view orientation (identity local
        // rotation). In the player's local Z-up frame, local -Z is forward, local +Y is
        // world up. So the camera sits at (0, up, +distance): pulled back +Z and raised
        // +Y. (engine thirdpersondistance=20 / thirdpersonup=10)
        player.ThirdPersonDistance = 20f;
        player.ThirdPersonUp = 10f;
        player.ToggleThirdPerson();
        player.SetView(0f, 0f); // re-sync the camera

        Vector3 off = camera.Position;
        GD.Print($"[player] third-person offset={off}");
        world.QueueFree();
        TestAssert.True(Mathf.Abs(off.X) < 1e-3f && Mathf.Abs(off.Y - 10f) < 1e-3f && Mathf.Abs(off.Z - 20f) < 1e-3f,
            $"camera raised (+Y local up) and behind (+Z local back) the eye (got {off})");
    }

    [Test]
    public void TryAttackAppliesRecoilAndHitsTargetPlayer()
    {
        var octa = TestData.ReadMap().ToOcta();
        // Shooter facing +Y (yaw = 0)
        var shooter = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 0f);
        shooter.SelectWeapon(Gun.Rifle);
        shooter.Combat.Ammo[(int)Gun.Rifle] = 10;

        // Target directly ahead at +Y = 946 + 40 = 986
        var target = MakePlayer(octa, new Vector3(3000, 986, 3040), yaw: 0f);
        target.Combat.Health = 100;
        target.Combat.Armour = 0;

        var targets = new List<IHitscanTarget> { target };

        bool fired = shooter.TryAttack(nowMillis: 1000, targets: targets);
        TestAssert.True(fired, "attack succeeded");

        // Ammo decremented
        TestAssert.Equal(9, shooter.Combat.Ammo[(int)Gun.Rifle], "ammo decremented to 9");

        // Recoil: rifle kick is 30 -> -dir * 30 * 2.5 = -75 along +Y -> Vel.Y == -75
        TestAssert.Near(-75f, shooter.Entity.Vel.Y, 0.01f, "shooter recoil along -Y");

        // Target hit: rifle damage 100 -> target health reduced to 0 -> dead
        TestAssert.Equal(0, target.Combat.Health, "target took 100 damage");
        TestAssert.True(target.Entity.State == PhysEnt.CsDead, "target died from lethal damage");

        // Target push: dir * (80 * 100 / 100) = 80 along +Y
        TestAssert.Near(80f, target.Entity.Vel.Y, 0.01f, "target pushed along +Y");

        shooter.Free();
        target.Free();
    }
}
