using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game.Items;

/// <summary>
/// Manages map items (<c>ET_SHELLS</c>..<c>ET_QUAD</c>). Ports the engine's item
/// spawning, respawn timers, and client pickup checks (<c>fpsgame/entities.cpp</c>
/// and <c>fpsgame/server.cpp</c>).
/// </summary>
[Tool]
[GlobalClass]
public partial class ItemManager : Node3D
{
    private readonly List<Item> _items = new();

    // Standalone-only clock for when an ItemManager runs outside a MapWorld
    // (the world owns the shared SimClock in normal play).
    private readonly SimClock _standaloneClock = new();

    /// <summary>List of all item pickups registered with this manager.</summary>
    public IReadOnlyList<Item> Items => _items;

    /// <summary>Current active gamemode info.</summary>
    public GameModeInfo Mode { get; set; } = GameModeCatalog.Default;

    /// <summary>Number of players used to calculate item respawn delays (fpsgame/server.cpp spawntime).</summary>
    public int NumPlayers { get; set; } = 1;

    /// <summary>Whether this manager advances respawns and animations automatically during _Process.</summary>
    [Export] public bool AutoAdvance { get; set; } = true;

    /// <summary>Event fired when an item is picked up by a player.</summary>
    public event Action<MapPlayer, Item>? ItemPickedUp;

    /// <summary>Event fired when an item respawns.</summary>
    public event Action<Item>? ItemSpawned;

    public void AddItem(Item item)
    {
        _items.Add(item);
        AddChild(item);
    }

    /// <summary>
    /// Engine <c>server::delayspawn(type)</c> (fpsgame/server.cpp:880). Armour, boost and quad
    /// are delayed on initial spawn in multiplayer / standard modes.
    /// </summary>
    public static bool DelaySpawn(int type) => type switch
    {
        EntityType.GreenArmour or EntityType.YellowArmour or
        EntityType.HealthBoost or EntityType.QuadDamage => true,
        _ => false,
    };

    /// <summary>
    /// Engine <c>server::spawntime(type)</c> (fpsgame/server.cpp:857). Respawn delay in ms,
    /// scaling with player count for ammo and health.
    /// </summary>
    public static int SpawnTime(int type, int numPlayers = 1)
    {
        int np = numPlayers < 3 ? 4 : (numPlayers > 4 ? 2 : 3);
        int sec = type switch
        {
            EntityType.Shells or EntityType.Bullets or EntityType.Rockets or
            EntityType.RifleRounds or EntityType.Grenades or EntityType.Cartridges => np * 4,
            EntityType.Health => np * 5,
            EntityType.GreenArmour => 20,
            EntityType.YellowArmour => 30,
            EntityType.HealthBoost => 60,
            EntityType.QuadDamage => 70,
            _ => 0,
        };
        return sec * 1000;
    }

    /// <summary>
    /// Engine <c>server::canspawnitem(type)</c> (fpsgame/server.cpp:855). Checks whether an item
    /// type is allowed under the current game mode flags.
    /// </summary>
    public static bool CanSpawnItem(int type, ModeFlags mode)
    {
        if (mode.HasFlag(ModeFlags.NoItems)) return false;
        if (type < ItemStats.FirstItemType || type >= ItemStats.FirstItemType + ItemStats.Count) return false;
        if (mode.HasFlag(ModeFlags.NoAmmo) && type >= EntityType.Shells && type <= EntityType.Cartridges) return false;
        return true;
    }

    /// <summary>
    /// Engine <c>spawnitems(force)</c> (fpsgame/entities.cpp:372). Initialises or resets item spawns
    /// according to the active game mode and delay rules.
    /// </summary>
    public void SpawnItems(bool force = false) => SpawnItems(Mode, force);

    /// <summary>
    /// Engine <c>spawnitems(force)</c> with an explicit mode override.
    /// </summary>
    public void SpawnItems(GameModeInfo? mode, bool force = false)
    {
        if (mode != null) Mode = mode;
        ModeFlags flags = Mode.Flags;

        if (flags.HasFlag(ModeFlags.NoItems))
        {
            foreach (var item in _items)
            {
                item.Spawned = false;
                item.SpawnTime = 0;
                item.UpdateVisual(0);
            }
            return;
        }

        foreach (var item in _items)
        {
            if (!CanSpawnItem(item.ItemType, flags))
            {
                item.Spawned = false;
                item.SpawnTime = 0;
                item.UpdateVisual(0);
                continue;
            }

            bool spawnNow = force || !DelaySpawn(item.ItemType);
            item.Spawned = spawnNow;
            item.SpawnTime = spawnNow ? 0 : SpawnTime(item.ItemType, NumPlayers);
            item.UpdateVisual(0);
        }
    }

