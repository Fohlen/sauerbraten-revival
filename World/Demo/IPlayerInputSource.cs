using SauerbratenRevival.Format;
using SauerbratenRevival.World.Game.Players;

namespace SauerbratenRevival.World.Demo;

/// <summary>
/// Source of player updates for simulation steps.
/// Replaces keyboard and mouse delta gathering when assigned to <see cref="MapPlayer.InputSource"/>.
/// </summary>
public interface IPlayerInputSource
{
    /// <summary>
    /// Fetches the update command for the given simulation tick.
    /// </summary>
    PlayerUpdateCmd? GetUpdate(int tick, MapPlayer player);
}
