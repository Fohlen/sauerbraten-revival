using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Objectives;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Physics;
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
        TestAssert.Equal(100, res.TickIntervalMillis, "default 100ms (10 Hz) bounded tick");
    }

    [Test]
    public void ScriptedObjective_EventDrivenZones_BoundedTick_AndPushHud()
    {
        var res = GD.Load<ScriptedObjectiveResource>("res://test/fixtures/koth.tres");
        TestAssert.True(res != null, "loaded koth.tres");

        var mode = GameModeCatalog.Get("teamplay");
        var obj = (ScriptedObjective)res!.CreateInstance(mode);
        TestAssert.True(obj != null, "created ScriptedObjective instance");

        // 1. claims_entity passes MapEntity data
        var baseEntity = new MapEntity { Type = EntityType.Base, Position = new Vector3(50, 60, 70) };
        var flagEntity = new MapEntity { Type = EntityType.Flag, Position = new Vector3(10, 20, 30) };
        TestAssert.True(obj!.ClaimsEntity(baseEntity), "claims base entity");
        TestAssert.False(obj.ClaimsEntity(flagEntity), "does not claim flag entity");

        // 2. setup_world registers native sphere zone & default spawn override in ObjectiveContext
        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.Light, Position = new Vector3(0, 0, 0) },
            baseEntity,
            flagEntity
        };
        var rootNode = new Node3D();
        obj.SetupWorld(null!, entities, rootNode);

        // 3. resolve_spawn reads directly from C# ObjectiveContext cache
        var player1 = new MapPlayer { PlayerId = 1, Team = 1 };
        var spawn = obj.ResolveSpawn(player1);
        TestAssert.True(spawn.HasValue, "resolve_spawn returned cached value");
        TestAssert.Equal(new Vector3(10, 20, 30), spawn!.Value.FeetPos, "spawn pos");
        TestAssert.Equal(180.0f, spawn.Value.Yaw, "spawn yaw");

        // 4. on_player_spawn
        player1.Entity.O = new Vector3(500, 500, 500);
        player1.Entity.State = PhysEnt.CsAlive;
        obj.OnPlayerSpawn(player1);

        // 5. Native zone transition detection: entering the hill fires on_zone_event once
        player1.Entity.O = new Vector3(50, 60, 70); // inside hill radius (< 64)
        obj.StepPlayer(player1, 100);
        TestAssert.True(obj.Context.is_player_in_zone(player1.PlayerId, 0), "player1 is in hill zone 0");
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 1), "team 1 has 1 player in zone");

        // Stepping again while still inside the zone does NOT re-trigger transition
        obj.StepPlayer(player1, 105);
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 1), "team 1 count remains 1");

        // Status icon was pushed by on_zone_event on entry
        var hudBeforeTick = obj.GetHudState(player1, 105);
        TestAssert.Equal(0, hudBeforeTick.PrimaryScore, "score is 0 before 100ms tick interval elapses");
        TestAssert.Equal(HudIcon.Health, hudBeforeTick.StatusIcon ?? HudIcon.Fist, "status icon set on zone enter");

        // 6. Bounded tick rate: 19 steps of 5 ms (95 ms) should NOT invoke on_objective_tick
        for (int i = 0; i < 19; i++)
        {
            obj.Step(null!, 5, 110 + i * 5);
        }
        TestAssert.Equal(0, obj.GetHudState(player1, 200).PrimaryScore, "no tick before 100ms threshold");

        // The 20th step of 5 ms reaches 100 ms -> invokes on_objective_tick once!
        obj.Step(null!, 5, 205);
        var hudAfterTick = obj.GetHudState(player1, 205);
        TestAssert.Equal(1, hudAfterTick.PrimaryScore, "team 1 score incremented at 100ms tick boundary");
        TestAssert.Equal(0, hudAfterTick.SecondaryScore, "team 2 score is 0");

        // 7. on_damage with reusable ObjectiveDamageContext:
        var friendlyAttacker = new MapPlayer { PlayerId = 2, Team = 1 };
        int damage = 100;
        var push = new Vector3(50, 0, 0);
        bool allowDamage = obj.OnBeforeDamage(friendlyAttacker, player1, Gun.Rifle, ref damage, ref push, 1000);
        TestAssert.False(allowDamage, "friendly fire canceled");

        var enemyAttacker = new MapPlayer { PlayerId = 3, Team = 2 };
        damage = 100;
        push = new Vector3(50, 0, 0);
        allowDamage = obj.OnBeforeDamage(enemyAttacker, player1, Gun.Rifle, ref damage, ref push, 1000);
        TestAssert.True(allowDamage, "enemy fire allowed");
        TestAssert.Equal(50, damage, "damage halved inside hill zone");
        TestAssert.Equal(Vector3.Zero, push, "knockback push neutralized inside hill zone");

        // Exiting the hill zone triggers on_zone_event(entered: false) and clears status icon
        player1.Entity.O = new Vector3(500, 500, 500);
        obj.StepPlayer(player1, 1100);
        TestAssert.False(obj.Context.is_player_in_zone(player1.PlayerId, 0), "player1 exited hill zone");
        TestAssert.Equal(0, obj.Context.get_zone_team_count(0, 1), "team 1 count in zone is 0");
        TestAssert.True(obj.GetHudState(player1, 1100).StatusIcon == null, "status icon cleared on zone exit");

        // Enemy fire outside the hill zone is unmodified
        damage = 80;
        push = new Vector3(20, 10, 0);
        allowDamage = obj.OnBeforeDamage(enemyAttacker, player1, Gun.Rifle, ref damage, ref push, 1200);
        TestAssert.True(allowDamage, "enemy fire allowed outside zone");
        TestAssert.Equal(80, damage, "damage unmodified outside hill zone");
        TestAssert.Equal(new Vector3(20, 10, 0), push, "push unmodified outside hill zone");

        // 8. on_player_died also clears zone occupancy if player dies inside zone
        player1.Entity.O = new Vector3(50, 60, 70);
        obj.StepPlayer(player1, 1300);
        TestAssert.True(obj.Context.is_player_in_zone(player1.PlayerId, 0), "player1 back in zone");
        player1.Entity.State = PhysEnt.CsDead;
        obj.OnPlayerDied(player1, enemyAttacker, Gun.Rifle, 1400);
        TestAssert.False(obj.Context.is_player_in_zone(player1.PlayerId, 0), "dead player removed from zone");

        player1.Free();
        friendlyAttacker.Free();
        enemyAttacker.Free();
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

        var defender = new MapPlayer { PlayerId = 1, Objective = obj, Team = 1, Mode = mode };
        defender.Respawn(new Vector3(100, 100, 0));
        defender.Combat.Armour = 0;
        int initialHealth = defender.Combat.Health;

        var friendly = new MapPlayer { PlayerId = 2, Objective = obj, Team = 1, Mode = mode };
        var enemy = new MapPlayer { PlayerId = 3, Objective = obj, Team = 2, Mode = mode };

        // 1. Friendly fire is short-circuited natively by MapPlayer.TakeDamage when FriendlyFireScale == 0
        defender.TakeDamage(100, attacker: friendly, gun: Gun.Rifle, pushVel: new Vector3(10, 0, 0), nowMillis: 1000);
        TestAssert.Equal(initialHealth, defender.Combat.Health, "friendly fire canceled");
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

        defender.Free();
        friendly.Free();
        enemy.Free();
        root.Free();
    }

    [Test]
    public void ObjectiveContext_GetPlayerBit_StrictRangeValidation()
    {
        // Valid 0-63 range
        TestAssert.Equal(1UL, ObjectiveContext.GetPlayerBit(0), "bit 0");
        TestAssert.Equal(2UL, ObjectiveContext.GetPlayerBit(1), "bit 1");
        TestAssert.Equal(1UL << 63, ObjectiveContext.GetPlayerBit(63), "bit 63");

        // Negative IDs throw ArgumentOutOfRangeException
        bool threwNegative = false;
        try
        {
            ObjectiveContext.GetPlayerBit(-1);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            threwNegative = true;
        }
        TestAssert.True(threwNegative, "negative player ID throws ArgumentOutOfRangeException");

        // IDs >= 64 throw ArgumentOutOfRangeException (no silent modulo folding)
        bool threw64 = false;
        try
        {
            ObjectiveContext.GetPlayerBit(64);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            threw64 = true;
        }
        TestAssert.True(threw64, "player ID 64 throws ArgumentOutOfRangeException");

        bool threw65 = false;
        try
        {
            ObjectiveContext.GetPlayerBit(65);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            threw65 = true;
        }
        TestAssert.True(threw65, "player ID 65 throws ArgumentOutOfRangeException");

        bool threw100 = false;
        try
        {
            ObjectiveContext.GetPlayerBit(100);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            threw100 = true;
        }
        TestAssert.True(threw100, "player ID 100 throws ArgumentOutOfRangeException");
    }

    [Test]
    public void ObjectiveContext_ZoneOccupancy_ArbitraryPlayerIds_NoModuloCollisions()
    {
        var res = GD.Load<ScriptedObjectiveResource>("res://test/fixtures/koth.tres");
        var mode = GameModeCatalog.Get("teamplay");
        var obj = (ScriptedObjective)res!.CreateInstance(mode);

        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.Base, Position = new Vector3(0, 0, 0) }
        };
        var root = new Node3D();
        obj.SetupWorld(null!, entities, root);

        // Player 0 and Player 64 differ by exactly 64 (would collide with 64-bit mask folding)
        var player0 = new MapPlayer { PlayerId = 0, Team = 1 };
        var player64 = new MapPlayer { PlayerId = 64, Team = 1 };
        var player1000 = new MapPlayer { PlayerId = 1000, Team = 2 };

        player0.Entity.O = new Vector3(0, 0, 0);
        player64.Entity.O = new Vector3(500, 500, 500); // Outside
        player1000.Entity.O = new Vector3(500, 500, 500); // Outside

        obj.StepPlayer(player0, 100);
        obj.StepPlayer(player64, 100);
        obj.StepPlayer(player1000, 100);

        TestAssert.True(obj.Context.is_player_in_zone(0, 0), "player 0 is in zone");
        TestAssert.False(obj.Context.is_player_in_zone(64, 0), "player 64 is NOT in zone (no collision with 0)");
        TestAssert.False(obj.Context.is_player_in_zone(1000, 0), "player 1000 is NOT in zone");
        TestAssert.Equal(1, obj.Context.get_zone_player_count(0), "zone player count is 1");
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 1), "team 1 count is 1");

        // Move player 64 into the zone as well
        player64.Entity.O = new Vector3(10, 10, 0);
        obj.StepPlayer(player64, 105);

        TestAssert.True(obj.Context.is_player_in_zone(0, 0), "player 0 remains in zone");
        TestAssert.True(obj.Context.is_player_in_zone(64, 0), "player 64 is in zone");
        TestAssert.Equal(2, obj.Context.get_zone_player_count(0), "zone player count is 2");
        TestAssert.Equal(2, obj.Context.get_zone_team_count(0, 1), "team 1 count is 2");

        // Move player 0 out; player 64 must remain inside
        player0.Entity.O = new Vector3(500, 500, 500);
        obj.StepPlayer(player0, 110);

        TestAssert.False(obj.Context.is_player_in_zone(0, 0), "player 0 exited zone");
        TestAssert.True(obj.Context.is_player_in_zone(64, 0), "player 64 remains in zone");
        TestAssert.Equal(1, obj.Context.get_zone_player_count(0), "zone player count is 1");
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 1), "team 1 count is 1");

        // Move high-ID player 1000 into the zone
        player1000.Entity.O = new Vector3(0, 0, 0);
        obj.StepPlayer(player1000, 115);

        TestAssert.True(obj.Context.is_player_in_zone(1000, 0), "arbitrary player ID 1000 is in zone");
        TestAssert.Equal(2, obj.Context.get_zone_player_count(0), "zone player count is 2");
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 1), "team 1 count is 1");
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 2), "team 2 count is 1");

        player0.Free();
        player64.Free();
        player1000.Free();
        root.Free();
    }

    [Test]
    public void ObjectiveContext_ZoneTeamCounts_UpdateOnTeamChange()
    {
        var res = GD.Load<ScriptedObjectiveResource>("res://test/fixtures/koth.tres");
        var mode = GameModeCatalog.Get("teamplay");
        var obj = (ScriptedObjective)res!.CreateInstance(mode);

        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.Base, Position = new Vector3(0, 0, 0) }
        };
        var root = new Node3D();
        obj.SetupWorld(null!, entities, root);

        var player = new MapPlayer { PlayerId = 5, Team = 1, Objective = obj };
        player.Entity.O = new Vector3(0, 0, 0);

        // Step player inside the zone
        obj.StepPlayer(player, 100);
        TestAssert.True(obj.Context.is_player_in_zone(5, 0), "player in zone");
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 1), "team 1 count is 1");
        TestAssert.Equal(0, obj.Context.get_zone_team_count(0, 2), "team 2 count is 0");

        // 1. Change team to Team 2 via player.Team property while remaining in the zone
        player.Team = 2;
        TestAssert.Equal(0, obj.Context.get_zone_team_count(0, 1), "team 1 count immediately 0 after team change");
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 2), "team 2 count immediately 1 after team change");

        // Step player again - should maintain accurate team counts
        obj.StepPlayer(player, 105);
        TestAssert.Equal(0, obj.Context.get_zone_team_count(0, 1), "team 1 count stays 0 after step");
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 2), "team 2 count stays 1 after step");

        // 2. Change team via ObjectiveContext.set_player_team
        obj.Context.set_player_team(player.PlayerId, 1);
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 1), "team 1 count is 1 after set_player_team");
        TestAssert.Equal(0, obj.Context.get_zone_team_count(0, 2), "team 2 count is 0 after set_player_team");

        // 3. Player exits zone: team count drops to 0 without going negative
        player.Entity.O = new Vector3(500, 500, 500);
        obj.StepPlayer(player, 110);
        TestAssert.False(obj.Context.is_player_in_zone(5, 0), "player exited zone");
        TestAssert.Equal(0, obj.Context.get_zone_team_count(0, 1), "team 1 count is 0 on exit");
        TestAssert.Equal(0, obj.Context.get_zone_team_count(0, 2), "team 2 count is 0 on exit");

        player.Free();
        root.Free();
    }

    [Test]
    public void ObjectiveContext_PlayerRemoval_CleansUpZonesAndStaleTeamCounts()
    {
        var res = GD.Load<ScriptedObjectiveResource>("res://test/fixtures/koth.tres");
        var mode = GameModeCatalog.Get("teamplay");
        var obj = (ScriptedObjective)res!.CreateInstance(mode);

        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.Base, Position = new Vector3(0, 0, 0) }
        };
        var root = new Node3D();
        obj.SetupWorld(null!, entities, root);

        var player1 = new MapPlayer { PlayerId = 10, Team = 1, Objective = obj };
        var player2 = new MapPlayer { PlayerId = 20, Team = 1, Objective = obj };

        player1.Entity.O = new Vector3(0, 0, 0);
        player2.Entity.O = new Vector3(0, 0, 0);

        obj.StepPlayer(player1, 100);
        obj.StepPlayer(player2, 100);

        TestAssert.Equal(2, obj.Context.get_zone_player_count(0), "2 players in zone");
        TestAssert.Equal(2, obj.Context.get_zone_team_count(0, 1), "team 1 count is 2");

        // Player 1 removed via objective OnPlayerRemoved
        obj.OnPlayerRemoved(player1);

        TestAssert.False(obj.Context.is_player_in_zone(10, 0), "player 1 removed from zone");
        TestAssert.True(obj.Context.is_player_in_zone(20, 0), "player 2 remains in zone");
        TestAssert.Equal(1, obj.Context.get_zone_player_count(0), "zone player count is 1");
        TestAssert.Equal(1, obj.Context.get_zone_team_count(0, 1), "team 1 count is 1 (not stale)");
        TestAssert.Equal(0, obj.Context.get_player_team(10), "player 1 snapshot removed");

        // Player 2 removed via context remove_player
        obj.Context.remove_player(20);

        TestAssert.False(obj.Context.is_player_in_zone(20, 0), "player 2 removed from zone");
        TestAssert.Equal(0, obj.Context.get_zone_player_count(0), "zone player count is 0");
        TestAssert.Equal(0, obj.Context.get_zone_team_count(0, 1), "team 1 count is 0 (not stale)");

        player1.Free();
        player2.Free();
        root.Free();
    }

    [Test]
    public void ScriptedObjective_TickInterval_ClampingAndValidation()
    {
        var res = new ScriptedObjectiveResource();
        TestAssert.Equal(100, res.TickIntervalMillis, "default 100 ms");
        TestAssert.Equal(5, ScriptedObjectiveResource.MinTickIntervalMillis, "min tick interval is 5 ms");
        TestAssert.Equal(5, ScriptedObjective.MinTickIntervalMillis, "ScriptedObjective.MinTickIntervalMillis matches resource");

        // Positive values below 5 are clamped to 5
        res.TickIntervalMillis = 1;
        TestAssert.Equal(5, res.TickIntervalMillis, "1 ms clamped to 5 ms");
        res.TickIntervalMillis = 4;
        TestAssert.Equal(5, res.TickIntervalMillis, "4 ms clamped to 5 ms");

        // Positive values >= 5 remain untouched
        res.TickIntervalMillis = 5;
        TestAssert.Equal(5, res.TickIntervalMillis, "5 ms preserved");
        res.TickIntervalMillis = 50;
        TestAssert.Equal(50, res.TickIntervalMillis, "50 ms preserved");

        // 0 or negative values disable ticking and are preserved
        res.TickIntervalMillis = 0;
        TestAssert.Equal(0, res.TickIntervalMillis, "0 ms preserved (disabled)");
        res.TickIntervalMillis = -1;
        TestAssert.Equal(-1, res.TickIntervalMillis, "-1 ms preserved (disabled)");
    }

    [Test]
    public void ScriptedObjective_Step_ClampsInterval_PreventsMultipleTicksPerPhysicsStep()
    {
        var res = GD.Load<ScriptedObjectiveResource>("res://test/fixtures/koth.tres");
        TestAssert.True(res != null, "loaded koth.tres");

        // Set sub-physics-step interval (1 ms), clamped to 5 ms
        res!.TickIntervalMillis = 1;
        TestAssert.Equal(ScriptedObjectiveResource.MinTickIntervalMillis, res.TickIntervalMillis, "clamped to MinTickIntervalMillis");

        var mode = GameModeCatalog.Get("teamplay");
        var obj = (ScriptedObjective)res.CreateInstance(mode);

        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.Base, Position = new Vector3(0, 0, 0) }
        };
        var root = new Node3D();
        obj.SetupWorld(null!, entities, root);

        var player = new MapPlayer { PlayerId = 1, Team = 1, Objective = obj };
        player.Entity.O = new Vector3(0, 0, 0);
        player.Entity.State = PhysEnt.CsAlive;
        obj.StepPlayer(player, 0);

        // One 5 ms physics step
        obj.Step(null!, 5, 5);

        // Score must increment by exactly 1 tick, NOT 5 ticks
        var hud = obj.GetHudState(player, 5);
        TestAssert.Equal(1, hud.PrimaryScore, "score incremented once in 5 ms step, not 5 times");

        // Second 5 ms physics step
        obj.Step(null!, 5, 10);
        hud = obj.GetHudState(player, 10);
        TestAssert.Equal(2, hud.PrimaryScore, "score incremented once in second 5 ms step");

        // Reset and test disabled interval (0 ms)
        res.TickIntervalMillis = 0;
        obj.Step(null!, 5, 15);
        hud = obj.GetHudState(player, 15);
        TestAssert.Equal(2, hud.PrimaryScore, "score does not advance when TickIntervalMillis is 0");

        player.Free();
        root.Free();
    }
}
