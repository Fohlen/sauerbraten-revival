using System;
using System.Collections.Generic;
using System.Numerics;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Runtime;
using SauerbratenRevival.World.Weapons;
using Vector3 = Godot.Vector3;

namespace SauerbratenRevival.World.Game.Objectives;

public enum ObjectiveZoneShape
{
    Sphere = 0,
    Cylinder = 1,
    Box = 2,
}

/// <summary>
/// Pre-allocated context passed to scripted objectives.
/// Owns native spatial trigger zones, O(1) player state snapshots,
/// push-based HUD state caches, and spawn overrides so the 200 Hz simulation
/// and render loop never poll GDScript or allocate Dictionaries.
/// </summary>
public partial class ObjectiveContext : RefCounted
{
    internal struct ZoneEntry
    {
        public int Id;
        public ObjectiveZoneShape Shape;
        public Vector3 Center;
        public float RadiusSq;
        public float HalfHeight;
        public Vector3 HalfExtents;
        public ulong OccupiedMask;
        public int Team1Count;
        public int Team2Count;

        public readonly bool Contains(Vector3 pos)
        {
            Vector3 d = pos - Center;
            return Shape switch
            {
                ObjectiveZoneShape.Sphere => d.LengthSquared() <= RadiusSq,
                ObjectiveZoneShape.Cylinder => (d.X * d.X + d.Y * d.Y) <= RadiusSq && Math.Abs(d.Z) <= HalfHeight,
                ObjectiveZoneShape.Box => Math.Abs(d.X) <= HalfExtents.X && Math.Abs(d.Y) <= HalfExtents.Y && Math.Abs(d.Z) <= HalfExtents.Z,
                _ => false,
            };
        }
    }

    internal struct PlayerSnapshot
    {
        public int Team;
        public Vector3 FeetPos;
        public bool Alive;
    }

    internal struct HudStatusEntry
    {
        public int Icon;
        public int Counter;
    }

    private ZoneEntry[] _zones = new ZoneEntry[8];
    private int _zoneCount;

    private readonly Dictionary<int, PlayerSnapshot> _players = new();
    private readonly int[] _teamScores = new int[3];
    private readonly HudStatusEntry[] _teamStatus =
    {
        new() { Icon = -1, Counter = -1 },
        new() { Icon = -1, Counter = -1 },
        new() { Icon = -1, Counter = -1 },
    };
    private readonly Dictionary<int, int> _playerScores = new();
    private readonly Dictionary<int, HudStatusEntry> _playerStatus = new();

    private (Vector3 FeetPos, float Yaw)? _defaultSpawnOverride;
    private readonly Dictionary<int, (Vector3 FeetPos, float Yaw)> _playerSpawnOverrides = new();

    // --- Spatial Trigger Zone API (GDScript & C#) ---

    public void clear_zones()
    {
        _zoneCount = 0;
    }

    public void add_sphere_zone(int zoneId, Vector3 center, float radius)
    {
        AddOrReplaceZone(new ZoneEntry
        {
            Id = zoneId,
            Shape = ObjectiveZoneShape.Sphere,
            Center = center,
            RadiusSq = radius * radius,
        });
    }

    public void add_cylinder_zone(int zoneId, Vector3 center, float radius, float halfHeight)
    {
        AddOrReplaceZone(new ZoneEntry
        {
            Id = zoneId,
            Shape = ObjectiveZoneShape.Cylinder,
            Center = center,
            RadiusSq = radius * radius,
            HalfHeight = halfHeight,
        });
    }

    public void add_box_zone(int zoneId, Vector3 center, Vector3 halfExtents)
    {
        AddOrReplaceZone(new ZoneEntry
        {
            Id = zoneId,
            Shape = ObjectiveZoneShape.Box,
            Center = center,
            HalfExtents = halfExtents,
        });
    }

