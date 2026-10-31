using System;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game.Items;

/// <summary>
/// Legacy wrapper for <see cref="ItemInfo"/> to maintain backwards compatibility.
/// </summary>
[Obsolete("Use ItemInfo instead.")]
public partial class ItemStat : ItemInfo
{
    public ItemStat() { }

    public ItemStat(int add, int max, GameSound sound, string name, int info)
        : base(add, max, sound, name, info)
    {
    }
}

/// <summary>
/// The engine's <c>itemstats[]</c> table (fpsgame/game.h:328, r6889).
/// Delegates to <see cref="ItemCatalog"/> as per ADR 0010.
/// </summary>
public static class ItemStats
{
    /// <summary>Number of rows (<c>I_SHELLS</c>..<c>I_QUAD</c>).</summary>
    public const int Count = ItemCatalog.Count;

    /// <summary>The engine's <c>I_SHELLS</c> (== <c>ET_GAMESPECIFIC</c> == 8).</summary>
    public const int FirstItemType = ItemCatalog.FirstItemType;

    /// <summary>The active item definitions table.</summary>
    public static ItemInfo[] All => ItemCatalog.All;

    /// <summary>Row for an entity type (<c>I_SHELLS</c>..<c>I_QUAD</c>, 8..18).</summary>
    public static ItemInfo Get(int itemType) => ItemCatalog.GetByEntityType(itemType);

    /// <summary>
    /// The ammo row for a gun (<c>itemstats[gun - GUN_SG]</c>), valid for
    /// <see cref="Gun.Shotgun"/>..<see cref="Gun.Pistol"/>.
    /// </summary>
    public static ItemInfo ForGun(Gun gun) => ItemCatalog.ForGun(gun);
}
