using System;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Content;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game.Items;

/// <summary>
/// The engine's <c>itemstats[]</c> table (fpsgame/game.h:328, r6889), item model mappings,
/// and respawn timer parameters.
///
/// Follows ADR 0010:
/// - Core catalog: <c>res://resources/items/&lt;name&gt;.tres</c>
/// - Mod overrides: <c>res://resources/items/overrides/</c>
/// - User overrides: <c>user://items/overrides/</c>
/// - Hot path lookup: O(1) direct integer indexing via <see cref="All"/>, <see cref="Get(ItemType)"/>,
///   or <see cref="GetByEntityType(int)"/>.
/// </summary>
public static class ItemCatalog
{
    /// <summary>Number of standard engine item types (<c>I_SHELLS</c>..<c>I_QUAD</c>).</summary>
    public const int Count = 11;

    /// <summary>The engine's <c>I_SHELLS</c> entity type (== <c>ET_GAMESPECIFIC</c> == 8).</summary>
    public const int FirstItemType = EntityType.Shells;

    /// <summary>Directory holding the authored per-item <c>.tres</c> files.</summary>
    public const string ResourceDir = "res://resources/items";

    /// <summary>Directory scanned for modded item overrides.</summary>
    public const string ModOverrideDir = "res://resources/items/overrides";

    /// <summary>Directory scanned for user-specific item overrides.</summary>
    public const string UserOverrideDir = "user://items/overrides";

    /// <summary>
    /// Built-in defaults: the ported <c>itemstats[]</c> rows and server parameters.
    /// Used as zero-failure fallback when a <c>.tres</c> is absent.
    /// </summary>
    public static readonly ItemInfo[] Defaults =
    {
        // 0: Shells
        new()
        {
            Id = ItemType.Shells, Name = "shells", ShortName = "SG",
            Add = 10, Max = 30, Sound = GameSound.ItemAmmo, VoiceSound = GameSound.None,
            Info = (int)Gun.Shotgun, Model = "ammo/shells", PickupRadius = 12.0f,
            DelaySpawn = false, BaseSpawnTime = 4, ScalesWithPlayers = true,
            Icon = HudIcon.Sg,
        },
        // 1: Bullets
        new()
        {
            Id = ItemType.Bullets, Name = "bullets", ShortName = "CG",
            Add = 20, Max = 60, Sound = GameSound.ItemAmmo, VoiceSound = GameSound.None,
            Info = (int)Gun.Chaingun, Model = "ammo/bullets", PickupRadius = 12.0f,
            DelaySpawn = false, BaseSpawnTime = 4, ScalesWithPlayers = true,
            Icon = HudIcon.Cg,
        },
        // 2: Rockets
        new()
        {
            Id = ItemType.Rockets, Name = "rockets", ShortName = "RL",
            Add = 5, Max = 15, Sound = GameSound.ItemAmmo, VoiceSound = GameSound.None,
            Info = (int)Gun.RocketLauncher, Model = "ammo/rockets", PickupRadius = 12.0f,
            DelaySpawn = false, BaseSpawnTime = 4, ScalesWithPlayers = true,
            Icon = HudIcon.Rl,
        },
        // 3: RifleRounds
        new()
        {
            Id = ItemType.RifleRounds, Name = "riflerounds", ShortName = "RI",
            Add = 5, Max = 15, Sound = GameSound.ItemAmmo, VoiceSound = GameSound.None,
            Info = (int)Gun.Rifle, Model = "ammo/rrounds", PickupRadius = 12.0f,
            DelaySpawn = false, BaseSpawnTime = 4, ScalesWithPlayers = true,
            Icon = HudIcon.Rifle,
        },
        // 4: Grenades
        new()
        {
            Id = ItemType.Grenades, Name = "grenades", ShortName = "GL",
            Add = 10, Max = 30, Sound = GameSound.ItemAmmo, VoiceSound = GameSound.None,
            Info = (int)Gun.GrenadeLauncher, Model = "ammo/grenades", PickupRadius = 12.0f,
            DelaySpawn = false, BaseSpawnTime = 4, ScalesWithPlayers = true,
            Icon = HudIcon.Gl,
        },
        // 5: Cartridges
        new()
        {
            Id = ItemType.Cartridges, Name = "cartridges", ShortName = "PI",
            Add = 30, Max = 120, Sound = GameSound.ItemAmmo, VoiceSound = GameSound.None,
            Info = (int)Gun.Pistol, Model = "ammo/cartridges", PickupRadius = 12.0f,
            DelaySpawn = false, BaseSpawnTime = 4, ScalesWithPlayers = true,
            Icon = HudIcon.Pistol,
        },
        // 6: Health
        new()
        {
            Id = ItemType.Health, Name = "health", ShortName = "H",
            Add = 25, Max = 100, Sound = GameSound.ItemHealth, VoiceSound = GameSound.None,
            Info = -1, Model = "health", PickupRadius = 12.0f,
            DelaySpawn = false, BaseSpawnTime = 5, ScalesWithPlayers = true,
            Icon = HudIcon.Health,
        },
        // 7: HealthBoost
        new()
        {
            Id = ItemType.HealthBoost, Name = "healthboost", ShortName = "MH",
            Add = 100, Max = 200, Sound = GameSound.ItemHealth, VoiceSound = GameSound.VBoost,
            Info = 50, Model = "boost", PickupRadius = 12.0f,
            DelaySpawn = true, BaseSpawnTime = 60, ScalesWithPlayers = false,
            Icon = HudIcon.Health,
        },
        // 8: GreenArmour
        new()
        {
            Id = ItemType.GreenArmour, Name = "greenarmour", ShortName = "GA",
            Add = 100, Max = 100, Sound = GameSound.ItemArmour, VoiceSound = GameSound.None,
            Info = (int)ArmourType.Green, Model = "armor/green", PickupRadius = 12.0f,
            DelaySpawn = true, BaseSpawnTime = 20, ScalesWithPlayers = false,
            Icon = HudIcon.GreenArmour,
        },
        // 9: YellowArmour
        new()
        {
            Id = ItemType.YellowArmour, Name = "yellowarmour", ShortName = "YA",
            Add = 200, Max = 200, Sound = GameSound.ItemArmour, VoiceSound = GameSound.None,
            Info = (int)ArmourType.Yellow, Model = "armor/yellow", PickupRadius = 12.0f,
            DelaySpawn = true, BaseSpawnTime = 30, ScalesWithPlayers = false,
            Icon = HudIcon.YellowArmour,
        },
        // 10: QuadDamage
        new()
        {
            Id = ItemType.QuadDamage, Name = "quaddamage", ShortName = "Q",
            Add = 20000, Max = 30000, Sound = GameSound.ItemPup, VoiceSound = GameSound.VQuad,
            Info = -1, Model = "quad", PickupRadius = 12.0f,
            DelaySpawn = true, BaseSpawnTime = 70, ScalesWithPlayers = false,
            Icon = HudIcon.Quad,
        },
    };