    private void AddOrReplaceZone(ZoneEntry entry)
    {
        for (int i = 0; i < _zoneCount; i++)
        {
            if (_zones[i].Id == entry.Id)
            {
                _zones[i] = entry;
                return;
            }
        }
        if (_zoneCount == _zones.Length)
        {
            Array.Resize(ref _zones, _zones.Length * 2);
        }
        _zones[_zoneCount++] = entry;
    }

    public bool is_player_in_zone(int playerId, int zoneId)
    {
        ulong bit = GetPlayerBit(playerId);
        for (int i = 0; i < _zoneCount; i++)
        {
            if (_zones[i].Id == zoneId)
                return (_zones[i].OccupiedMask & bit) != 0;
        }
        return false;
    }

    public int get_zone_team_count(int zoneId, int team)
    {
        for (int i = 0; i < _zoneCount; i++)
        {
            if (_zones[i].Id == zoneId)
            {
                return team switch
                {
                    1 => _zones[i].Team1Count,
                    2 => _zones[i].Team2Count,
                    _ => 0,
                };
            }
        }
        return 0;
    }

    public int get_zone_player_count(int zoneId)
    {
        for (int i = 0; i < _zoneCount; i++)
        {
            if (_zones[i].Id == zoneId)
                return BitOperations.PopCount(_zones[i].OccupiedMask);
        }
        return 0;
    }

    // --- Player Snapshot API ---

    public int get_player_team(int playerId) =>
        _players.TryGetValue(playerId, out var snap) ? snap.Team : 0;

    public Vector3 get_player_pos(int playerId) =>
        _players.TryGetValue(playerId, out var snap) ? snap.FeetPos : Vector3.Zero;

    public bool is_player_alive(int playerId) =>
        _players.TryGetValue(playerId, out var snap) && snap.Alive;

    // --- Push-Based Score & HUD Status API ---

    public void set_team_score(int team, int score)
    {
        if (team is >= 1 and <= 2)
            _teamScores[team] = score;
    }

    public int get_team_score(int team) =>
        team is >= 1 and <= 2 ? _teamScores[team] : 0;

    public void set_player_score(int playerId, int score) =>
        _playerScores[playerId] = score;

    public int get_player_score(int playerId) =>
        _playerScores.TryGetValue(playerId, out int s) ? s : 0;

    public void set_team_status(int team, int statusIcon, int statusCounter = -1)
    {
        if (team is >= 1 and <= 2)
            _teamStatus[team] = new HudStatusEntry { Icon = statusIcon, Counter = statusCounter };
    }

    public void clear_team_status(int team)
    {
        if (team is >= 1 and <= 2)
            _teamStatus[team] = new HudStatusEntry { Icon = -1, Counter = -1 };
    }

    public void set_player_status(int playerId, int statusIcon, int statusCounter = -1) =>
        _playerStatus[playerId] = new HudStatusEntry { Icon = statusIcon, Counter = statusCounter };

    public void clear_player_status(int playerId) =>
        _playerStatus.Remove(playerId);

    // --- Push-Based Spawn Override API ---

    public void set_default_spawn_override(Vector3 feetPos, float yaw) =>
        _defaultSpawnOverride = (feetPos, yaw);

    public void clear_default_spawn_override() =>
        _defaultSpawnOverride = null;

    public void set_player_spawn_override(int playerId, Vector3 feetPos, float yaw) =>
        _playerSpawnOverrides[playerId] = (feetPos, yaw);

    public void clear_player_spawn_override(int playerId) =>
        _playerSpawnOverrides.Remove(playerId);

    // --- Internal Native Hot-Path Evaluators (C#) ---

    internal static ulong GetPlayerBit(int playerId) =>
        1UL << (playerId & 63);

    internal void SyncPlayerSnapshot(MapPlayer player)
    {
        _players[player.PlayerId] = new PlayerSnapshot
        {
            Team = player.Team,
            FeetPos = player.Entity.FeetPos(),
            Alive = player.IsAlive,
        };
    }

