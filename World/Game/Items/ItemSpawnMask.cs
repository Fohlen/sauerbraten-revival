using System;

namespace SauerbratenRevival.World.Game.Items;

/// <summary>
/// Bitmask for world item spawns corresponding to <see cref="ItemType"/> (1 &lt;&lt; (int)ItemType).
/// Replaces legacy <c>ModeFlags.NoItems</c> and <c>ModeFlags.NoAmmo</c> with exact item filtering.
/// </summary>
[Flags]
public enum ItemSpawnMask : ushort
{
    None = 0,
    Shells = 1 << ItemType.Shells,             // 1 << 0 = 1
    Bullets = 1 << ItemType.Bullets,           // 1 << 1 = 2
    Rockets = 1 << ItemType.Rockets,           // 1 << 2 = 4
    RifleRounds = 1 << ItemType.RifleRounds,   // 1 << 3 = 8
    Grenades = 1 << ItemType.Grenades,         // 1 << 4 = 16
    Cartridges = 1 << ItemType.Cartridges,     // 1 << 5 = 32
    Health = 1 << ItemType.Health,             // 1 << 6 = 64
    HealthBoost = 1 << ItemType.HealthBoost,   // 1 << 7 = 128
    GreenArmour = 1 << ItemType.GreenArmour,   // 1 << 8 = 256
    YellowArmour = 1 << ItemType.YellowArmour, // 1 << 9 = 512
    QuadDamage = 1 << ItemType.QuadDamage,     // 1 << 10 = 1024

    Ammo = Shells | Bullets | Rockets | RifleRounds | Grenades | Cartridges,
    Armour = GreenArmour | YellowArmour,
    Powerups = HealthBoost | QuadDamage,
    NonAmmo = Health | Armour | Powerups,
    All = Ammo | NonAmmo,
}
