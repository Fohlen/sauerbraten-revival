using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game.Ctf;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Runtime;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game.Objectives;

public enum FlagPlacementMode
{
    MapTeamFlags = 0,      // Spawns team 1 & 2 flags at ET_FLAG entities (CTF, Protect)
    DynamicHoldSpawns = 1, // Uses ET_BASE entities as spawn points for neutral flags (Hold)
}

public enum FlagScoreRule
{
    RelayToFriendlyBase = 0, // Carry enemy flag to home base while own flag is home (CTF)
    TouchEnemyFlag = 1,      // Touch enemy flag to score immediately (Protect)
    HoldTimer = 2,           // Hold flag for HoldSeconds to score (Hold)
}

public enum FlagReturnRule
{
    TouchDropped = 0,   // Touch dropped friendly flag to return; auto-reset after ResetDelayMillis (CTF)
    DropInsideBase = 1, // Drop inside own base to return; timeout deducts 1 point (Protect)
    RandomRespawn = 2,  // Timeout respawns flag at a new hold spawn (Hold)
}

[Tool]
[GlobalClass]
public partial class FlagObjectiveResource : ModeObjectiveResource
{
    private FlagPlacementMode _placement = FlagPlacementMode.MapTeamFlags;
    [Export]
    public FlagPlacementMode Placement
    {
        get => _placement;
        set
        {
            _placement = value;
            if (value == FlagPlacementMode.DynamicHoldSpawns && ResetDelayMillis == 10000)
            {
                ResetDelayMillis = 30000;
            }
        }
    }
    [Export] public FlagScoreRule ScoreRule { get; set; } = FlagScoreRule.RelayToFriendlyBase;
    [Export] public FlagReturnRule ReturnRule { get; set; } = FlagReturnRule.TouchDropped;
    [Export] public int ResetDelayMillis { get; set; } = 10000;
    [Export] public int InvisDelayMillis { get; set; } = 20000;
    [Export] public int HoldSeconds { get; set; } = 20;
    [Export] public int HoldDeathPenaltySeconds { get; set; } = 5;

    public FlagObjectiveResource()
    {
        Id = "ctf";
        Name = "ctf";
        DefaultTeamPlay = true;
        DefaultMapPool = MapListId.Ctf;
        DefaultSpawnArmour = 50;
    }

    public override IModeObjective CreateInstance(GameModeInfo mode)
    {
        if (mode != null && mode.Objective == null)
        {
            mode.Objective = this;
        }
        return new FlagObjective(this, mode);
    }
}

public class FlagObjective : IModeObjective
{
    public ModeObjectiveResource Definition => Resource;
    public FlagObjectiveResource Resource { get; }
    public GameModeInfo Mode { get; }
    public FlagManager Flags { get; set; }

    public FlagObjective(FlagObjectiveResource resource, GameModeInfo mode, FlagManager? flags = null)
    {
        Resource = resource;
        Mode = mode ?? GameModeCatalog.Get("ctf");
        if (mode != null && mode.Objective == null)
        {
            mode.Objective = resource;
        }
        Flags = flags ?? new FlagManager { Mode = Mode, Resource = resource };
        Flags.Mode = Mode;
        Flags.Resource = resource;
    }

    public bool ClaimsEntity(MapEntity e)
    {
        if (e.Type == EntityType.Flag) return true;
        if (Resource.Placement == FlagPlacementMode.DynamicHoldSpawns && e.Type == EntityType.Base) return true;
        return false;
    }

    public void SetupWorld(IGameWorld world, IReadOnlyList<MapEntity> entities, Node3D objectiveRoot)
    {
        Flags.ResetFlags();
        if (Resource.Placement == FlagPlacementMode.DynamicHoldSpawns)
        {
            foreach (var e in entities)
            {
                if (e.Type == EntityType.Base)
                    Flags.AddHoldSpawn(e.Position);
            }
            Flags.SetupHoldSpawns();
        }
        else
        {
            foreach (var e in entities)
            {
                if (e.Type == EntityType.Flag)
                {
                    if (e.Attr2 < 1 || e.Attr2 > 2) continue;
                    int flagId = Flags.Flags.Count;
                    Flags.AddFlag(flagId, e.Position, e.Attr2, 0,
                        isProtect: Resource.ScoreRule == FlagScoreRule.TouchEnemyFlag,
                        spawnAngle: e.Attr1);
                }
            }
        }
    }

    public void Step(IGameWorld world, int stepMillis, int nowMillis)
    {
        Func<int, (int team, Vector3 pos)?>? getPlayerInfo = world != null ? pid =>
        {
            var players = world.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p.PlayerId == pid) return (p.Team, p.Entity.FeetPos());
            }
            return null;
        }
        : null;

        Flags.Update(Resource, Mode, nowMillis, getPlayerInfo);
    }

    public void StepPlayer(MapPlayer player, int nowMillis)
    {
        player.CheckFlags(nowMillis);
    }

    public void OnPlayerDied(MapPlayer victim, MapPlayer? killer, Gun gun, int nowMillis)
    {
        bool isTeammate = killer != null && killer.Team == victim.Team;
        Flags.OnPlayerDied(victim.PlayerId, killer?.PlayerId, isTeammate, victim.Entity.FeetPos(), Mode, nowMillis, victim.Entity.Yaw);
    }

    public ObjectiveHudState GetHudState(MapPlayer localPlayer, int nowMillis)
    {
        int myTeam = localPlayer.Team;
        int enemyTeam = myTeam == 1 ? 2 : 1;
        int myScore = Flags.GetScore(myTeam);
        int enemyScore = Flags.GetScore(enemyTeam);

        HudIcon? icon = null;
        int counter = -1;

        var carried = Flags.GetCarriedFlagState(localPlayer.PlayerId);
        if (carried != null)
        {
            bool isHold = Resource.Placement == FlagPlacementMode.DynamicHoldSpawns;
            icon = StatusBarRenderer.GetFlagIcon(isHold, carried.Team, localPlayer.Team);

            if (Resource.ScoreRule == FlagScoreRule.HoldTimer && carried.HoldTime > 0)
            {
                int remaining = Math.Max(0, Resource.HoldSeconds - carried.HoldCounter(nowMillis) / 1000);
                counter = remaining;
            }
        }

        return new ObjectiveHudState(
            PrimaryScore: myScore,
            SecondaryScore: enemyScore,
            PrimaryIsFriendly: true,
            StatusIcon: icon,
            StatusCounter: counter);
    }
}
