using SauerbratenRevival.World.Hud;

namespace SauerbratenRevival.World.Game.Objectives;

/// <summary>
/// Zero-allocation HUD snapshot returned by <see cref="IModeObjective.GetHudState"/>.
/// Drives the top-screen match score display and the bottom status-bar objective indicator.
/// </summary>
public readonly record struct ObjectiveHudState(
    int PrimaryScore = int.MinValue,
    int SecondaryScore = int.MinValue,
    bool PrimaryIsFriendly = true,
    HudIcon? StatusIcon = null,
    int StatusCounter = -1);