    internal void EvaluatePlayerZones(
        MapPlayer player,
        int nowMillis,
        ScriptedObjective owner)
    {
        SyncPlayerSnapshot(player);
        if (_zoneCount == 0) return;

        ulong bit = GetPlayerBit(player.PlayerId);
        bool alive = player.IsAlive;
        Vector3 feet = player.Entity.FeetPos();
        int team = player.Team;

        for (int i = 0; i < _zoneCount; i++)
        {
            ref ZoneEntry z = ref _zones[i];
            bool wasIn = (z.OccupiedMask & bit) != 0;
            bool nowIn = alive && z.Contains(feet);

            if (wasIn != nowIn)
            {
                if (nowIn)
                {
                    z.OccupiedMask |= bit;
                    if (team == 1) z.Team1Count++;
                    else if (team == 2) z.Team2Count++;
                }
                else
                {
                    z.OccupiedMask &= ~bit;
                    if (team == 1 && z.Team1Count > 0) z.Team1Count--;
                    else if (team == 2 && z.Team2Count > 0) z.Team2Count--;
                }

                owner.NotifyZoneTransition(z.Id, player.PlayerId, nowIn, nowMillis);
            }
        }
    }

    internal void EvaluateWorldZones(
        IReadOnlyList<MapPlayer> players,
        int nowMillis,
        ScriptedObjective owner)
    {
        for (int i = 0; i < players.Count; i++)
        {
            EvaluatePlayerZones(players[i], nowMillis, owner);
        }
    }

    internal (Vector3 FeetPos, float Yaw)? ResolveSpawn(int playerId)
    {
        if (_playerSpawnOverrides.TryGetValue(playerId, out var specific))
            return specific;
        return _defaultSpawnOverride;
    }

    internal ObjectiveHudState ResolveHudState(MapPlayer localPlayer, bool teamPlay)
    {
        int myTeam = localPlayer.Team;
        int primaryScore;
        int secondaryScore;

        if (teamPlay)
        {
            int enemyTeam = myTeam == 1 ? 2 : 1;
            primaryScore = get_team_score(myTeam);
            secondaryScore = get_team_score(enemyTeam);
        }
        else
        {
            primaryScore = get_player_score(localPlayer.PlayerId);
            secondaryScore = int.MinValue;
        }

        HudIcon? icon = null;
        int counter = -1;

        if (_playerStatus.TryGetValue(localPlayer.PlayerId, out var pStatus) && pStatus.Icon >= 0)
        {
            icon = (HudIcon)pStatus.Icon;
            counter = pStatus.Counter;
        }
        else if (myTeam is >= 1 and <= 2 && _teamStatus[myTeam].Icon >= 0)
        {
            icon = (HudIcon)_teamStatus[myTeam].Icon;
            counter = _teamStatus[myTeam].Counter;
        }

        return new ObjectiveHudState(
            PrimaryScore: primaryScore,
            SecondaryScore: secondaryScore,
            PrimaryIsFriendly: true,
            StatusIcon: icon,
            StatusCounter: counter);
    }
}

/// <summary>
/// Pre-allocated synchronous combat context reused across all <c>on_damage</c> calls
/// to avoid per-hit Dictionary allocations.
/// </summary>
public partial class ObjectiveDamageContext : RefCounted
{
    private readonly ObjectiveContext _context;

    public ObjectiveDamageContext()
    {
        _context = null!;
    }

    public ObjectiveDamageContext(ObjectiveContext context)
    {
        _context = context;
    }

    public int attacker_id { get; set; } = -1;
    public int victim_id { get; set; } = -1;
    public int attacker_team { get; set; }
    public int victim_team { get; set; }
    public int gun { get; set; }
    public int damage { get; set; }
    public Vector3 push { get; set; }
    public bool cancelled { get; set; }
    public int now_millis { get; set; }

    public bool is_victim_in_zone(int zoneId) =>
        _context != null && _context.is_player_in_zone(victim_id, zoneId);

    public bool is_attacker_in_zone(int zoneId) =>
        _context != null && attacker_id >= 0 && _context.is_player_in_zone(attacker_id, zoneId);
}

