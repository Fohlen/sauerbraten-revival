using SauerbratenRevival.World.Game;

namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// One row of the engine's <c>itemstats[]</c> table (fpsgame/game.h:328). Static
/// definition data only. The table is indexed by item type starting at
/// <c>I_SHELLS</c> (<c>ET_GAMESPECIFIC</c>), and the ammo rows' <see cref="Info"/>
/// is the gun they restock, which is what <see cref="PlayerState.BaseAmmo"/> and
/// <c>addammo</c> use.
/// </summary>
public sealed class ItemStat
{
    public ItemStat(int add, int max, GameSound sound, string name, int info)
    {
        Add = add;
        Max = max;
        Sound = sound;
        Name = name;
        Info = info;
    }

    /// <summary>Amount added by one pickup (<c>itemstats[].add</c>).</summary>
    public int Add { get; }

    /// <summary>Maximum ammo/health/armour (<c>itemstats[].max</c>).</summary>
    public int Max { get; }

    /// <summary>Pickup sound (<c>S_ITEM*</c>).</summary>
    public GameSound Sound { get; }

    /// <summary>Short HUD name (e.g. "SG", "MH", "YA").</summary>
    public string Name { get; }

    /// <summary>Payload: the gun id for ammo, the <see cref="ArmourType"/> for armour, else -1 or health boost info.</summary>
    public int Info { get; }
}

/// <summary>
/// The engine's <c>itemstats[]</c> table (fpsgame/game.h:328, r6889), in engine
/// order. Only the data <see cref="PlayerState"/> needs today is consumed
/// (<c>baseammo</c>/<c>addammo</c>); <c>pickup</c>/<c>canpickup</c> land with item
/// pickup. Locked verbatim to the source by <c>test/PlayerStateTest.cs</c>.
/// </summary>
public static class ItemStats
{
    /// <summary>Number of rows (<c>I_SHELLS</c>..<c>I_QUAD</c>).</summary>
    public const int Count = 11;

    /// <summary>The engine's <c>I_SHELLS</c> (== <c>ET_GAMESPECIFIC</c> == 8).</summary>
    public const int FirstItemType = 8;

    public static readonly ItemStat[] All =
    {
        //                        add    max    sound             name   info
        new(10,    30,  GameSound.ItemAmmo,   "SG", (int)Gun.Shotgun),
        new(20,    60,  GameSound.ItemAmmo,   "CG", (int)Gun.Chaingun),
        new(5,     15,  GameSound.ItemAmmo,   "RL", (int)Gun.RocketLauncher),
        new(5,     15,  GameSound.ItemAmmo,   "RI", (int)Gun.Rifle),
        new(10,    30,  GameSound.ItemAmmo,   "GL", (int)Gun.GrenadeLauncher),
        new(30,    120, GameSound.ItemAmmo,   "PI", (int)Gun.Pistol),
        new(25,    100, GameSound.ItemHealth, "H",  -1),
        new(100,   200, GameSound.ItemHealth, "MH", 50),
        new(100,   100, GameSound.ItemArmour, "GA", (int)ArmourType.Green),
        new(200,   200, GameSound.ItemArmour, "YA", (int)ArmourType.Yellow),
        new(20000, 30000, GameSound.ItemPup,  "Q",  -1),
    };

    /// <summary>Row for an item type (<c>I_SHELLS</c>..<c>I_QUAD</c>).</summary>
    public static ItemStat Get(int itemType) => All[itemType - FirstItemType];

    /// <summary>
    /// The ammo row for a gun (<c>itemstats[gun - GUN_SG]</c>), valid for
    /// <see cref="Gun.Shotgun"/>..<see cref="Gun.Pistol"/>.
    /// </summary>
    public static ItemStat ForGun(Gun gun) => All[(int)gun - (int)Gun.Shotgun];
}
