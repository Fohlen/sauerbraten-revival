using System;
using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Models;

/// <summary>
/// The engine's player model table (render.cpp <c>playermodels[5]</c>).
///
/// Follows ADR 0010:
/// - Core catalog: <c>res://resources/playermodels/&lt;name&gt;.tres</c>
/// - Mod overrides: <c>res://resources/playermodels/overrides/</c>
/// - User overrides: <c>user://playermodels/overrides/</c>
/// - Hot-path lookup: O(1) direct integer indexing via <see cref="All"/> or <see cref="Get(PlayerModelId)"/>.
/// </summary>
public static class PlayerModelCatalog
{
    /// <summary>Directory holding the authored per-model <c>.tres</c> files.</summary>
    public const string ResourceDir = "res://resources/playermodels";

    /// <summary>Directory scanned for modded player model overrides.</summary>
    public const string ModOverrideDir = "res://resources/playermodels/overrides";

    /// <summary>Directory scanned for user-specific player model overrides.</summary>
    public const string UserOverrideDir = "user://playermodels/overrides";

    /// <summary>
    /// Built-in defaults: the ported <c>playermodels[]</c> rows (render.cpp).
    /// Used as the zero-failure fallback when a <c>.tres</c> is absent.
    /// </summary>
    public static readonly PlayerModelInfo[] Defaults =
    {
        new()
        {
            Id = PlayerModelId.MrFixit, Name = "mrfixit",
            Ffa = "mrfixit", BlueTeam = "mrfixit/blue", RedTeam = "mrfixit/red",
            Hudguns = "mrfixit/hudguns", Vwep = null, Quad = "mrfixit/horns",
            Armour = new[] { "mrfixit/armor/blue", "mrfixit/armor/green", "mrfixit/armor/yellow" },
            FfaIcon = "mrfixit", BlueIcon = "mrfixit_blue", RedIcon = "mrfixit_red", Ragdoll = true,
        },
        new()
        {
            Id = PlayerModelId.Snoutx10k, Name = "snoutx10k",
            Ffa = "snoutx10k", BlueTeam = "snoutx10k/blue", RedTeam = "snoutx10k/red",
            Hudguns = "snoutx10k/hudguns", Vwep = null, Quad = "snoutx10k/wings",
            Armour = new[] { "snoutx10k/armor/blue", "snoutx10k/armor/green", "snoutx10k/armor/yellow" },
            FfaIcon = "snoutx10k", BlueIcon = "snoutx10k_blue", RedIcon = "snoutx10k_red", Ragdoll = true,
        },
        // The engine's third entry (ogro/green) is commented out; ogro2 is index 2.
        new()
        {
            Id = PlayerModelId.Ogro2, Name = "ogro2",
            Ffa = "ogro2", BlueTeam = "ogro2/blue", RedTeam = "ogro2/red",
            Hudguns = "mrfixit/hudguns", Vwep = null, Quad = "ogro2/quad",
            Armour = new[] { "ogro2/armor/blue", "ogro2/armor/green", "ogro2/armor/yellow" },
            FfaIcon = "ogro", BlueIcon = "ogro_blue", RedIcon = "ogro_red", Ragdoll = true,
        },
        new()
        {
            Id = PlayerModelId.Inky, Name = "inky",
            Ffa = "inky", BlueTeam = "inky/blue", RedTeam = "inky/red",
            Hudguns = "inky/hudguns", Vwep = null, Quad = "inky/quad",
            Armour = new[] { "inky/armor/blue", "inky/armor/green", "inky/armor/yellow" },
            FfaIcon = "inky", BlueIcon = "inky_blue", RedIcon = "inky_red", Ragdoll = true,
        },
        new()
        {
            Id = PlayerModelId.CaptainCannon, Name = "captaincannon",
            Ffa = "captaincannon", BlueTeam = "captaincannon/blue", RedTeam = "captaincannon/red",
            Hudguns = "captaincannon/hudguns", Vwep = null, Quad = "captaincannon/quad",
            Armour = new[] { "captaincannon/armor/blue", "captaincannon/armor/green", "captaincannon/armor/yellow" },
            FfaIcon = "captaincannon", BlueIcon = "captaincannon_blue", RedIcon = "captaincannon_red", Ragdoll = true,
        },
    };

    private static PlayerModelInfo[]? _all;

    /// <summary>
    /// The active player models catalogue: one <see cref="PlayerModelInfo"/> per model index,
    /// loaded from <see cref="ResourceDir"/> and overlay directories; falls back to
    /// <see cref="Defaults"/>. Once loaded/reloaded, hot-path lookup is O(1) direct array indexing.
    /// </summary>
    public static PlayerModelInfo[] All => _all ??= Load();

    /// <summary>The total number of currently configured player models.</summary>
    public static int Count => All.Length;

    /// <summary>Look up a player model by typed enum id (<see cref="PlayerModelId"/>).</summary>
    public static PlayerModelInfo Get(PlayerModelId id) => Get((int)id);

    /// <summary>Clamped lookup (<c>getplayermodelinfo(int)</c>).</summary>
    public static PlayerModelInfo Get(int index) => All[Math.Clamp(index, 0, All.Length - 1)];

    /// <summary>Look up a player model by index, or null when out of range.</summary>
    public static PlayerModelInfo? TryGet(int index) => index >= 0 && index < All.Length ? All[index] : null;

    /// <summary>render.cpp <c>chooserandomplayermodel</c>.</summary>
    public static int ChooseRandom(int seed) => (seed & 0xFFFF) % All.Length;

    /// <summary>Replace the active player model catalogue (test/mod seam).</summary>
    public static void Override(PlayerModelInfo[] models) => _all = models;

    /// <summary>
    /// Reload the active player model catalogue from the base directory and any override directories.
    /// </summary>
    public static void Reload(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        _all = Load(dir, modDir, userDir);
    }

    /// <summary>
    /// Load player model definitions following the ADR 0010 3-tier cascade:
    /// 1. Core base directory (<paramref name="dir"/>), falling back to <see cref="Defaults"/>
    /// 2. Mod overrides (<paramref name="modDir"/>)
    /// 3. User overrides (<paramref name="userDir"/>)
    /// </summary>
    public static PlayerModelInfo[] Load(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        return ResourceCascade.LoadCascade<PlayerModelInfo, PlayerModelInfo, PlayerModelId>(
            Defaults,
            dir,
            modDir,
            userDir,
            def => def,
            () => new PlayerModelInfo());
    }
}