    private static ItemInfo[]? _all;

    /// <summary>
    /// The active items catalogue: one <see cref="ItemInfo"/> per item index,
    /// loaded from <see cref="ResourceDir"/> and overlay directories; falls back to
    /// <see cref="Defaults"/>. Once loaded/reloaded, hot-path lookup is O(1) direct array indexing.
    /// </summary>
    public static ItemInfo[] All => _all ??= Load();

    /// <summary>Look up an item by typed enum ID (<see cref="ItemType"/> is the index).</summary>
    public static ItemInfo Get(ItemType type) => All[(int)type];

    /// <summary>Look up an item by 0-based dense index.</summary>
    public static ItemInfo Get(int index) => All[index];

    /// <summary>Look up an item by 0-based dense index, or null when out of range.</summary>
    public static ItemInfo? TryGet(int index) => index >= 0 && index < All.Length ? All[index] : null;

    /// <summary>
    /// Look up an item by map entity type (<c>ET_SHELLS</c>..<c>ET_QUAD</c>, 8..18).
    /// </summary>
    public static ItemInfo GetByEntityType(int entityType) => All[entityType - FirstItemType];

    /// <summary>
    /// Look up an item by map entity type, or null when out of range.
    /// </summary>
    public static ItemInfo? TryGetByEntityType(int entityType)
    {
        int index = entityType - FirstItemType;
        return index >= 0 && index < All.Length ? All[index] : null;
    }

    /// <summary>
    /// The ammo row for a gun (<c>itemstats[gun - GUN_SG]</c>), valid for
    /// <see cref="Gun.Shotgun"/>..<see cref="Gun.Pistol"/>.
    /// </summary>
    public static ItemInfo ForGun(Gun gun) => All[(int)gun - (int)Gun.Shotgun];

    /// <summary>Converts a map entity type (8..18) to <see cref="ItemType"/>.</summary>
    public static ItemType FromEntityType(int entityType)
    {
        int index = entityType - FirstItemType;
        return index >= 0 && index < Count ? (ItemType)index : ItemType.None;
    }

    /// <summary>Converts an <see cref="ItemType"/> to map entity type (8..18).</summary>
    public static int ToEntityType(ItemType itemType) =>
        itemType >= 0 ? (int)itemType + FirstItemType : -1;

    /// <summary>Replace the active item catalogue (test/mod seam).</summary>
    public static void Override(ItemInfo[] items) => _all = items;

    /// <summary>
    /// Reload the active item catalogue from the base directory and any override directories.
    /// </summary>
    public static void Reload(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        _all = Load(dir, modDir, userDir);
    }

    /// <summary>
    /// Load item definitions following the ADR 0010 3-tier cascade:
    /// 1. Core base directory (<paramref name="dir"/>), falling back to <see cref="Defaults"/>
    /// 2. Mod overrides (<paramref name="modDir"/>)
    /// 3. User overrides (<paramref name="userDir"/>)
    /// </summary>
    public static ItemInfo[] Load(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        return ResourceCascade.LoadCascade<ItemInfo, ItemInfo, ItemType>(
            Defaults,
            dir,
            modDir,
            userDir,
            def => def,
            () => new ItemInfo());
    }
}
