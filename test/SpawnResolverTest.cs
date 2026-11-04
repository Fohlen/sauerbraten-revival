using System;
using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Tests player spawn group selection, CTF base spawning, ratespawn proximity weighting,
/// and respawn placement fidelity matching Sauerbraten r6889.
/// </summary>
public class SpawnResolverTest : GoTestBase
{
    public SpawnResolverTest(Node testScene) : base(testScene) { }

    [Test]
    public void GameModeCatalog_GetSpawnGroup_MatchesEngineCmode()
    {
        var ctf = GameModeCatalog.Get("ctf");
        var protect = GameModeCatalog.Get("protect");
        var hold = GameModeCatalog.Get("hold");
        var ffa = GameModeCatalog.Get("ffa");
        var tdm = GameModeCatalog.Get("teamplay");

        // CTF: Team 1 (good) -> 1, Team 2 (evil) -> 2
        TestAssert.Equal(1, GameModeCatalog.GetSpawnGroup(ctf, 1), "CTF team 1 spawn tag");
        TestAssert.Equal(2, GameModeCatalog.GetSpawnGroup(ctf, 2), "CTF team 2 spawn tag");
        TestAssert.Equal(0, GameModeCatalog.GetSpawnGroup(ctf, 0), "CTF neutral team spawn tag");

        // Protect: same as CTF
        TestAssert.Equal(1, GameModeCatalog.GetSpawnGroup(protect, 1), "Protect team 1 spawn tag");
        TestAssert.Equal(2, GameModeCatalog.GetSpawnGroup(protect, 2), "Protect team 2 spawn tag");

        // Hold: m_hold ? 0 : ctfteamflag(d->team) -> always 0
        TestAssert.Equal(0, GameModeCatalog.GetSpawnGroup(hold, 1), "Hold team 1 spawn tag is 0");
        TestAssert.Equal(0, GameModeCatalog.GetSpawnGroup(hold, 2), "Hold team 2 spawn tag is 0");

        // FFA / TDM: tag 0
        TestAssert.Equal(0, GameModeCatalog.GetSpawnGroup(ffa, 1), "FFA spawn tag is 0");
        TestAssert.Equal(0, GameModeCatalog.GetSpawnGroup(tdm, 1), "TDM spawn tag is 0");
        TestAssert.Equal(0, GameModeCatalog.GetSpawnGroup(null, 1), "null mode spawn tag is 0");
    }

    [Test]
    public void ProximityScoreAndHarmonicMean_MatchGoldenFormulas()
    {
        // Proximity score: lower=80, upper=400
        TestAssert.Equal(1.0f, SpawnResolver.ProximityScore(50f, 80f, 400f), "below lower");
        TestAssert.Equal(0.0f, SpawnResolver.ProximityScore(500f, 80f, 400f), "above upper");

        // Midpoint: (b*b)/(a*a + b*b) at halfway 240: a=160, b=-160 -> (-160)^2 / (160^2 + (-160)^2) = 0.5
        float mid = SpawnResolver.ProximityScore(240f, 80f, 400f);
        TestAssert.True(Mathf.Abs(mid - 0.5f) < 0.001f, "midpoint proximity score 0.5");

        // Harmonic mean: 2 * a * b / (a + b)
        float hm = SpawnResolver.HarmonicMean(2.0f, 6.0f);
        TestAssert.True(Mathf.Abs(hm - 3.0f) < 0.001f, "harmonic mean 2 and 6 is 3.0");
        TestAssert.Equal(0.0f, SpawnResolver.HarmonicMean(0f, 5f), "harmonic mean with 0");
    }

    [Test]
    public void CtfSpawning_SpawnsAtFriendlyBaseNeverEnemyBase()
    {
        // Create synthetic map entities:
        // Spawns 0-2: Team 1 (Good base, Z=100)
        // Spawns 3-5: Team 2 (Evil base, Z=200)
        // Spawn 6: Neutral (tag 0, Z=50)
        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.PlayerStart, Position = new Vector3(10, 10, 100), Attr1 = 0, Attr2 = 1 },
            new() { Type = EntityType.PlayerStart, Position = new Vector3(20, 10, 100), Attr1 = 90, Attr2 = 1 },
            new() { Type = EntityType.PlayerStart, Position = new Vector3(30, 10, 100), Attr1 = 180, Attr2 = 1 },

