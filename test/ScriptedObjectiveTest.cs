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

public class ScriptedObjectiveTest : GoTestBase
{
    public ScriptedObjectiveTest(Node testScene) : base(testScene) { }

    [Test]
    public void LoadScriptedObjectiveResource_FromTres()
    {
        var res = GD.Load<ScriptedObjectiveResource>("res://test/fixtures/koth.tres");
        TestAssert.True(res != null, "loaded koth.tres");
        TestAssert.Equal("koth", res!.Id, "id");
        TestAssert.Equal("King of the Hill", res.Name, "name");
        TestAssert.True(res.ObjectiveScript != null, "ObjectiveScript is set");
        TestAssert.True(res.DefaultTeamPlay, "teamplay");
    }

    [Test]
    public void ScriptedObjective_FullContractExercised()
    {
        var res = GD.Load<ScriptedObjectiveResource>("res://test/fixtures/koth.tres");
        TestAssert.True(res != null, "loaded koth.tres");

        var mode = GameModeCatalog.Get("teamplay");
        var obj = res!.CreateInstance(mode);
        TestAssert.True(obj != null, "created ScriptedObjective instance");

        // 1. claims_entity passes MapEntity data, not just integer
        var baseEntity = new MapEntity { Type = EntityType.Base, Position = new Vector3(50, 60, 70) };
        var flagEntity = new MapEntity { Type = EntityType.Flag, Position = new Vector3(10, 20, 30) };
        TestAssert.True(obj!.ClaimsEntity(baseEntity), "claims base entity (EntityType.Base = 19)");
        TestAssert.False(obj.ClaimsEntity(flagEntity), "does not claim flag entity");

        // 2. setup_world receives (world, entities, root)
        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.Light, Position = new Vector3(0, 0, 0) },
            baseEntity,
            flagEntity
        };
        var rootNode = new Node3D();
        obj.SetupWorld(null!, entities, rootNode);

        // 3. resolve_spawn
        var player1 = new MapPlayer { PlayerId = 1, Team = 1 };
        var spawn = obj.ResolveSpawn(player1);
        TestAssert.True(spawn.HasValue, "resolve_spawn returned a value");
        TestAssert.Equal(new Vector3(10, 20, 30), spawn!.Value.FeetPos, "spawn pos");
        TestAssert.Equal(180.0f, spawn.Value.Yaw, "spawn yaw");

        // 4. on_player_spawn
        obj.OnPlayerSpawn(player1);

        // 5. step and step_player
        obj.Step(null!, 500, 1000);
        player1.Entity.O = new Vector3(50, 60, 70); // inside hill radius (< 64)
        player1.Entity.State = SauerbratenRevival.World.Physics.PhysEnt.CsAlive;
        obj.StepPlayer(player1, 1000);

        // Score should have increased for team 1
        var hud = obj.GetHudState(player1, 1000);
        TestAssert.Equal(1, hud.PrimaryScore, "team 1 score after stepping near hill");
        TestAssert.Equal(0, hud.SecondaryScore, "team 2 score");
        TestAssert.Equal(HudIcon.Health, hud.StatusIcon ?? HudIcon.Fist, "status icon");
        TestAssert.Equal(1, hud.StatusCounter, "status counter");

        // 6. on_before_damage:
        // Case A: Friendly fire is canceled
        var friendlyAttacker = new MapPlayer { PlayerId = 2, Team = 1 };
        int damage = 100;
        var push = new Vector3(50, 0, 0);
        bool allowDamage = obj.OnBeforeDamage(friendlyAttacker, player1, Gun.Rifle, ref damage, ref push, 1000);
        TestAssert.False(allowDamage, "friendly fire is canceled by script");

        // Case B: Enemy fire against player on the hill mutates damage and push
        var enemyAttacker = new MapPlayer { PlayerId = 3, Team = 2 };
        damage = 100;
        push = new Vector3(50, 0, 0);
        allowDamage = obj.OnBeforeDamage(enemyAttacker, player1, Gun.Rifle, ref damage, ref push, 1000);
        TestAssert.True(allowDamage, "enemy fire is allowed");
        TestAssert.Equal(50, damage, "damage was halved by script (50% reduction on hill)");
        TestAssert.Equal(Vector3.Zero, push, "knockback push was neutralized by script");

        // Case C: Enemy fire against player far away from the hill is unmodified
        var farPlayer = new MapPlayer { PlayerId = 4, Team = 1 };
        farPlayer.Entity.O = new Vector3(500, 500, 500);
        damage = 80;
        push = new Vector3(20, 10, 0);
        allowDamage = obj.OnBeforeDamage(enemyAttacker, farPlayer, Gun.Rifle, ref damage, ref push, 1000);
        TestAssert.True(allowDamage, "enemy fire is allowed on far player");
        TestAssert.Equal(80, damage, "damage unmodified for player far from hill");
        TestAssert.Equal(new Vector3(20, 10, 0), push, "push unmodified for player far from hill");

        // 7. on_player_died
        obj.OnPlayerDied(player1, enemyAttacker, Gun.Rifle, 2000);

        rootNode.Free();
    }

    [Test]
    public void ScriptedObjective_CombatIntegration_ViaMapPlayerTakeDamage()
    {
        var res = GD.Load<ScriptedObjectiveResource>("res://test/fixtures/koth.tres");
        var mode = GameModeCatalog.Get("teamplay");
        var obj = res!.CreateInstance(mode);

        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.Base, Position = new Vector3(100, 100, 0) }
        };
        var root = new Node3D();
        obj.SetupWorld(null!, entities, root);

        var defender = new MapPlayer { Objective = obj, Team = 1, Mode = mode };
        defender.Respawn(new Vector3(100, 100, 0));
        defender.Combat.Armour = 0;
        int initialHealth = defender.Combat.Health;

        var friendly = new MapPlayer { Objective = obj, Team = 1, Mode = mode };
        var enemy = new MapPlayer { Objective = obj, Team = 2, Mode = mode };

        // 1. Friendly fire should deal 0 damage
        defender.TakeDamage(100, attacker: friendly, gun: Gun.Rifle, pushVel: new Vector3(10, 0, 0), nowMillis: 1000);
        TestAssert.Equal(initialHealth, defender.Combat.Health, "friendly fire canceled by script");
        TestAssert.Equal(Vector3.Zero, defender.Entity.Vel, "friendly fire push canceled");

        // 2. Enemy fire while on hill: 50% damage reduction and push neutralized
        defender.TakeDamage(100, attacker: enemy, gun: Gun.Rifle, pushVel: new Vector3(20, 0, 0), nowMillis: 2000);
        TestAssert.Equal(initialHealth - 50, defender.Combat.Health, "enemy damage halved on hill");
        TestAssert.Equal(Vector3.Zero, defender.Entity.Vel, "enemy push neutralized on hill");

        // 3. Enemy fire while away from hill: full damage and push
        defender.Entity.O = new Vector3(1000, 1000, 0);
        defender.Combat.Health = initialHealth;
        defender.TakeDamage(50, attacker: enemy, gun: Gun.Rifle, pushVel: new Vector3(30, 0, 0), nowMillis: 3000);
        TestAssert.Equal(initialHealth - 50, defender.Combat.Health, "enemy damage full off hill");
        TestAssert.Equal(new Vector3(30, 0, 0), defender.Entity.Vel, "enemy push full off hill");

        root.Free();
    }
}
