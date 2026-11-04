using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Runtime;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game.Objectives;

/// <summary>
/// Runtime state machine for a match objective (Deathmatch, Edit, Flags, Capture, Racing, Prop Hunt, or GDScript).
/// Instantiated once per match from a <see cref="ModeObjectiveResource"/>.
/// </summary>
public interface IModeObjective
{
    /// <summary>Definition resource that created this objective instance.</summary>
    ModeObjectiveResource Definition { get; }

    /// <summary>
    /// Returns true if this objective claims ownership of the map entity <paramref name="e"/>
    /// (preventing ModeEntityBuilder from rendering a static placeholder for it).
    /// </summary>
    bool ClaimsEntity(MapEntity e) => false;

    /// <summary>
    /// Called during world build to initialize objective state from map entities
    /// and attach dynamic 3D nodes (e.g. FlagNodes, BaseNodes, Checkpoints) under <paramref name="objectiveRoot"/>.
    /// </summary>
    void SetupWorld(IGameWorld world, IReadOnlyList<MapEntity> entities, Node3D objectiveRoot) { }

    /// <summary>
    /// Called once per 5 ms physics substep by WorldSimulation.Step.
    /// Advances objective timers (flag drop resets, hold countdowns, capture ticks, race timers, dynamic world commands).
    /// </summary>
    void Step(IGameWorld world, int stepMillis, int nowMillis) { }

    /// <summary>
    /// Called once per 5 ms physics substep for each alive player.
    /// Handles proximity triggers (flag pickups/scores, base capture zones, race checkpoints).
    /// </summary>
    void StepPlayer(MapPlayer player, int nowMillis) { }

    /// <summary>
    /// Called once per render frame (_Process) to interpolate visual objective nodes.
    /// </summary>
    void UpdateVisual(IGameWorld world, int lastMillis) { }

    /// <summary>
    /// Optional override for where <paramref name="player"/> spawns (e.g. last reached checkpoint in Racing).
    /// Returns null to use standard map ET_PLAYERSTART resolution.
    /// </summary>
    (Vector3 FeetPos, float Yaw)? ResolveSpawn(MapPlayer player) => null;

    /// <summary>Called after <paramref name="player"/> respawns and receives their mode loadout.</summary>
    void OnPlayerSpawn(MapPlayer player) { }

    /// <summary>
    /// Pre-damage hook invoked before hitscan or explosion damage/push is applied.
    /// Return false to cancel standard damage (e.g. friendly rifle shot passing the flag in Rugby).
    /// </summary>
    bool OnBeforeDamage(
        MapPlayer? attacker,
        MapPlayer victim,
        Gun gun,
        ref int damage,
        ref Vector3 pushVel,
        int nowMillis) => true;

    /// <summary>
    /// Called when <paramref name="victim"/> dies. Handles flag drops, hold penalties, skull drops, or frag scoring.
    /// </summary>
    void OnPlayerDied(MapPlayer victim, MapPlayer? killer, Gun gun, int nowMillis) { }

    /// <summary>Returns the current score and bottom status-bar indicator for <paramref name="localPlayer"/>.</summary>
    ObjectiveHudState GetHudState(MapPlayer localPlayer, int nowMillis) => default;
}
