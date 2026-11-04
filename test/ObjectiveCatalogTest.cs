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
        string[] required = { "dm", "edit", "ctf", "protect", "hold", "rugby", "capture", "collect", "race" };
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
    public void FlagObjective_Rugby_PassOnFriendlyHitTransfersFlag()
    {
        var rugbyRes = ObjectiveCatalog.Get("rugby") as FlagObjectiveResource;
        TestAssert.True(rugbyRes != null, "rugby is FlagObjectiveResource");
        TestAssert.True(rugbyRes!.PassOnFriendlyHit, "PassOnFriendlyHit is true for rugby");

        var mode = GameModeCatalog.Get("rugby");
        var obj = rugbyRes.CreateInstance(mode) as FlagObjective;
        TestAssert.True(obj != null, "created FlagObjective");

        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.Flag, Attr1 = 0, Attr2 = 1, Position = new Vector3(10, 0, 0) },
            new() { Type = EntityType.Flag, Attr1 = 0, Attr2 = 2, Position = new Vector3(20, 0, 0) },
        };
        obj!.SetupWorld(null!, entities, new Node3D());

        // Player 1 (Blue) carries enemy flag (flag 1)
        int carrierId = 10;
        int receiverId = 20;
        obj.Flags.OwnFlag(1, carrierId, 5000);
        TestAssert.Equal(carrierId, obj.Flags.GetCarrier(1) ?? -1, "initial carrier");

        var carrier = new MapPlayer { PlayerId = carrierId, Team = 1 };
        var receiver = new MapPlayer { PlayerId = receiverId, Team = 1 };

        int damage = 100;
        var pushVel = new Vector3(0, 0, 10);
        bool allowDamage = obj.OnBeforeDamage(carrier, receiver, Gun.Rifle, ref damage, ref pushVel, 5100);

        // Friendly hit while carrying flag must transfer flag and cancel damage
        TestAssert.False(allowDamage, "friendly damage canceled");
        TestAssert.Equal(0, damage, "damage zeroed");
        TestAssert.Equal(Vector3.Zero, pushVel, "push velocity zeroed");
        TestAssert.Equal(receiverId, obj.Flags.GetCarrier(1) ?? -1, "flag ownership transferred to receiver");
        TestAssert.Equal(1, obj.Flags.GetCarriedFlag(receiverId) ?? -1, "receiver carried flag index");
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
    public void RaceObjective_CheckpointsAndRespawn()
    {
        var raceRes = ObjectiveCatalog.Get("race") as RaceObjectiveResource;
        TestAssert.True(raceRes != null, "race is RaceObjectiveResource");

        var mode = GameModeCatalog.Get("race");
        var obj = raceRes!.CreateInstance(mode) as RaceObjective;
        TestAssert.True(obj != null, "created RaceObjective");

        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.RespawnPoint, Attr1 = 90, Attr2 = 0, Position = new Vector3(0, 0, 0) },
            new() { Type = EntityType.Carrot, Attr1 = 180, Attr2 = 1, Position = new Vector3(100, 0, 0) },
            new() { Type = EntityType.Carrot, Attr1 = 270, Attr2 = 2, Position = new Vector3(200, 0, 0) },
        };
        obj!.SetupWorld(null!, entities, new Node3D());
        TestAssert.Equal(3, obj.Checkpoints.Count, "checkpoint count 3");

        var player = new MapPlayer { PlayerId = 5 };
        player.Respawn(new Vector3(0, 0, 0));

        // Before any checkpoint
        var spawn0 = obj.ResolveSpawn(player);
        TestAssert.True(spawn0 == null, "no checkpoint reached yet");

        // Trigger checkpoint 0
        obj.StepPlayer(player, 100);
        var spawn1 = obj.ResolveSpawn(player);
        TestAssert.True(spawn1 != null, "reached cp 0");
        TestAssert.Equal(new Vector3(0, 0, 0), spawn1!.Value.FeetPos, "cp 0 pos");
        TestAssert.Equal(90f, spawn1.Value.Yaw, "cp 0 yaw");

        // Move to checkpoint 1
        player.Respawn(new Vector3(100, 0, 0));
        obj.StepPlayer(player, 200);
        var spawn2 = obj.ResolveSpawn(player);
        TestAssert.True(spawn2 != null, "reached cp 1");
        TestAssert.Equal(new Vector3(100, 0, 0), spawn2!.Value.FeetPos, "cp 1 pos");
        TestAssert.Equal(180f, spawn2.Value.Yaw, "cp 1 yaw");
    }
}
