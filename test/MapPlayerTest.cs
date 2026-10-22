using System.Collections.Generic;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Entities;
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

    [Test]
    public void JumpPadAppliesPushVelocityAndSetsFallingPhysState()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040));
        Step(player, 200); // settle on floor

        var pad = new JumpPad
        {
            Position = new Vector3(3000, 946, 3040),
            Push = new Vector3(50f, 100f, 150f),
            Sound = 0,
        };

        bool triggered = player.ApplyJumpPad(pad, lastMillis: 1000);
        TestAssert.True(triggered, "jump pad triggered");
        TestAssert.Equal(pad.Push, player.Entity.Vel, "velocity set to push");
        TestAssert.Equal(Vector3.Zero, player.Entity.Falling, "falling reset to zero");
        TestAssert.Equal(Phys.PhysFall, player.Entity.PhysState, "physstate set to PhysFall");
        TestAssert.Equal(1, player.Entity.TimeInAir, "timeinair set to 1");
        TestAssert.Equal(EntityType.JumpPad, player.LastPickup, "lastpickup set to JumpPad");
        TestAssert.Equal(1000, player.LastPickupMillis, "lastpickupmillis set to 1000");

        // Within 300 ms, triggering the jump pad again is rate limited
        bool retriggered = player.ApplyJumpPad(pad, lastMillis: 1200);
        TestAssert.False(retriggered, "jump pad rate limited within 300 ms");

        // After 300 ms, triggering succeeds
        retriggered = player.ApplyJumpPad(pad, lastMillis: 1350);
        TestAssert.True(retriggered, "jump pad triggers after 300 ms cooldown");
        TestAssert.Equal(1350, player.LastPickupMillis, "lastpickupmillis updated");

        player.Free();
        pad.Free();
    }

    [Test]
    public void CheckJumpPadsDetectsWithinRadius()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040));
        Step(player, 200);

        Vector3 feet = player.Entity.FeetPos();

        // One pad too far (> 12 units), one pad in range (< 12 units)
        var farPad = new JumpPad
        {
            Position = feet + new Vector3(15f, 0, 0),
            Push = new Vector3(0, 0, 100f),
            Sound = 0,
        };
        var closePad = new JumpPad
        {
            Position = feet + new Vector3(5f, 0, 0),
            Push = new Vector3(0, 0, 200f),
            Sound = 0,
        };

        player.JumpPads = new[] { farPad, closePad };

        var hit = player.CheckJumpPads(lastMillis: 1000);
        TestAssert.Equal(closePad, hit, "triggered pad within 12 unit radius");
        TestAssert.Equal(new Vector3(0, 0, 200f), player.Entity.Vel, "velocity set by close pad");

        player.Free();
        farPad.Free();
        closePad.Free();
    }

    [Test]
    public void JumpPadDoesNotTriggerWhenDeadOrFloating()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040));
        Step(player, 200);

        Vector3 feet = player.Entity.FeetPos();
        var pad = new JumpPad
        {
            Position = feet,
            Push = new Vector3(0, 0, 150f),
            Sound = 0,
        };
        player.JumpPads = new[] { pad };

        // Dead player should not trigger
        player.Combat.Armour = 0;
        player.TakeDamage(100, nowMillis: 500);
        TestAssert.Equal(PhysEnt.CsDead, player.Entity.State, "player dead");
        var hit = player.CheckJumpPads(lastMillis: 600);
        TestAssert.True(hit == null, "dead player does not trigger jump pad");

        // Respawn and float
        player.Respawn();
        player.ToggleFloat();
        TestAssert.True(player.Floating, "player floating");
        hit = player.CheckJumpPads(lastMillis: 1000);
        TestAssert.True(hit == null, "floating player does not trigger jump pad");

        player.Free();
        pad.Free();
    }

    [Test]
    public void CheckTeleportsMovesPlayerToDestination()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040));
        Step(player, 200);

        Vector3 feet = player.Entity.FeetPos();
        var dest = new TeleDest
        {
            Position = new Vector3(1000, 1000, 500),
            Yaw = 90f,
            Pushed = false,
        };
        var tp = new Teleport
        {
            Position = feet,
            BasePosition = feet,
            Destination = dest,
            Sound = 0,
        };
        player.Teleports = new[] { tp };
        player.Entity.Vel = new Vector3(50, 50, 0);

        var hit = player.CheckTeleports(lastMillis: 1000);
        TestAssert.True(hit == tp, "teleport triggered");
        TestAssert.True(Mathf.Abs(player.Entity.O.X - 1000f) < 0.01f, "pos X matches dest");
        TestAssert.True(Mathf.Abs(player.Entity.O.Y - 1000f) < 0.01f, "pos Y matches dest");
        TestAssert.True(Mathf.Abs(player.Entity.O.Z - (500f + MapPlayer.EyeHeight)) < 0.01f, "pos Z has eye height added");
        TestAssert.True(Mathf.Abs(player.Entity.Yaw - 90f) < 0.01f, "yaw updated to dest yaw");
        TestAssert.Equal(Vector3.Zero, player.Entity.Vel, "vel reset to zero when not pushed");

        player.Free();
        dest.Free();
        tp.Free();
    }

    [Test]
    public void TeleportPreservesVelocityWhenPushed()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = MakePlayer(octa, new Vector3(3000, 946, 3040));
        Step(player, 200);

        Vector3 feet = player.Entity.FeetPos();
        var dest = new TeleDest
        {
            Position = new Vector3(1000, 1000, 500),
            Yaw = 0f, // facing +Y
            Pushed = true,
        };
        var tp = new Teleport
        {
            Position = feet,
            BasePosition = feet,
            Destination = dest,
            Sound = 0,
        };
        player.Teleports = new[] { tp };
        player.Entity.Vel = new Vector3(30, 40, 15); // horizontal speed = 50

        var hit = player.CheckTeleports(lastMillis: 1000);
        TestAssert.True(hit == tp, "teleport triggered");
        // Yaw 0 has dir = (0, 1, 0). Speed 50 -> vel should be (0, 50, 15)
        TestAssert.True(Mathf.Abs(player.Entity.Vel.X) < 0.01f, "vel X redirected");
        TestAssert.True(Mathf.Abs(player.Entity.Vel.Y - 50f) < 0.01f, "vel Y redirected with horizontal speed");
        TestAssert.True(Mathf.Abs(player.Entity.Vel.Z - 15f) < 0.01f, "vel Z preserved");

        // Cooldown check (500ms)
        var hitCooldown = player.CheckTeleports(lastMillis: 1200);
        TestAssert.True(hitCooldown == null, "rate limited by 500ms cooldown");

        player.Free();
        dest.Free();
        tp.Free();
    }

    [Test]
    public void WorldPhysicsProcessStepsRegisteredPlayersAndClock()
    {
        var world = new MapWorld { OgzPath = "base/church51.ogz", BakeRadiance = false };
        world.Build();
        TestAssert.True(world.Physics != null, "world physics initialized");

        int initialMillis = world.LastMillis;
        var player = new MapPlayer();
        world.RegisterPlayer(player);
        player.Setup(world.Physics!, new Vector3(3000, 946, 3040), 0f);

        TestAssert.True(world.Players.Contains(player), "registered player is in world Players");
        TestAssert.Equal(initialMillis, player.LastMillis, "player LastMillis initialized to world clock");

        // Advance world physics by 10 ms (two 5 ms substeps)
        world._PhysicsProcess(0.010);

        TestAssert.Equal(initialMillis + 10, world.LastMillis, "world clock advanced by 10 ms");
        TestAssert.Equal(world.LastMillis, player.LastMillis, "player clock matches world clock");

        world.UnregisterPlayer(player);
        player.Free();
        world.Free();
    }

    [Test]
    public void RegisteredPlayersDoNotAdvanceWorldClock()
    {
        var world = new MapWorld { OgzPath = "base/church51.ogz", BakeRadiance = false };
        world.Build();

        var a = new MapPlayer();
        var b = new MapPlayer();
        world.RegisterPlayer(a);
        world.RegisterPlayer(b);
        a.Setup(world.Physics!, new Vector3(3000, 946, 3040), 0f);
        b.Setup(world.Physics!, new Vector3(3000, 946, 3050), 0f);

        int before = world.LastMillis;
        world._PhysicsProcess(0.010);

        // Two players, but the clock must advance exactly once per 5 ms substep.
        TestAssert.Equal(before + 10, world.LastMillis, "world clock advanced once per substep, not once per player");
        TestAssert.Equal(world.LastMillis, a.LastMillis, "player A reads the shared clock");
        TestAssert.Equal(world.LastMillis, b.LastMillis, "player B reads the shared clock");

        // Detaching preserves the last value so a standalone player does not jump.
        world.UnregisterPlayer(a);
        TestAssert.Equal(world.LastMillis, a.LastMillis, "detached player keeps the final shared value");

        world.UnregisterPlayer(b);
        a.Free();
        b.Free();
        world.Free();
    }

    [Test]
    public void WorldBuildKeepsPreexistingSessionClock()
    {
        var sessionClock = new SimClock(12345);
        var world = new MapWorld { OgzPath = "base/church51.ogz", Clock = sessionClock, BakeRadiance = false };
        world.Build();

        TestAssert.Equal(12345, world.LastMillis, "session clock value is preserved across world build");
        TestAssert.True(ReferenceEquals(sessionClock, world.Clock), "world uses the assigned session clock resource");

        world.Free();
    }

    [Test]
    public void SimClockAdvanceAndSet()
    {
        var clock = new SimClock(100);
        TestAssert.Equal(100, clock.NowMillis, "seeded clock");
        clock.Advance(5);
        TestAssert.Equal(105, clock.NowMillis, "advance adds");
        clock.Set(0);
        TestAssert.Equal(0, clock.NowMillis, "set overrides");
    }

    [Test]
    public void WorldPhysicsProcessStepsFlagsInSync()
    {
        var world = new MapWorld { OgzPath = "base/church51.ogz", BakeRadiance = false };
        world.Build();

        var fm = world.Flags;
        TestAssert.True(fm != null, "flags manager present");

        fm!.AddFlag(0, new Vector3(100, 100, 100), 1);
        // Drop a flag
        fm.DropFlag(0, new Vector3(100, 100, 100), world.LastMillis);
        TestAssert.Equal(world.LastMillis, fm.Flags[0].DropTime, "flag drop time recorded");

        // Advance world by 100 ms
        int before = world.LastMillis;
        for (int i = 0; i < 10; i++)
        {
            world._PhysicsProcess(0.010);
        }

        TestAssert.Equal(before + 100, world.LastMillis, "clock advanced by 100 ms");
        world.Free();
    }

    [Test]
    public void WorldPhysicsProcessStepsRegisteredBouncer()
    {
        var world = new MapWorld { OgzPath = "base/church51.ogz", BakeRadiance = false };
        world.Build();

        var bouncer = new BounceBody { BodyKind = BounceBody.Kind.Debris };
        world.RegisterBouncer(bouncer);
        bouncer.Setup(world.Physics!, new Vector3(3000, 946, 3040), new Vector3(0, 0, 50));

        TestAssert.True(world.ActiveBouncers.Contains(bouncer), "registered bouncer in world ActiveBouncers");

        Vector3 startPos = bouncer.Entity.O;
        world._PhysicsProcess(0.020); // 4 substeps (20 ms)

        TestAssert.True(bouncer.Entity.O != startPos, "bouncer position updated by world physics");

        world.UnregisterBouncer(bouncer);
        bouncer.Free();
        world.Free();
    }

    [Test]
    public void WorldPhysicsProcessStepsActiveRockets()
    {
        var world = new MapWorld { OgzPath = "base/church51.ogz", BakeRadiance = false };
        world.Build();

        var rl = WeaponCatalog.Get(Gun.RocketLauncher);
        Vector3 from = new(3000, 946, 3040);
        Vector3 target = new(3000, 1946, 3040);
        var rocket = new RocketProjectile(from, target, rl, owner: null, world: world.Octa);

        world.ActiveRockets.Add(rocket);
        TestAssert.Equal(1, world.ActiveRockets.Count, "one active rocket in world");

        Vector3 startPos = rocket.O;
        world._PhysicsProcess(0.020); // 4 substeps (20 ms)

        TestAssert.True(rocket.O != startPos, "rocket advanced by world physics");
        TestAssert.True(rocket.O.Y > startPos.Y, "rocket moved toward target");

        world.Free();
    }

    [Test]
    public void DeadPredicateAndWorldModeAreSingleSourceOfTruth()
    {
        var player = new MapPlayer();
        TestAssert.True(player.IsAlive, "alive initially");
        TestAssert.False(player.Entity.Dead, "not dead initially");

        player.Entity.Dead = true;
        TestAssert.Equal(PhysEnt.CsDead, player.Entity.State, "Dead=true transitions State to CsDead");
        TestAssert.False(player.IsAlive, "IsAlive false when Dead=true");

        player.Entity.Dead = false;
        TestAssert.Equal(PhysEnt.CsAlive, player.Entity.State, "Dead=false restores CsAlive");
        TestAssert.True(player.IsAlive, "IsAlive true when Dead=false");

        var instaMode = World.Game.GameModeCatalog.Get("insta");
        var world = new MapWorld { Mode = instaMode };
        var items = new ItemManager();
        world.AddChild(items);
        world.AddChild(player);

        TestAssert.Equal(instaMode, player.Mode, "player inherits Mode from parent MapWorld");
        TestAssert.Equal(instaMode, items.Mode, "ItemManager inherits Mode from parent MapWorld");

        world.Free();
    }

    [Test]
    public void ProjectileDetonationAndWeaponEffectsDispatchWithoutClosures()
    {
        var world = new MapWorld();
        var effects = new World.Effects.WeaponEffectsRenderer { Name = "WeaponEffects" };
        world.AddChild(effects);
        world.WeaponEffects = effects;

        var player = new MapPlayer();
        world.AddChild(player);
        world.RegisterPlayer(player);

        TestAssert.Equal(effects, player.WeaponEffects, "player resolves WeaponEffects from parent MapWorld");
        TestAssert.True(ReferenceEquals(world.ActiveRockets, effects.ActiveRockets),
            "WeaponEffectsRenderer reads parent MapWorld.ActiveRockets directly");

        world.Clock.Set(2500);
        var rl = WeaponCatalog.Get(Gun.RocketLauncher);
        var rocket = new RocketProjectile(Vector3.Zero, new Vector3(0, 10, 0), rl, owner: player);
        var candidates = new IExplosionTarget[] { player };
        rocket.Advance(50, world: null, candidates: candidates);
        rocket.Advance(50, world: null, candidates: candidates);

        TestAssert.True(rocket.Exploded, "rocket exploded");
        TestAssert.Equal(2500, player.LastHit, "OnRocketDetonated updated player.LastHit");

        world.Free();
    }

    [Test]
    public void SignalsEmitOnStateTransitions()
    {
        var player = new MapPlayer();
        player.Combat.Health = 100;
        player.Combat.Armour = 0;

        int healthChangedCount = 0;
        int lastOldHealth = 0;
        int lastNewHealth = 0;
        player.HealthChanged += (oldH, newH) =>
        {
            healthChangedCount++;
            lastOldHealth = oldH;
            lastNewHealth = newH;
        };

        bool diedEmitted = false;
        player.Died += () => diedEmitted = true;

        bool respawnedEmitted = false;
        player.Respawned += () => respawnedEmitted = true;

        int weaponChangedCount = 0;
        int lastWeapon = -1;
        player.WeaponChanged += (gunId) =>
        {
            weaponChangedCount++;
            lastWeapon = gunId;
        };

        int damageDealtCount = 0;
        int lastTargetId = -1;
        int lastDamage = 0;
        player.DamageDealt += (targetId, dmg) =>
        {
            damageDealtCount++;
            lastTargetId = targetId;
            lastDamage = dmg;
        };

        // 1. HealthChanged via property setter
        player.Health = 80;
        TestAssert.Equal(1, healthChangedCount, "HealthChanged emitted on property set");
        TestAssert.Equal(100, lastOldHealth, "oldHealth is 100");
        TestAssert.Equal(80, lastNewHealth, "newHealth is 80");

        // 2. HealthChanged via TakeDamage
        player.TakeDamage(20);
        TestAssert.Equal(2, healthChangedCount, "HealthChanged emitted on TakeDamage");
        TestAssert.Equal(80, lastOldHealth, "oldHealth is 80");
        TestAssert.Equal(60, lastNewHealth, "newHealth is 60");

        // 3. WeaponChanged via SelectWeapon
        player.SelectWeapon(Gun.Shotgun);
        TestAssert.Equal(1, weaponChangedCount, "WeaponChanged emitted on SelectWeapon");
        TestAssert.Equal((int)Gun.Shotgun, lastWeapon, "gun is Shotgun");

        // 4. WeaponChanged via GunSelect property setter
        player.GunSelect = (int)Gun.Rifle;
        TestAssert.Equal(2, weaponChangedCount, "WeaponChanged emitted on GunSelect setter");
        TestAssert.Equal((int)Gun.Rifle, lastWeapon, "gun is Rifle");

        // 5. Died and HealthChanged on lethal damage
        player.TakeDamage(100);
        TestAssert.True(diedEmitted, "Died signal emitted on lethal damage");
        TestAssert.Equal(3, healthChangedCount, "HealthChanged emitted on death");
        TestAssert.Equal(0, lastNewHealth, "newHealth is 0 on death");

        // 6. Respawned signal
        player.Respawn();
        TestAssert.True(respawnedEmitted, "Respawned signal emitted on Respawn");
        TestAssert.Equal(4, healthChangedCount, "HealthChanged emitted on Respawn");
        TestAssert.Equal(100, lastNewHealth, "health restored to 100 on Respawn");

        // 7. DamageDealt signal on hitscan hit
        var target = new MapPlayer { PlayerId = 42 };
        target.Entity.O = new Vector3(0, 50, 14);
        target.Combat.Health = 100;
        target.Combat.Armour = 0;
        player.SelectWeapon(Gun.Rifle);
        player.Combat.Ammo[(int)Gun.Rifle] = 10;
        player.TryAttack(nowMillis: 1000, targets: new[] { target });

        TestAssert.Equal(1, damageDealtCount, "DamageDealt emitted when striking target");
        TestAssert.Equal(42, lastTargetId, "target ID matches target player");
        TestAssert.Equal(100, lastDamage, "damage dealt matches rifle damage");

        target.Free();
        player.Free();
    }

    [Test]
    public void PhysicsInstanceEventsAreIsolatedPerInstance()
    {
        var octa1 = new World.Octa();
        octa1.EmptyMap(10);
        var phys1 = new OctaPhysics(octa1);

        var octa2 = new World.Octa();
        octa2.EmptyMap(10);
        var phys2 = new OctaPhysics(octa2);

        int bounces1 = 0, dynCollides1 = 0, traces1 = 0;

        phys1.Bounced += (ent, wall) => bounces1++;
        phys1.DynEntCollide += (ent, other, wall) => dynCollides1++;
        phys1.MoveTrace = _ => traces1++;

        // Trigger events on phys2
        var bouncer2 = new PhysEnt
        {
            O = new Vector3(256, 256, 512.0f + 20.0f),
            Vel = new Vector3(0, 0, -60.0f),
            Type = Phys.EntBounce,
            PhysState = Phys.PhysFall,
            Radius = 4.1f,
            EyeHeight = 14.0f,
            AboveEye = 1.0f,
            NewPos = new Vector3(256, 256, 512.0f + 20.0f),
        };
        phys2.Bounce(bouncer2, 0.3f, 0.5f, 0.0f, 1.0f);

        var player2 = new PhysEnt
        {
            O = new Vector3(256, 256, 512.0f + 14.0f),
            Type = Phys.EntPlayer,
            Radius = 4.1f,
            EyeHeight = 14.0f,
            AboveEye = 1.0f,
            State = PhysEnt.CsAlive,
        };
        phys2.MovePlayer(player2, Phys.MoveRes, Phys.PhysFrameTime);

        TestAssert.Equal(0, bounces1, "phys1 received no bounces from phys2");
        TestAssert.Equal(0, dynCollides1, "phys1 received no dyn collisions from phys2");
        TestAssert.Equal(0, traces1, "phys1 received no move trace logs from phys2");

        // Now trigger on phys1
        var bouncer1 = new PhysEnt
        {
            O = new Vector3(256, 256, 512.0f + 20.0f),
            Vel = new Vector3(0, 0, -60.0f),
            Type = Phys.EntBounce,
            PhysState = Phys.PhysFall,
            Radius = 4.1f,
            EyeHeight = 14.0f,
            AboveEye = 1.0f,
            NewPos = new Vector3(256, 256, 512.0f + 20.0f),
        };
        phys1.Bounce(bouncer1, 0.3f, 0.5f, 0.0f, 1.0f);

        var player1 = new PhysEnt
        {
            O = new Vector3(256, 256, 512.0f + 14.0f),
            Type = Phys.EntPlayer,
            Radius = 4.1f,
            EyeHeight = 14.0f,
            AboveEye = 1.0f,
            State = PhysEnt.CsAlive,
        };
        phys1.MovePlayer(player1, Phys.MoveRes, Phys.PhysFrameTime);

        TestAssert.True(bounces1 > 0, "phys1 received bounce event on phys1 action");
        TestAssert.True(traces1 > 0, "phys1 received trace events on phys1 action");
    }
}