[Tool]
[GlobalClass]
public partial class ScriptedObjectiveResource : ModeObjectiveResource
{
    /// <summary>GDScript or C# Script implementing objective lifecycle and event methods.</summary>
    [Export] public Script? ObjectiveScript { get; set; }

    /// <summary>
    /// Period (in milliseconds) for the bounded <c>on_objective_tick</c> callback.
    /// Defaults to 100 ms (10 Hz). Set to 0 or negative to disable periodic script ticking.
    /// </summary>
    [Export] public int TickIntervalMillis { get; set; } = 100;

    public ScriptedObjectiveResource()
    {
        Id = "custom";
        Name = "custom";
        DefaultTeamPlay = false;
        DefaultMapPool = MapListId.Dm;
        DefaultSpawnArmour = 25;
    }

    public override IModeObjective CreateInstance(GameModeInfo mode) => new ScriptedObjective(this, mode);
}

public class ScriptedObjective : IModeObjective
{
    private static readonly StringName MethodClaimsEntity = "claims_entity";
    private static readonly StringName MethodSetupWorld = "setup_world";
    private static readonly StringName MethodOnZoneEvent = "on_zone_event";
    private static readonly StringName MethodOnObjectiveTick = "on_objective_tick";
    private static readonly StringName MethodUpdateVisual = "update_visual";
    private static readonly StringName MethodOnPlayerSpawn = "on_player_spawn";
    private static readonly StringName MethodOnDamage = "on_damage";
    private static readonly StringName MethodOnPlayerDied = "on_player_died";

    public ModeObjectiveResource Definition => Resource;
    public ScriptedObjectiveResource Resource { get; }
    public GameModeInfo Mode { get; }
    public ObjectiveContext Context => _context;

    private readonly Variant _scriptInstanceHolder;
    private readonly GodotObject? _scriptInstance;
    private readonly ObjectiveContext _context;
    private readonly ObjectiveDamageContext _damageContext;

    private readonly bool _hasClaimsEntity;
    private readonly bool _hasSetupWorld;
    private readonly bool _hasOnZoneEvent;
    private readonly bool _hasOnObjectiveTick;
    private readonly bool _hasUpdateVisual;
    private readonly bool _hasOnPlayerSpawn;
    private readonly bool _hasOnDamage;
    private readonly bool _hasOnPlayerDied;

    private int _tickAccum;

    public ScriptedObjective(ScriptedObjectiveResource resource, GameModeInfo mode)
    {
        Resource = resource;
        Mode = mode;
        _context = new ObjectiveContext();
        _damageContext = new ObjectiveDamageContext(_context);

        if (resource.ObjectiveScript != null)
        {
            _scriptInstanceHolder = resource.ObjectiveScript.Call("new");
            _scriptInstance = _scriptInstanceHolder.AsGodotObject();
            if (_scriptInstance != null)
            {
                _hasClaimsEntity = _scriptInstance.HasMethod(MethodClaimsEntity);
                _hasSetupWorld = _scriptInstance.HasMethod(MethodSetupWorld);
                _hasOnZoneEvent = _scriptInstance.HasMethod(MethodOnZoneEvent);
                _hasOnObjectiveTick = _scriptInstance.HasMethod(MethodOnObjectiveTick);
                _hasUpdateVisual = _scriptInstance.HasMethod(MethodUpdateVisual);
                _hasOnPlayerSpawn = _scriptInstance.HasMethod(MethodOnPlayerSpawn);
                _hasOnDamage = _scriptInstance.HasMethod(MethodOnDamage);
                _hasOnPlayerDied = _scriptInstance.HasMethod(MethodOnPlayerDied);
            }
        }
    }

    public bool ClaimsEntity(MapEntity e)
    {
        if (_hasClaimsEntity && _scriptInstance != null)
        {
            return _scriptInstance.Call(MethodClaimsEntity, e).AsBool();
        }
        return false;
    }

