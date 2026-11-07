using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Objectives;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class ObjectiveCatalogTest : GoTestBase
{
    public ObjectiveCatalogTest(Node testScene) : base(testScene) { }

    [Test]
    public void CatalogLoadsAllBuiltInObjectives()
    {
        string[] required = { "dm", "edit", "ctf" };
        TestAssert.Equal(required.Length, ObjectiveCatalog.All.Length, "built-in objective count");
        foreach (var id in required)
        {
            var obj = ObjectiveCatalog.TryGet(id);
            TestAssert.True(obj != null, $"objective '{id}' exists in catalog");
            TestAssert.Equal(id, obj!.Id, $"objective '{id}' id matches");
        }
    }

    [Test]
    public void FlagObjective_RelayRulesAndHudState()
    {
        var ctfRes = ObjectiveCatalog.Get("ctf") as FlagObjectiveResource;
        TestAssert.True(ctfRes != null, "ctf is FlagObjectiveResource");
        TestAssert.Equal(FlagPlacementMode.MapTeamFlags, ctfRes!.Placement, "placement");
        TestAssert.Equal(FlagScoreRule.RelayToFriendlyBase, ctfRes.ScoreRule, "score rule");
        TestAssert.Equal(FlagReturnRule.TouchDropped, ctfRes.ReturnRule, "return rule");

        var mode = GameModeCatalog.Get("ctf");
        var obj = ctfRes.CreateInstance(mode) as FlagObjective;
        TestAssert.True(obj != null, "created FlagObjective");

        // Claims ET_FLAG
        var flagEnt = new MapEntity { Type = EntityType.Flag, Attr1 = 0, Attr2 = 1, Position = new Vector3(100, 100, 10) };
        TestAssert.True(obj!.ClaimsEntity(flagEnt), "claims flag entity");

        var baseEnt = new MapEntity { Type = EntityType.Base, Position = new Vector3(200, 200, 10) };
        TestAssert.False(obj.ClaimsEntity(baseEnt), "ctf does not claim base entity");

        // Setup world
        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.Flag, Attr1 = 0, Attr2 = 1, Position = new Vector3(10, 0, 0) },
            new() { Type = EntityType.Flag, Attr1 = 0, Attr2 = 2, Position = new Vector3(20, 0, 0) },
        };
        obj.SetupWorld(null!, entities, new Node3D());
        TestAssert.Equal(2, obj.Flags.Flags.Count, "created 2 flags");

        // Scoring and carrier hud state
        obj.Flags.SetScore(1, 3);
        obj.Flags.SetScore(2, 1);
        obj.Flags.OwnFlag(1, 42, 1000); // player 42 owns flag 1 (red flag)

        var player = new MapPlayer { PlayerId = 42, Team = 1 };
        var hudState = obj.GetHudState(player, 1000);
        TestAssert.Equal(3, hudState.PrimaryScore, "blue score");
        TestAssert.Equal(1, hudState.SecondaryScore, "red score");
        TestAssert.Equal(HudIcon.RedFlag, hudState.StatusIcon ?? HudIcon.Fist, "carrying enemy flag");
    }

    [Test]
    public void FlagObjective_DataDrivenRuleParametersAffectRuntimeBehavior()
    {
        // ADR 0012 Example 1: fast_ctf data-driven flag variant with ResetDelayMillis = 5000
        var fastCtfRes = new FlagObjectiveResource
        {
            Id = "fast_ctf",
            Name = "fast ctf",
            ResetDelayMillis = 5000,
            InvisDelayMillis = 15000,
            HoldSeconds = 12,
            HoldDeathPenaltySeconds = 3,
        };

        var fastMode = new GameModeInfo
        {
            Id = "fast_ctf",
            Name = "fast ctf",
            Objective = fastCtfRes,
        };

        var obj = fastCtfRes.CreateInstance(fastMode) as FlagObjective;
        TestAssert.True(obj != null, "created fast_ctf FlagObjective");

        // 1. ResetDelayMillis: dropped flag resets after 5000ms instead of default 10000ms
        obj!.Flags.AddFlag(0, new Vector3(0, 0, 0), 1, 0, false);
        obj.Flags.DropFlag(0, new Vector3(50, 50, 0), 1000);
        TestAssert.True(obj.Flags.Flags[0].DropTime > 0, "flag is dropped");

        // Step at 4999ms elapsed (1000 + 4999 = 5999ms): not reset yet
        obj.Step(null!, 5, 5999);
        TestAssert.Equal(1000, obj.Flags.Flags[0].DropTime, "flag not reset before 5000ms delay");

        // Step at 5000ms elapsed (1000 + 5000 = 6000ms): reset occurs!
        obj.Step(null!, 5, 6000);
        TestAssert.Equal(0, obj.Flags.Flags[0].DropTime, "flag resets at exactly 5000ms ResetDelayMillis");

        // 2. InvisDelayMillis: protect touch cooldown uses custom 12000ms instead of 20000ms
        var protectRes = new FlagObjectiveResource
        {
            Id = "custom_protect",
            ScoreRule = FlagScoreRule.TouchEnemyFlag,
            ReturnRule = FlagReturnRule.DropInsideBase,
            InvisDelayMillis = 12000,
        };
        var protectMode = new GameModeInfo
        {
            Id = "custom_protect",
            Objective = protectRes,
        };
        var protectObj = protectRes.CreateInstance(protectMode) as FlagObjective;
        protectObj!.Flags.AddFlag(0, new Vector3(0, 0, 0), 1, 0, isProtect: true);
        protectObj.Flags.Flags[0].InvisTime = 1000;

        // Player 2 attempts to touch enemy flag before 12000ms has elapsed: fails
        bool scoredEarly = protectObj.Flags.TakeFlag(2, 2, new Vector3(0, 0, 0), 0, protectObj.Flags.Flags[0].Version, 1000 + 11999);
        TestAssert.False(scoredEarly, "cannot score during custom 12s InvisDelayMillis cooldown");

        // Step clears invis cooldown after 12000ms
        protectObj.Step(null!, 5, 1000 + 12000);
        bool scored = protectObj.Flags.TakeFlag(2, 2, new Vector3(0, 0, 0), 0, protectObj.Flags.Flags[0].Version, 1000 + 12000);
        TestAssert.True(scored, "touch enemy flag scores once custom InvisDelayMillis expires");

        // 3. HoldSeconds: scores after 12 seconds instead of default 20 seconds
        var holdRes = new FlagObjectiveResource
        {
            Id = "custom_hold",
            Placement = FlagPlacementMode.DynamicHoldSpawns,
            ScoreRule = FlagScoreRule.HoldTimer,
            ReturnRule = FlagReturnRule.RandomRespawn,
            HoldSeconds = 12,
            HoldDeathPenaltySeconds = 3,
        };
        var holdMode = new GameModeInfo
        {
            Id = "custom_hold",
            Objective = holdRes,
        };
        var holdObj = holdRes.CreateInstance(holdMode) as FlagObjective;
        holdObj!.Flags.AddHoldSpawn(new Vector3(10, 10, 0));
        holdObj.Flags.SetupHoldSpawns();
        holdObj.Flags.TakeFlag(1, 1, Vector3.Zero, 0, holdObj.Flags.Flags[0].Version, 1000);

        // Step at 11s elapsed: no score
        int p1Score = 0;
        holdObj.Flags.Update(1000 + 11000, pid => (1, Vector3.Zero), (pid, s) => p1Score += s);
        TestAssert.Equal(0, p1Score, "not scored yet after 11s of 12s hold");

        // Step at 12s elapsed: scores!
        holdObj.Flags.Update(1000 + 12000, pid => (1, Vector3.Zero), (pid, s) => p1Score += s);
        TestAssert.Equal(1, p1Score, "scored after 12s HoldSeconds");

        // 4. HoldDeathPenaltySeconds: carrier dies, penalty is 3s instead of default 5s
        holdObj.Flags.TakeFlag(1, 1, Vector3.Zero, 0, holdObj.Flags.Flags[0].Version, 20000);
        var victim = new MapPlayer { PlayerId = 1, Team = 1, Mode = holdMode };
        victim.Flags = holdObj.Flags;
        holdObj.OnPlayerDied(victim, null, Gun.Pistol, 25000);
        // Prior to death, holdtime was 20000. 3s penalty adds 3000 -> 23000
        TestAssert.Equal(23000, holdObj.Flags.Flags[0].HoldTime, "holdtime incremented by custom 3s HoldDeathPenaltySeconds");
    }

    [Test]
    public void DeathmatchObjective_ScoringAndPenalties()
    {
        var dmRes = ObjectiveCatalog.Get("dm") as DeathmatchObjectiveResource;
        TestAssert.True(dmRes != null, "dm is DeathmatchObjectiveResource");

        var mode = GameModeCatalog.Get("teamplay");
        var obj = dmRes!.CreateInstance(mode) as DeathmatchObjective;
        TestAssert.True(obj != null, "created DeathmatchObjective");

        var player1 = new MapPlayer { PlayerId = 1, Team = 1 };
        var player2 = new MapPlayer { PlayerId = 2, Team = 2 };

        // Normal frag
        obj!.OnPlayerDied(player2, player1, Gun.Shotgun, 1000);
        TestAssert.Equal(1, obj.GetPlayerFrags(1), "player 1 frag +1");
        TestAssert.Equal(1, obj.GetTeamScore(1), "team 1 score +1");

        // Teamkill penalty
        var player3 = new MapPlayer { PlayerId = 3, Team = 1 };
        obj.OnPlayerDied(player3, player1, Gun.RocketLauncher, 2000);
        TestAssert.Equal(0, obj.GetPlayerFrags(1), "player 1 penalized for tk");
        TestAssert.Equal(0, obj.GetTeamScore(1), "team 1 penalized for tk");

        // Suicide penalty
        obj.OnPlayerDied(player1, null, Gun.RocketLauncher, 3000);
        TestAssert.Equal(-1, obj.GetPlayerFrags(1), "player 1 suicide penalty");
        TestAssert.Equal(-1, obj.GetTeamScore(1), "team 1 suicide penalty");

        var hud = obj.GetHudState(player1, 3000);
        TestAssert.Equal(-1, hud.PrimaryScore, "hud primary score");
        TestAssert.Equal(0, hud.SecondaryScore, "hud secondary score");
    }

    [Test]
    public void MapPlayer_InfiniteAmmo_BypassesAmmoConsumption()
    {
        var infiniteMode = new GameModeInfo
        {
            Id = "custom_infinite",
            Name = "Infinite Ammo Custom",
            ObjectiveId = "dm",
            InfiniteAmmo = true,
        };
        infiniteMode.Bake();

        var player = new MapPlayer { Mode = infiniteMode };
        player.Respawn(new Vector3(0, 0, 0));
        player.Combat.GunSelect = (int)Gun.Rifle;
        player.Combat.Ammo[(int)Gun.Rifle] = 0; // 0 ammo

        // Should be able to attack without ammo
        bool fired = player.TryAttack(nowMillis: 1000);
        TestAssert.True(fired, "attack succeeds with 0 ammo under InfiniteAmmo");
        TestAssert.Equal(0, player.Combat.Ammo[(int)Gun.Rifle], "ammo unchanged at 0");
    }

    [Test]
    public void MapPlayer_SelfDamageAndPushScaling()
    {
        var customMode = new GameModeInfo
        {
            Id = "custom_rocket_jump",
            Name = "Rocket Jump Custom",
            ObjectiveId = "dm",
            SelfDamageScale = 0.25f,
            SelfPushScale = 2.0f,
        };
        customMode.Bake();

        var player = new MapPlayer { Mode = customMode };
        player.Respawn(new Vector3(0, 0, 0));
        player.Combat.Armour = 0; // Strip armour so damage directly affects health
        int initialHealth = player.Combat.Health;

        var initialVel = player.Entity.Vel;
        player.TakeDamage(100, attacker: player, gun: Gun.RocketLauncher, pushVel: new Vector3(0, 10, 0), nowMillis: 1000);

        // Damage is scaled by 0.25 -> 25 damage
        TestAssert.Equal(initialHealth - 25, player.Combat.Health, "damage scaled by SelfDamageScale");
        // Push is scaled by 2.0 -> (0, 20, 0)
        TestAssert.Equal(initialVel + new Vector3(0, 20, 0), player.Entity.Vel, "push scaled by SelfPushScale");
    }
}
