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
    MapTeamFlags = 0,      // Spawns team 1 & 2 flags at ET_FLAG entities (CTF, Protect, Rugby)
    DynamicHoldSpawns = 1, // Uses ET_BASE entities as spawn points for neutral flags (Hold)
}

public enum FlagScoreRule
{
    RelayToFriendlyBase = 0, // Carry enemy flag to home base while own flag is home (CTF, Rugby)
    TouchEnemyFlag = 1,      // Touch enemy flag to score immediately (Protect)
    HoldTimer = 2,           // Hold flag for HoldSeconds to score (Hold)
}

public enum FlagReturnRule
{
    TouchDropped = 0,   // Touch dropped friendly flag to return; auto-reset after ResetDelayMillis (CTF, Rugby)
    DropInsideBase = 1, // Drop inside own base to return; timeout deducts 1 point (Protect)
    RandomRespawn = 2,  // Timeout respawns flag at a new hold spawn (Hold)
}

[Tool]
[GlobalClass]
public partial class FlagObjectiveResource : ModeObjectiveResource
{
    [Export] public FlagPlacementMode Placement { get; set; } = FlagPlacementMode.MapTeamFlags;
    [Export] public FlagScoreRule ScoreRule { get; set; } = FlagScoreRule.RelayToFriendlyBase;
    [Export] public FlagReturnRule ReturnRule { get; set; } = FlagReturnRule.TouchDropped;
    [Export] public int ResetDelayMillis { get; set; } = 10000;
    [Export] public int InvisDelayMillis { get; set; } = 20000;
    [Export] public int HoldSeconds { get; set; } = 20;
    [Export] public int HoldDeathPenaltySeconds { get; set; } = 5;

    /// <summary>
    /// When true (Rugby mode), shooting a friendly teammate while carrying a flag
    /// passes the flag directly to that teammate instead of dealing damage.
    /// </summary>
    [Export] public bool PassOnFriendlyHit { get; set; } = false;

    public FlagObjectiveResource()
    {
        Id = "ctf";
        Name = "ctf";
        DefaultTeamPlay = true;
        DefaultMapPool = MapListId.Ctf;
        DefaultSpawnArmour = 50;
    }

    public override IModeObjective CreateInstance(GameModeInfo mode) => new FlagObjective(this, mode);
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
        Mode = mode;
        Flags = flags ?? new FlagManager { Mode = mode };
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
        Flags.Update(nowMillis);
    }

    public void StepPlayer(MapPlayer player, int nowMillis)
    {
        player.CheckFlags(nowMillis);
    }

    public bool OnBeforeDamage(
        MapPlayer? attacker,
        MapPlayer victim,
        Gun gun,
        ref int damage,
        ref Vector3 pushVel,
        int nowMillis)
    {
        if (Resource.PassOnFriendlyHit && attacker != null && attacker != victim && attacker.Team == victim.Team)
        {
            int? carried = Flags.GetCarriedFlag(attacker.PlayerId);
            if (carried.HasValue)
            {
                Flags.OwnFlag(carried.Value, victim.PlayerId, nowMillis);
                damage = 0;
                pushVel = Vector3.Zero;
                return false;
            }
        }
        return true;
    }

    public void OnPlayerDied(MapPlayer victim, MapPlayer? killer, Gun gun, int nowMillis)
    {
        Flags.OnPlayerDied(victim.PlayerId, null, false, victim.Entity.FeetPos(), nowMillis, victim.Entity.Yaw);
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
                int remaining = Math.Max(0, (carried.HoldTime - nowMillis + 999) / 1000);
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
