using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Content;
using SauerbratenRevival.World.Hud;

namespace SauerbratenRevival.World.Game.Items;

/// <summary>
/// One pickup item definition (ports <c>itemstats[]</c> in <c>fpsgame/game.h:328</c>,
/// model mappings in <c>fpsgame/entities.cpp:entmdlname</c>, and respawn logic in <c>fpsgame/server.cpp</c>).
///
/// Follows ADR 0010 as a typed <see cref="Resource"/> with Godot inspector visibility,
/// 3-tier cascade modding, and pre-baked flat table runtime indexing.
/// </summary>
[Tool]
[GlobalClass]
public partial class ItemInfo : Resource, IResourceDefinition<ItemType>
{
    /// <summary>Stable item enum ID (<see cref="ItemType"/>).</summary>
    [Export] public ItemType Id { get; set; } = ItemType.None;

    /// <summary>Descriptive canonical name (e.g. "shells", "health", "quaddamage").</summary>
    [Export] public string Name { get; set; } = "";

    /// <summary>Short HUD abbreviation (e.g. "SG", "MH", "YA").</summary>
    [Export] public string ShortName { get; set; } = "";

    /// <summary>Amount added on pickup (<c>itemstats[].add</c>).</summary>
    [Export] public int Add { get; set; }

    /// <summary>Maximum ammo/health/armour allowed (<c>itemstats[].max</c>).</summary>
    [Export] public int Max { get; set; }

    /// <summary>Primary pickup sound (<c>itemstats[].sound</c>).</summary>
    [Export] public GameSound Sound { get; set; } = GameSound.None;

    /// <summary>Optional announcer/voice sound played on pickup (e.g. <see cref="GameSound.VBoost"/> or <see cref="GameSound.VQuad"/>).</summary>
    [Export] public GameSound VoiceSound { get; set; } = GameSound.None;

    /// <summary>Item payload: Gun ID for ammo, ArmourType for armour, boost info, or -1.</summary>
    [Export] public int Info { get; set; } = -1;

    /// <summary>Model resource path relative to packages/ (e.g. "ammo/shells", "health", "quad").</summary>
    [Export] public string Model { get; set; } = "";

    /// <summary>Collision trigger radius for player pickup detection.</summary>
    [Export] public float PickupRadius { get; set; } = 12.0f;

    /// <summary>Whether this item's initial spawn is delayed in standard/competitive matches.</summary>
    [Export] public bool DelaySpawn { get; set; }

    /// <summary>Base respawn time in seconds (either fixed or scaled with player count).</summary>
    [Export] public int BaseSpawnTime { get; set; }

    /// <summary>Whether the respawn time scales dynamically with player count (ammo and health).</summary>
    [Export] public bool ScalesWithPlayers { get; set; }

    /// <summary>HUD icon index (<c>itemstats[].icon</c>).</summary>
    [Export] public HudIcon Icon { get; set; }

    public ItemInfo() { }

    public ItemInfo(int add, int max, GameSound sound, string name, int info)
    {
        Add = add;
        Max = max;
        Sound = sound;
        Name = name;
        ShortName = name;
        Info = info;
    }

    /// <summary>
    /// Calculates the respawn delay in milliseconds matching engine <c>fpsgame/server.cpp:spawntime</c>.
    /// </summary>
    public int CalculateSpawnTime(int numPlayers = 1)
    {
        int np = numPlayers < 3 ? 4 : (numPlayers > 4 ? 2 : 3);
        int sec = ScalesWithPlayers ? np * BaseSpawnTime : BaseSpawnTime;
        return sec * 1000;
    }
}