    public void SetupWorld(IGameWorld world, IReadOnlyList<MapEntity> entities, Node3D objectiveRoot)
    {
        _tickAccum = 0;
        if (_hasSetupWorld && _scriptInstance != null)
        {
            var entityArray = new Godot.Collections.Array();
            for (int i = 0; i < entities.Count; i++)
            {
                entityArray.Add(entities[i]);
            }
            _scriptInstance.Call(MethodSetupWorld, _context, entityArray, objectiveRoot);
        }
    }

    internal void NotifyZoneTransition(int zoneId, int playerId, bool entered, int nowMillis)
    {
        if (_hasOnZoneEvent && _scriptInstance != null)
        {
            _scriptInstance.Call(MethodOnZoneEvent, _context, zoneId, playerId, entered, nowMillis);
        }
    }

    public void Step(IGameWorld world, int stepMillis, int nowMillis)
    {
        if (world != null)
        {
            _context.EvaluateWorldZones(world.Players, nowMillis, this);
        }

        int interval = Resource.TickIntervalMillis;
        if (interval > 0 && _hasOnObjectiveTick && _scriptInstance != null)
        {
            _tickAccum += stepMillis;
            while (_tickAccum >= interval)
            {
                _tickAccum -= interval;
                _scriptInstance.Call(MethodOnObjectiveTick, _context, interval, nowMillis);
            }
        }
    }

    public void StepPlayer(MapPlayer player, int nowMillis)
    {
        // When a player runs standalone (outside IGameWorld), evaluate its zone transitions in C#.
        // Under IGameWorld, Step() evaluates all players centrally.
        if (player.GetParent() is not IGameWorld)
        {
            _context.EvaluatePlayerZones(player, nowMillis, this);
        }
    }

    public void UpdateVisual(IGameWorld world, int lastMillis)
    {
        if (_hasUpdateVisual && _scriptInstance != null)
        {
            _scriptInstance.Call(MethodUpdateVisual, _context, lastMillis);
        }
    }

    public (Vector3 FeetPos, float Yaw)? ResolveSpawn(MapPlayer player) =>
        _context.ResolveSpawn(player.PlayerId);

    public void OnPlayerSpawn(MapPlayer player)
    {
        _context.EvaluatePlayerZones(player, player.LastMillis, this);
        if (_hasOnPlayerSpawn && _scriptInstance != null)
        {
            _scriptInstance.Call(MethodOnPlayerSpawn, _context, player.PlayerId);
        }
    }

    public bool OnBeforeDamage(
        MapPlayer? attacker,
        MapPlayer victim,
        Gun gun,
        ref int damage,
        ref Vector3 pushVel,
        int nowMillis)
    {
        if (!_hasOnDamage || _scriptInstance == null)
            return true;

        if (attacker != null)
            _context.EvaluatePlayerZones(attacker, nowMillis, this);
        _context.EvaluatePlayerZones(victim, nowMillis, this);

        _damageContext.attacker_id = attacker?.PlayerId ?? -1;
        _damageContext.victim_id = victim.PlayerId;
        _damageContext.attacker_team = attacker?.Team ?? 0;
        _damageContext.victim_team = victim.Team;
        _damageContext.gun = (int)gun;
        _damageContext.damage = damage;
        _damageContext.push = pushVel;
        _damageContext.cancelled = false;
        _damageContext.now_millis = nowMillis;

        _scriptInstance.Call(MethodOnDamage, _context, _damageContext);

        if (_damageContext.cancelled)
            return false;

        damage = _damageContext.damage;
        pushVel = _damageContext.push;
        return true;
    }

    public void OnPlayerDied(MapPlayer victim, MapPlayer? killer, Gun gun, int nowMillis)
    {
        _context.EvaluatePlayerZones(victim, nowMillis, this);
        if (_hasOnPlayerDied && _scriptInstance != null)
        {
            _scriptInstance.Call(MethodOnPlayerDied, _context, victim.PlayerId, killer?.PlayerId ?? -1, (int)gun, nowMillis);
        }
    }

    public ObjectiveHudState GetHudState(MapPlayer localPlayer, int nowMillis) =>
        _context.ResolveHudState(localPlayer, Mode.TeamPlay);
}