            new() { Type = EntityType.PlayerStart, Position = new Vector3(100, 100, 200), Attr1 = 180, Attr2 = 2 },
            new() { Type = EntityType.PlayerStart, Position = new Vector3(110, 100, 200), Attr1 = 270, Attr2 = 2 },
            new() { Type = EntityType.PlayerStart, Position = new Vector3(120, 100, 200), Attr1 = 0, Attr2 = 2 },

            new() { Type = EntityType.PlayerStart, Position = new Vector3(50, 50, 50), Attr1 = 45, Attr2 = 0 },
        };

        var ctfMode = GameModeCatalog.Get("ctf");
        var rng = new Random(1337);

        // Team 1 player must always spawn at Team 1 base (Z=100)
        for (int i = 0; i < 20; i++)
        {
            int tag = GameModeCatalog.GetSpawnGroup(ctfMode, team: 1);
            var (pos, _) = SpawnResolver.FindPlayerSpawn(entities, Vector3.Zero, tag: tag, rng: rng);
            TestAssert.Equal(100f, pos.Z, $"Team 1 spawn #{i} is at friendly base (Z=100), never enemy base");
            TestAssert.True(pos.X is 10f or 20f or 30f, "Spawn X is in team 1 positions");
        }

        // Team 2 player must always spawn at Team 2 base (Z=200)
        for (int i = 0; i < 20; i++)
        {
            int tag = GameModeCatalog.GetSpawnGroup(ctfMode, team: 2);
            var (pos, _) = SpawnResolver.FindPlayerSpawn(entities, Vector3.Zero, tag: tag, rng: rng);
            TestAssert.Equal(200f, pos.Z, $"Team 2 spawn #{i} is at friendly base (Z=200), never enemy base");
            TestAssert.True(pos.X is 100f or 110f or 120f, "Spawn X is in team 2 positions");
        }
    }

    [Test]
    public void FallbackWhenNoMatchingTagSpawnsExist()
    {
        // Map with only tag 0 spawns (DM map played in CTF)
        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.PlayerStart, Position = new Vector3(50, 50, 50), Attr1 = 90, Attr2 = 0 },
            new() { Type = EntityType.PlayerStart, Position = new Vector3(60, 60, 60), Attr1 = 180, Attr2 = 0 },
        };

        // Asking for tag 1 (good base) gracefully falls back to tag 0 spawns
        var (pos, yaw) = SpawnResolver.FindPlayerSpawn(entities, new Vector3(999, 999, 999), tag: 1);
        TestAssert.True(pos.X is 50f or 60f, "Falls back to tag 0 spawn rather than void center");

        // Completely empty entity list falls back to provided bounds center fallback
        var (posEmpty, _) = SpawnResolver.FindPlayerSpawn(new List<MapEntity>(), new Vector3(512, 512, 512), tag: 1);
        TestAssert.Equal(new Vector3(512, 512, 512), posEmpty, "Empty entity list falls back to bounds center");
    }

    [Test]
    public void RateSpawn_PenalizesSpawnNearLivingEnemy()
    {
        var spawnNearEnemy = new MapEntity { Type = EntityType.PlayerStart, Position = new Vector3(100, 100, 0), Attr1 = 0, Attr2 = 1 };
        var spawnFar = new MapEntity { Type = EntityType.PlayerStart, Position = new Vector3(500, 500, 0), Attr1 = 0, Attr2 = 1 };

        var player = new MapPlayer { Team = 1 };
        var enemy = new MapPlayer { Team = 2 };
        enemy.Entity.O = new Vector3(120, 100, MapPlayer.EyeHeight); // 20 units away from spawnNearEnemy
        enemy.Entity.State = PhysEnt.CsAlive;

        var players = new List<MapPlayer> { player, enemy };
        var ctfMode = GameModeCatalog.Get("ctf");

        float weightNear = SpawnResolver.RateSpawn(spawnNearEnemy, player, players, null, ctfMode, null);
        float weightFar = SpawnResolver.RateSpawn(spawnFar, player, players, null, ctfMode, null);

        TestAssert.True(weightFar > weightNear, $"Far spawn ({weightFar}) rated higher than enemy-adjacent spawn ({weightNear})");
        TestAssert.True(weightNear < 0.2f, "Spawn 20 units from enemy has severe proximity penalty");
        TestAssert.Equal(1.0f, weightFar, "Spawn 500+ units away has max 1.0 rating");

        player.Free();
        enemy.Free();
    }
}