    /// <summary>Engine <c>resetspawns()</c> (fpsgame/entities.cpp:370).</summary>
    public void ResetSpawns()
    {
        foreach (var item in _items)
        {
            item.Spawned = false;
            item.SpawnTime = 0;
            item.UpdateVisual(0);
        }
    }

    /// <summary>Engine <c>setspawn(i, on)</c> (fpsgame/entities.cpp:386).</summary>
    public void SetSpawn(int itemIndex, bool on)
    {
        if (itemIndex >= 0 && itemIndex < _items.Count)
        {
            var item = _items[itemIndex];
            item.Spawned = on;
            item.SpawnTime = 0;
            item.UpdateVisual(0);
        }
    }

    /// <summary>
    /// Updates item respawn countdown timers. Call during the fixed physics step.
    /// </summary>
    public void UpdateRespawns(int deltaMillis)
    {
        foreach (var item in _items)
        {
            if (!item.Spawned && item.SpawnTime > 0)
            {
                item.SpawnTime -= deltaMillis;
                if (item.SpawnTime <= 0)
                {
                    item.SpawnTime = 0;
                    item.Spawned = true;
                    item.UpdateVisual(0);
                    ItemSpawned?.Invoke(item);
                }
            }
        }
    }

    /// <summary>
    /// Updates visual bobbing and spinning for all items. Call during the render frame (_Process).
    /// </summary>
    public void UpdateVisual(int lastMillis)
    {
        foreach (var item in _items)
        {
            item.UpdateVisual(lastMillis);
        }
    }

    /// <summary>
    /// Advances item respawn timers and updates visual bobbing and spinning.
    /// Retained for standalone testing and backwards compatibility.
    /// </summary>
    public void Advance(int deltaMillis, int lastMillis)
    {
        UpdateRespawns(deltaMillis);
        UpdateVisual(lastMillis);
    }

    /// <summary>
    /// Checks all spawned items against a player's feet position, picking up the first
    /// reachable item that the player can accept (<c>fpsgame/entities.cpp:334 checkitems</c>).
    /// Returns the item picked up, or null if none.
    /// </summary>
    public Item? CheckPickups(MapPlayer player, int lastMillis)
    {
        if (!player.IsAlive || player.Floating) return null;

        Vector3 feet = player.Entity.FeetPos();
        foreach (var item in _items)
        {
            if (!item.Spawned) continue;

            float dist = item.BasePosition.DistanceTo(feet);
            if (dist < item.PickupRadius)
            {
                if (player.Combat.CanPickup(item.ItemType))
                {
                    item.Spawned = false;
                    item.SpawnTime = SpawnTime(item.ItemType, NumPlayers);
                    item.UpdateVisual(lastMillis);

                    player.Pickup(item.ItemType);

                    // Audio effects matching fpsgame/entities.cpp:184-198
                    ItemStat stat = ItemStats.Get(item.ItemType);
                    player.PlaySound(stat.Sound);

                    if (item.ItemType == EntityType.HealthBoost)
                    {
                        player.PlaySound(GameSound.VBoost);
                    }
                    else if (item.ItemType == EntityType.QuadDamage)
                    {
                        player.PlaySound(GameSound.VQuad);
                    }

                    ItemPickedUp?.Invoke(player, item);
                    return item;
                }
            }
        }
        return null;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationParented)
        {
            if (GetParent() is MapWorld mw)
            {
                AutoAdvance = false;
                if (mw.Mode != null) Mode = mw.Mode;
            }
        }
    }

    public override void _EnterTree()
    {
        if (GetParent() is MapWorld mw)
        {
            AutoAdvance = false;
            if (mw.Mode != null) Mode = mw.Mode;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Engine.IsEditorHint()) return;
        if (!AutoAdvance || GetParent() is MapWorld) return;

        int deltaMillis = (int)System.Math.Round(delta * 1000.0);
        _standaloneClock.Advance(deltaMillis);
        UpdateRespawns(deltaMillis);
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint()) return;
        if (!AutoAdvance || GetParent() is MapWorld) return;

        UpdateVisual(_standaloneClock.NowMillis);
    }
}
