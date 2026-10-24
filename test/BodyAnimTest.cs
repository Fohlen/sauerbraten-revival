using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Runtime.Formats.Ogz;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;
using MapPlayer = SauerbratenRevival.World.Game.Players.MapPlayer;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies <see cref="MapPlayer.SelectBodyAnim"/> mirrors the engine's
/// <c>renderclient</c> animation selection (rendermodel.cpp:1060-1110) and
/// <c>renderplayer</c> (render.cpp:133). Covers movement, hold, attack, pain,
/// dying and dead states. Pure state logic — no physics engine needed.
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
        // Default spawn weapon is pistol; pistol has hold=0 in renderplayer, so still -> idle.
        TestAssert.Equal("idle", p.SelectBodyAnim(), "still + pistol → idle");
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

    [Test]
    public void HoldsWeaponWhenStill()
    {
        var p = Player();

        // render.cpp:133 hold = mdl.vwep || d->gunselect==GUN_PISTOL ? 0 : (ANIM_HOLD1+d->gunselect)|ANIM_LOOP
        p.Combat.GunSelect = (int)Gun.Fist;
        TestAssert.Equal("hold 1", p.SelectBodyAnim(), "fist still → hold 1");

        p.Combat.GunSelect = (int)Gun.Shotgun;
        TestAssert.Equal("hold 2", p.SelectBodyAnim(), "shotgun still → hold 2");

        p.Combat.GunSelect = (int)Gun.Chaingun;
        TestAssert.Equal("hold 3", p.SelectBodyAnim(), "chaingun still → hold 3");

        p.Combat.GunSelect = (int)Gun.RocketLauncher;
        TestAssert.Equal("hold 4", p.SelectBodyAnim(), "rocket still → hold 4");

        p.Combat.GunSelect = (int)Gun.Rifle;
        TestAssert.Equal("hold 5", p.SelectBodyAnim(), "rifle still → hold 5");

        p.Combat.GunSelect = (int)Gun.GrenadeLauncher;
        TestAssert.Equal("hold 6", p.SelectBodyAnim(), "gl still → hold 6");

        // Pistol has hold=0 in renderplayer, so it falls back to idle.
        p.Combat.GunSelect = (int)Gun.Pistol;
        TestAssert.Equal("idle", p.SelectBodyAnim(), "pistol still → idle");

        p.Free();
    }

    [Test]
    public void MovementOverridesHold()
    {
        var p = Player();
        p.Combat.GunSelect = (int)Gun.Rifle;

        // Still -> hold 5
        TestAssert.Equal("hold 5", p.SelectBodyAnim(), "still → hold 5");

        // Moving forward -> forward wins over hold
        p.Entity.Move = 1;
        TestAssert.Equal("forward", p.SelectBodyAnim(), "running with rifle → forward");

        // Jumping -> jump wins over hold
        p.Entity.Move = 0;
        p.Entity.PhysState = Phys.PhysFall;
        p.Entity.TimeInAir = 150;
        TestAssert.Equal("jump", p.SelectBodyAnim(), "jumping with rifle → jump");

        // Water -> swim wins over hold
        p.Entity.InWater = Phys.MatWater;
        p.Entity.Move = 1;
        TestAssert.Equal("swim", p.SelectBodyAnim(), "swimming with rifle → swim");

        p.Free();
    }

    [Test]
    public void AttacksByGun()
    {
        var p = Player();

        // Rifle attack: attackdelay is 1500 + 50 = 1550 ms (WeaponCatalog rifle AttackDelay = 1500)
        p.Combat.GunSelect = (int)Gun.Rifle;
        p.Combat.LastAction = 1000;
        p.Combat.LastAttackGun = (int)Gun.Rifle;

        // Within attack window (1000..2550)
        TestAssert.Equal("attack 5", p.SelectBodyAnim(nowMillis: 1100), "rifle attack active → attack 5");

        // Attack beats movement on full-body model
        p.Entity.Move = 1;
        TestAssert.Equal("attack 5", p.SelectBodyAnim(nowMillis: 1100), "rifle attack active while moving → attack 5");

        // After attackdelay expires (>= 2550)
        TestAssert.Equal("forward", p.SelectBodyAnim(nowMillis: 2600), "rifle attack expired while moving → forward");
        p.Entity.Move = 0;
        TestAssert.Equal("hold 5", p.SelectBodyAnim(nowMillis: 2600), "rifle attack expired while still → hold 5");

        // Shotgun attack (AttackDelay 1400 + 50 = 1450)
        p.Combat.GunSelect = (int)Gun.Shotgun;
        p.Combat.LastAction = 2000;
        p.Combat.LastAttackGun = (int)Gun.Shotgun;
        TestAssert.Equal("attack 2", p.SelectBodyAnim(nowMillis: 2100), "shotgun attack → attack 2");

        // Pistol attack (AttackDelay 500 + 50 = 550)
        p.Combat.GunSelect = (int)Gun.Pistol;
        p.Combat.LastAction = 3000;
        p.Combat.LastAttackGun = (int)Gun.Pistol;
        TestAssert.Equal("attack 7", p.SelectBodyAnim(nowMillis: 3100), "pistol attack → attack 7");

        // Fist attack (AttackDelay 250 + 50 = 300)
        p.Combat.GunSelect = (int)Gun.Fist;
        p.Combat.LastAction = 4000;
        p.Combat.LastAttackGun = (int)Gun.Fist;
        TestAssert.Equal("attack 1", p.SelectBodyAnim(nowMillis: 4100), "fist attack → attack 1");

        p.Free();
    }

    [Test]
    public void PainOverridesAttackAndMovement()
    {
        var p = Player();
        p.Combat.GunSelect = (int)Gun.Rifle;
        p.Combat.LastAction = 1000;
        p.Combat.LastAttackGun = (int)Gun.Rifle;

        // Player takes damage at 1050 ms
        p.Combat.LastPain = 1050;

        // At 1100 ms (50 ms after pain < 300 ms): pain takes precedence over attack and movement
        p.Entity.Move = 1;
        TestAssert.Equal("pain", p.SelectBodyAnim(nowMillis: 1100), "pain < 300ms beats movement and attack → pain");

        // At 1400 ms (350 ms after pain >= 300 ms): pain expired, attack still active (1000 + 1550 = 2550)
        // Wait, rendermodel.cpp:1089 checks `lastpain < lastaction` for attack.
        // Since lastpain (1050) > lastaction (1000), attack was interrupted by pain!
        // So with Move=1, it reverts to movement.
        TestAssert.Equal("forward", p.SelectBodyAnim(nowMillis: 1400), "pain expired & interrupted attack → forward");

        p.Free();
    }

    [Test]
    public void DeathPlaysDyingThenDead()
    {
        var p = Player();
        p.Entity.State = PhysEnt.CsDead;
        p.Combat.LastPain = 1000;

        // Within 1000 ms of death: dying
        TestAssert.Equal("dying", p.SelectBodyAnim(nowMillis: 1500), "500ms after death → dying");
        TestAssert.Equal("dying", p.SelectBodyAnim(nowMillis: 2000), "1000ms after death → dying");

        // After 1000 ms: dead (looping)
        TestAssert.Equal("dead", p.SelectBodyAnim(nowMillis: 2001), "1001ms after death → dead");
        TestAssert.Equal("dead", p.SelectBodyAnim(nowMillis: 5000), "long after death → dead");

        p.Free();
    }

    [Test]
    public void LaggedState()
    {
        var p = Player();
        p.Entity.State = PhysEnt.CsLagged;
        TestAssert.Equal("lag", p.SelectBodyAnim(), "lagged → lag");
        p.Free();
    }

    [Test]
    public void LoopingClipsClassification()
    {
        TestAssert.True(MapPlayer.IsLoopingAnim("idle"), "idle loops");
        TestAssert.True(MapPlayer.IsLoopingAnim("hold 1"), "hold 1 loops");
        TestAssert.True(MapPlayer.IsLoopingAnim("hold 5"), "hold 5 loops");
        TestAssert.True(MapPlayer.IsLoopingAnim("forward"), "forward loops");
        TestAssert.True(MapPlayer.IsLoopingAnim("dead"), "dead loops");
        TestAssert.True(MapPlayer.IsLoopingAnim("swim"), "swim loops");
        TestAssert.True(MapPlayer.IsLoopingAnim("edit"), "edit loops");

        TestAssert.True(!MapPlayer.IsLoopingAnim("jump"), "jump does not loop");
        TestAssert.True(!MapPlayer.IsLoopingAnim("dying"), "dying does not loop");
        TestAssert.True(!MapPlayer.IsLoopingAnim("pain"), "pain does not loop");
        TestAssert.True(!MapPlayer.IsLoopingAnim("attack 1"), "attack 1 does not loop");
        TestAssert.True(!MapPlayer.IsLoopingAnim("attack 5"), "attack 5 does not loop");
    }

    [Test]
    public void WeaponSelectionAndFiring()
    {
        var p = Player();
        p.LastMillis = 1000;

        // Select shotgun
        p.SelectWeapon(Gun.Shotgun);
        TestAssert.Equal((int)Gun.Shotgun, p.Combat.GunSelect, "selected shotgun");
        TestAssert.Equal("hold 2", p.SelectBodyAnim(), "shotgun still → hold 2");

        // Give ammo
        p.Combat.Ammo[(int)Gun.Shotgun] = 10;

        // Fire shotgun
        bool fired = p.TryAttack(nowMillis: 1000);
        TestAssert.True(fired, "shotgun fired");
        TestAssert.Equal(1000, p.Combat.LastAction, "lastaction recorded");
        TestAssert.Equal((int)Gun.Shotgun, p.Combat.LastAttackGun, "lastattackgun recorded");
        TestAssert.Equal(9, p.Combat.Ammo[(int)Gun.Shotgun], "shotgun ammo consumed");
        TestAssert.Equal(1400, p.Combat.GunWait, "gunwait matches shotgun AttackDelay");

        // Animation now reflects attack 2
        TestAssert.Equal("attack 2", p.SelectBodyAnim(nowMillis: 1100), "shooting shotgun → attack 2");

        // Firing again before GunWait expires must fail
        TestAssert.True(!p.TryAttack(nowMillis: 1500), "firing before gunwait fails");

        // Take damage
        p.TakeDamage(25, nowMillis: 2000);
        TestAssert.Equal(75, p.Combat.Health, "health reduced");
        TestAssert.Equal(2000, p.Combat.LastPain, "lastpain recorded");
        TestAssert.Equal("pain", p.SelectBodyAnim(nowMillis: 2050), "in pain → pain");

        // Fatal damage kills
        p.TakeDamage(100, nowMillis: 2500);
        TestAssert.Equal(0, p.Combat.Health, "health zeroed");
        TestAssert.Equal(PhysEnt.CsDead, p.Entity.State, "player state set to dead");
        TestAssert.True(p.Entity.Dead, "entity dead flag set");
        TestAssert.Equal("dying", p.SelectBodyAnim(nowMillis: 2600), "freshly dead → dying");

        // Attack while dead triggers respawn
        p.TryAttack(nowMillis: 3000);
        TestAssert.Equal(PhysEnt.CsAlive, p.Entity.State, "attack while dead triggers respawn");
        TestAssert.True(!p.Entity.Dead, "no longer dead");
        TestAssert.Equal(100, p.Combat.Health, "health restored");

        p.Free();
    }
}
