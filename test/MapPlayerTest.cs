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
    public void ApplyLookSettingsCopiesUserSettings()
    {
        var player = new MapPlayer();
        player.ApplyLookSettings(new UserSettings
        {
            Sensitivity = 7.5f,
            SensitivityScale = 2f,
            MouseAccel = 0.5f,
            InvertMouse = true,
            Fov = 120f,
        });

        TestAssert.Near(7.5f, player.Sensitivity, 1e-4f, "sensitivity applied");
        TestAssert.Near(2f, player.SensitivityScale, 1e-4f, "sensitivity scale applied");
        TestAssert.Near(0.5f, player.MouseAccel, 1e-4f, "mouse accel applied");
        TestAssert.True(player.InvertMouse, "invert applied");
        TestAssert.Near(120f, player.Fov, 1e-4f, "fov applied");
        player.Free();
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

    [Test]
    public void TryAttackRocketLauncherSpawnsRocketAndAppliesRecoil()
    {
        var octa = TestData.ReadMap().ToOcta();
        var shooter = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 0f);
        shooter.SelectWeapon(Gun.RocketLauncher);
        shooter.Combat.Ammo[(int)Gun.RocketLauncher] = 5;

        bool fired = shooter.TryAttack(nowMillis: 1000);
        TestAssert.True(fired, "rocket attack succeeded");

        // Ammo decremented
        TestAssert.Equal(4, shooter.Combat.Ammo[(int)Gun.RocketLauncher], "rocket ammo decremented to 4");

        // RL kick is 10 -> -dir * 10 * 2.5 = -25 along +Y
        TestAssert.Near(-25f, shooter.Entity.Vel.Y, 0.01f, "shooter recoil kickback along -Y");

        // Active rocket spawned
        TestAssert.Equal(1, shooter.ActiveRockets.Count, "one active rocket in flight");

        shooter.Free();
    }

    [Test]
    public void RocketJumpAppliesUpwardImpulseAndReducedSelfDamage()
    {
        var octa = TestData.ReadMap().ToOcta();
        var shooter = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 0f);
        shooter.SelectWeapon(Gun.RocketLauncher);
        shooter.Combat.Ammo[(int)Gun.RocketLauncher] = 5;
        shooter.Combat.Health = 100;
        shooter.Combat.Armour = 0;

        // Settle on floor
        Step(shooter, 200);

        // Aim straight down (-90 degrees pitch)
        shooter.SetView(0f, -90f);
        shooter.Entity.Vel = Vector3.Zero;

        bool fired = shooter.TryAttack(nowMillis: 1000);
        TestAssert.True(fired, "fired rocket straight down");
        TestAssert.Equal(1, shooter.ActiveRockets.Count, "rocket spawned");

        // Advance simulation for 200ms to allow rocket to hit ground right below player
        Step(shooter, 40);

        // Rocket should have detonated and been pruned
        TestAssert.Equal(0, shooter.ActiveRockets.Count, "rocket detonated and pruned");

        // Player took halved self damage (health reduced from 100)
        TestAssert.True(shooter.Combat.Health > 0 && shooter.Combat.Health < 100,
            $"took halved self damage: health={shooter.Combat.Health}");

        // Player received upward Z velocity impulse from explosion
        GD.Print($"[rocket jump] player Vel={shooter.Entity.Vel} health={shooter.Combat.Health}");
        TestAssert.True(shooter.Entity.Vel.Z > 10f, $"rocket jump launched player upward: Vel.Z={shooter.Entity.Vel.Z}");

        shooter.Free();
    }

    [Test]
    public void TryAttackGrenadeLauncherSpawnsGrenadeWithArc()
    {
        var octa = TestData.ReadMap().ToOcta();
        var shooter = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 0f);
        TestScene.AddChild(shooter); // grenades are parented to the shooter; without a parent they leak
        shooter.SelectWeapon(Gun.GrenadeLauncher);
        shooter.Combat.Ammo[(int)Gun.GrenadeLauncher] = 5;

        bool fired = shooter.TryAttack(nowMillis: 1000);
        TestAssert.True(fired, "grenade attack succeeded");

        TestAssert.Equal(4, shooter.Combat.Ammo[(int)Gun.GrenadeLauncher], "grenade ammo decremented to 4");
        TestAssert.Equal(1, shooter.ActiveGrenades.Count, "one active grenade spawned");

        var grenade = shooter.ActiveGrenades[0];
        // In weapon.cpp:641-643: up = to; up.z += dist/8 -> launch velocity has upward Z arc
        TestAssert.True(grenade.Entity.Vel.Z > 0f, $"grenade launched with upward arc: Vel.Z={grenade.Entity.Vel.Z}");

        shooter.Free();
    }

    [Test]
    public void GrenadeJumpAppliesUpwardImpulseOnBounceContact()
    {
        var octa = TestData.ReadMap().ToOcta();
        var shooter = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 0f);
        TestScene.AddChild(shooter); // grenades are parented to the shooter; without a parent they leak
        shooter.SelectWeapon(Gun.GrenadeLauncher);
        shooter.Combat.Ammo[(int)Gun.GrenadeLauncher] = 5;
        shooter.Combat.Health = 100;
        shooter.Combat.Armour = 0;

        // Settle on floor
        Step(shooter, 200);

        // Jump and aim steeply down (-80 degrees)
        shooter.Entity.Vel = new Vector3(0, 0, 60f); // jumping upward
        shooter.SetView(0f, -80f);

        bool fired = shooter.TryAttack(nowMillis: 1000);
        TestAssert.True(fired, "grenade attack succeeded");
        TestAssert.Equal(1, shooter.ActiveGrenades.Count, "active grenade spawned");

        for (int s = 0; s < 40; s++)
        {
            shooter.Advance(Phys.PhysFrameTime / 1000.0);
            if (shooter.ActiveGrenades.Count == 0) break;
        }

        TestAssert.Equal(0, shooter.ActiveGrenades.Count, "grenade detonated on contact with player");
        TestAssert.True(shooter.Combat.Health > 0 && shooter.Combat.Health < 100,
            $"player took halved self-damage: health={shooter.Combat.Health}");
        GD.Print($"[grenade jump] player Vel={shooter.Entity.Vel} health={shooter.Combat.Health}");
        TestAssert.True(shooter.Entity.Vel.Z > 30f,
            $"grenade jump launched player upward: Vel.Z={shooter.Entity.Vel.Z}");

        shooter.Free();
    }

    [Test]
    public void GrenadeDetonatesOnPlayerContactWhenSteppingOnIt()
    {
        var octa = TestData.ReadMap().ToOcta();
        var shooter = MakePlayer(octa, new Vector3(3000, 946, 3040), yaw: 0f);
        TestScene.AddChild(shooter); // grenades are parented to the shooter; without a parent they leak
        shooter.SelectWeapon(Gun.GrenadeLauncher);
        shooter.Combat.Ammo[(int)Gun.GrenadeLauncher] = 5;
        shooter.Combat.Health = 100;
        shooter.Combat.Armour = 0;

        // Settle on floor
        Step(shooter, 200);

        // Aim forward-down (-60 degrees pitch) so grenade lands right in front of player
        shooter.SetView(0f, -60f);
        bool fired = shooter.TryAttack(nowMillis: 1000);
        TestAssert.True(fired, "grenade attack succeeded");
        TestAssert.Equal(1, shooter.ActiveGrenades.Count, "active grenade spawned");

        var grenade = shooter.ActiveGrenades[0];

        // Walk forward and jump towards the grenade
        shooter.Entity.Move = 1;
        shooter.Entity.Jumping = true;
        bool detonatedOnPlayer = false;

        for (int s = 0; s < 250; s++)
        {
            shooter.Advance(Phys.PhysFrameTime / 1000.0);
            if (shooter.ActiveGrenades.Count == 0)
            {
                detonatedOnPlayer = true;
                break;
            }
        }

        TestAssert.True(detonatedOnPlayer, "grenade detonated when player jumped/walked into it before fuse timeout");
        TestAssert.True(shooter.Combat.Health < 100, "player took damage from grenade explosion");
        TestAssert.True(shooter.Entity.Vel.Length() > 50f, $"player received explosion impulse: Vel={shooter.Entity.Vel}");

        shooter.Free();
    }

    [Test]
    public void FiringConsumesAmmoAndEmptyGunCannotFire()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040));
        player.SelectWeapon(Gun.Pistol);
        player.Combat.Ammo[(int)Gun.Pistol] = 2;

        bool fired = player.TryAttack(nowMillis: 1000);
        TestAssert.True(fired, "pistol fired with ammo");
        TestAssert.Equal(1, player.Combat.Ammo[(int)Gun.Pistol], "pistol ammo decremented to 1");

        fired = player.TryAttack(nowMillis: 1500);
        TestAssert.True(fired, "pistol fired second shot");
        TestAssert.Equal(0, player.Combat.Ammo[(int)Gun.Pistol], "pistol ammo decremented to 0");

        // Firing with 0 ammo fails, triggers gunwait = 600, and does not fire
        fired = player.TryAttack(nowMillis: 2000);
        TestAssert.False(fired, "pistol cannot fire with 0 ammo");
        TestAssert.Equal(600, player.Combat.GunWait, "gunwait set to 600 ms on dry fire");

        player.Free();
    }

    [Test]
    public void LethalDamageKillsPlayerAndHaltsMovement()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040));
        player.Combat.Health = 100;
        player.Combat.Armour = 0;
        player.Entity.Vel = new Vector3(50, 50, 0);

        player.TakeDamage(100, nowMillis: 1000);

        TestAssert.Equal(0, player.Combat.Health, "health reduced to 0");
        TestAssert.False(player.IsAlive, "player is dead");
        TestAssert.Equal(PhysEnt.CsDead, player.Entity.State, "state is CsDead");
        TestAssert.Equal(0, player.Entity.Move, "move input halted on death");
        TestAssert.Equal(0, player.Entity.Strafe, "strafe input halted on death");
        TestAssert.False(player.Entity.Jumping, "jumping halted on death");
        TestAssert.Equal(new Vector3(50, 50, 0), player.Entity.Vel, "velocity/knockback preserved on death");
        TestAssert.Equal("dying", player.SelectBodyAnim(nowMillis: 1200), "anim is dying within 1000ms");
        TestAssert.Equal("dead", player.SelectBodyAnim(nowMillis: 2500), "anim is dead after 1000ms");

        // Cannot attack while dead
        bool fired = player.TryAttack(nowMillis: 1100);
        TestAssert.False(fired, "cannot attack while dead");

        player.Free();
    }

    [Test]
    public void ClickToRespawnAfterDelayRestoresPlayer()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040));
        player.SpawnFeet = new Vector3(3000, 946, 3040);
        player.SpawnYaw = 90f;
        player.SpawnWaitMillis = 500;

        // Player dies at t = 1000
        player.Combat.Armour = 0;
        player.TakeDamage(100, nowMillis: 1000);
        TestAssert.Equal(PhysEnt.CsDead, player.Entity.State, "player died");

        // Move away from spawn position while dead (ragdoll/fall)
        player.Entity.O = new Vector3(500, 500, 500);

        // Click to respawn before SpawnWaitMillis (t = 1200, only 200 ms passed)
        bool respawned = player.TryRespawn(nowMillis: 1200);
        TestAssert.False(respawned, "cannot respawn before spawn wait delay");
        TestAssert.Equal(PhysEnt.CsDead, player.Entity.State, "still dead");

        // Click to respawn after SpawnWaitMillis (t = 1600, 600 ms passed > 500 ms)
        respawned = player.TryRespawn(nowMillis: 1600);
        TestAssert.True(respawned, "respawn succeeded after spawn wait delay");
        TestAssert.Equal(PhysEnt.CsAlive, player.Entity.State, "state restored to CsAlive");
        TestAssert.True(player.IsAlive, "player is alive");
        TestAssert.Equal(100, player.Combat.Health, "health restored to 100");
        TestAssert.Equal(25, player.Combat.Armour, "FFA blue armour restored to 25");
        TestAssert.Equal(40, player.Combat.Ammo[(int)Gun.Pistol], "FFA pistol ammo restored to 40");
        TestAssert.Equal(player.SpawnFeet with { Z = player.SpawnFeet.Z + MapPlayer.EyeHeight }, player.Entity.O,
            "position restored to spawn point");
        TestAssert.Equal(90f, player.Entity.Yaw, "yaw restored to spawn yaw");

        player.Free();
    }
}
