namespace SauerbratenRevival.World.Game.Items;

/// <summary>
/// Identifies the classic pickup items (engine <c>I_SHELLS</c>..<c>I_QUAD</c> in <c>fpsgame/game.h:328</c>).
/// Dense 0-based indices matching <see cref="ItemCatalog.All"/> and <see cref="ItemStats.All"/>.
/// </summary>
public enum ItemType
{
    None = -1,
    Shells = 0,
    Bullets = 1,
    Rockets = 2,
    RifleRounds = 3,
    Grenades = 4,
    Cartridges = 5,
    Health = 6,
    HealthBoost = 7,
    GreenArmour = 8,
    YellowArmour = 9,
    QuadDamage = 10,
}
