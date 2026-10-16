using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class CrosshairTest : GoTestBase
{
    public CrosshairTest(Node testScene) : base(testScene) { }

    [Test]
    public void HiddenWhenDead()
    {
        var renderer = new CrosshairRenderer();
        var player = new MapPlayer();
        player.Combat.Health = 0;
        player.Entity.State = PhysEnt.CsDead;
        player.Entity.Dead = true;

        int index = renderer.SelectCrosshair(
            player,
            nowMillis: 1000,
            isTeamPlay: false,
            aimedTarget: null,
            out _);

        TestAssert.True(index == -1, "hidden when dead");
        player.Free();
    }

    [Test]
    public void DefaultCrosshairWhenAlive()
    {
        var renderer = new CrosshairRenderer();
        var player = new MapPlayer();
        player.Combat.Health = 100;
        player.Entity.State = PhysEnt.CsAlive;

        int index = renderer.SelectCrosshair(
            player,
            nowMillis: 1000,
            isTeamPlay: false,
            aimedTarget: null,
            out Color color);

        TestAssert.True(index == CrosshairRenderer.CrosshairDefault, "default crosshair index");
        TestAssert.True(color == Colors.White, "white color");
        player.Free();
    }

    [Test]
    public void HitConfirmationTriggersHitCrosshair()
    {
        var renderer = new CrosshairRenderer();
        var player = new MapPlayer();
        player.Combat.Health = 100;
        player.Entity.State = PhysEnt.CsAlive;
        player.LastHit = 1000;

        // At 1200ms, lastHit is 200ms ago (< 425ms)
        int index = renderer.SelectCrosshair(
            player,
            nowMillis: 1200,
            isTeamPlay: false,
            aimedTarget: null,
            out _);

        TestAssert.True(index == CrosshairRenderer.CrosshairHit, "hit crosshair active");

        // At 1500ms, lastHit is 500ms ago (> 425ms), reverts to default
        index = renderer.SelectCrosshair(
            player,
            nowMillis: 1500,
            isTeamPlay: false,
            aimedTarget: null,
            out _);

        TestAssert.True(index == CrosshairRenderer.CrosshairDefault, "reverts to default after timeout");
        player.Free();
    }

    [Test]
    public void TeammateCrosshairWhenAimingAtAlly()
    {
        var renderer = new CrosshairRenderer();
        var player = new MapPlayer { Team = 1 };
        player.Combat.Health = 100;
        player.Entity.State = PhysEnt.CsAlive;

        var teammate = new MapPlayer { Team = 1 };
        teammate.Combat.Health = 100;
        teammate.Entity.State = PhysEnt.CsAlive;

        var enemy = new MapPlayer { Team = 2 };
        enemy.Combat.Health = 100;
        enemy.Entity.State = PhysEnt.CsAlive;

        // Aiming at teammate in team play
        int index = renderer.SelectCrosshair(
            player,
            nowMillis: 1000,
            isTeamPlay: true,
            aimedTarget: teammate,
            out Color color);

        TestAssert.True(index == CrosshairRenderer.CrosshairTeammate, "teammate crosshair index");
        TestAssert.True(color == new Color(0f, 0f, 1f), "blue color for teammate");

        // Aiming at enemy in team play
        index = renderer.SelectCrosshair(
            player,
            nowMillis: 1000,
            isTeamPlay: true,
            aimedTarget: enemy,
            out color);

        TestAssert.True(index == CrosshairRenderer.CrosshairDefault, "default crosshair for enemy");
        TestAssert.True(color == Colors.White, "white color for enemy");

        // Aiming at teammate in FFA (isTeamPlay = false)
        index = renderer.SelectCrosshair(
            player,
            nowMillis: 1000,
            isTeamPlay: false,
            aimedTarget: teammate,
            out color);

        TestAssert.True(index == CrosshairRenderer.CrosshairDefault, "default crosshair in ffa");
        TestAssert.True(color == Colors.White, "white color in ffa");

        player.Free();
        teammate.Free();
        enemy.Free();
    }

    [Test]
    public void HealthColorWarning()
    {
        var renderer = new CrosshairRenderer();
        var player = new MapPlayer();
        player.Entity.State = PhysEnt.CsAlive;

        // Health <= 25 -> Red
        player.Combat.Health = 25;
        renderer.SelectCrosshair(player, 1000, false, null, out Color redColor);
        TestAssert.True(redColor == new Color(1f, 0f, 0f), "red color when health <= 25");

        // Health <= 50 -> Orange
        player.Combat.Health = 50;
        renderer.SelectCrosshair(player, 1000, false, null, out Color orangeColor);
        TestAssert.True(orangeColor == new Color(1f, 0.5f, 0f), "orange color when health <= 50");

        // Health > 50 -> White
        player.Combat.Health = 75;
        renderer.SelectCrosshair(player, 1000, false, null, out Color whiteColor);
        TestAssert.True(whiteColor == Colors.White, "white color when health > 50");

        player.Free();
    }

    [Test]
    public void GunWaitDimsCrosshair()
    {
        var renderer = new CrosshairRenderer();
        var player = new MapPlayer();
        player.Combat.Health = 100;
        player.Entity.State = PhysEnt.CsAlive;
        player.Combat.GunWait = 250; // Weapon cooling down

        renderer.SelectCrosshair(player, 1000, false, null, out Color dimmedColor);
        TestAssert.True(Mathf.IsEqualApprox(dimmedColor.R, 0.5f), "dimmed red");
        TestAssert.True(Mathf.IsEqualApprox(dimmedColor.G, 0.5f), "dimmed green");
        TestAssert.True(Mathf.IsEqualApprox(dimmedColor.B, 0.5f), "dimmed blue");

        player.Free();
    }

    [Test]
    public void SizeCalculationMatchesEngineRatio()
    {
        var renderer = new CrosshairRenderer { CrosshairSize = 30 };
        // chsize = crosshairsize * w / 900.0f
        // For screen width 1800: 30 * 1800 / 900 = 60
        float size = renderer.CalculateSize(1800f);
        TestAssert.True(Mathf.IsEqualApprox(size, 60f), "size at width 1800");

        // For screen width 900: 30 * 900 / 900 = 30
        size = renderer.CalculateSize(900f);
        TestAssert.True(Mathf.IsEqualApprox(size, 30f), "size at width 900");
    }
}
