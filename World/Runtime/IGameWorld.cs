using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Runtime;

/// <summary>
/// Core contract for a playable simulation world. Extends <see cref="IMapSource"/>
/// with centralized physics stepping, dynent cache updates, player management,
/// and flying projectile/bouncer simulation.
/// </summary>
public interface IGameWorld : IMapSource
{
    /// <summary>Active flying projectiles (rockets) stepped centrally by this world's physics loop.</summary>
    List<RocketProjectile> ActiveRockets { get; }

    /// <summary>Active bouncers (grenades, debris, gibs) stepped centrally by this world's physics loop.</summary>
    List<BounceBody> ActiveBouncers { get; }

    /// <summary>Backing read-only list for active bouncers.</summary>
    IReadOnlyList<BounceBody> Bouncers => ActiveBouncers;

    /// <summary>Active players stepped by this world's physics loop.</summary>
    IReadOnlyList<MapPlayer> Players { get; }

    /// <summary>The local player whose team drives local-only presentation (e.g. flag colors).</summary>
    MapPlayer? LocalPlayer { get; set; }

    /// <summary>Active game mode info.</summary>
    GameModeInfo? Mode { get; set; }

    /// <summary>Registers a bouncer to be stepped by the world's physics loop.</summary>
    void RegisterBouncer(BounceBody bouncer);

    /// <summary>Unregisters a bouncer from the world's physics loop.</summary>
    void UnregisterBouncer(BounceBody bouncer);

    /// <summary>Resolves carrier player position and orientation by player ID from registered players.</summary>
    (Vector3 pos, float yaw, float aboveEye) GetPlayerOwnerInfo(int playerId)
    {
        for (int i = 0; i < Players.Count; i++)
        {
            var p = Players[i];
            if (p.PlayerId == playerId)
                return (p.Entity.O, p.Entity.Yaw, p.Entity.AboveEye);
        }
        return default;
    }
}
