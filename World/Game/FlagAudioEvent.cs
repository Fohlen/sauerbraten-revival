using System;
using Godot;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// Audio event dispatched during flag state machine transitions.
/// Matches Sauerbraten engine sounds:
/// <list type="bullet">
/// <item><see cref="GameSound.FlagPickup"/> - Flag taken or picked up</item>
/// <item><see cref="GameSound.FlagDrop"/> - Flag dropped by carrier</item>
/// <item><see cref="GameSound.FlagReturn"/> - Friendly dropped flag returned</item>
/// <item><see cref="GameSound.FlagScore"/> - Flag captured/scored by friendly team</item>
/// <item><see cref="GameSound.FlagReset"/> - Flag reset after timeout or team penalty</item>
/// <item><see cref="GameSound.FlagFail"/> - Enemy team scored against local player</item>
/// </list>
/// </summary>
public readonly record struct FlagAudioEvent(
    GameSound Sound,
    int FlagId,
    int Team,
    int? PlayerId = null,
    Vector3? Position = null
);
