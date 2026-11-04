using System;
using System.Collections.Generic;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// The engine's curated classic-map pools (SVN r6889 <c>data/lists.cfg</c>).
///
/// Follows ADR 0010:
/// - Core catalog: <c>res://resources/maplists/&lt;name&gt;.tres</c>
/// - Mod overrides: <c>res://resources/maplists/overrides/</c>
/// - User overrides: <c>user://maplists/overrides/</c>
/// - Hot-path lookup: O(1) direct integer indexing via <see cref="All"/> or <see cref="Get(MapListId)"/>.
/// </summary>
public static class MapListCatalog
{
    /// <summary>Number of standard engine map lists (dm, capture, ctf, sp, concept, rpg).</summary>
    public const int Count = 6;

    /// <summary>Directory holding authored per-list <c>.tres</c> files.</summary>
    public const string ResourceDir = "res://resources/maplists";

    /// <summary>Directory scanned for modded map list overrides.</summary>
    public const string ModOverrideDir = "res://resources/maplists/overrides";

    /// <summary>Directory scanned for user-specific map list overrides.</summary>
    public const string UserOverrideDir = "user://maplists/overrides";

    /// <summary>
    /// Built-in defaults defining the 6 canonical engine map list slots.
    /// Used as zero-failure fallback when a <c>.tres</c> is absent.
    /// Full map pools are authored in resource files under <see cref="ResourceDir"/>.
    /// </summary>
    public static readonly MapList[] Defaults =
    {
        new()
        {
            Id = MapListId.Dm,
            Name = "ffa",
            Modes = new[] { "ffa", "coop_edit", "teamplay", "insta", "instateam", "effic", "efficteam", "tactics", "tactics_team", "race" }
        },
        new()
        {
            Id = MapListId.Capture,
            Name = "capture",
            Modes = new[] { "capture", "regen_capture", "hold", "insta_hold", "effic_hold" }
        },
        new()
        {
            Id = MapListId.Ctf,
            Name = "ctf",
            Modes = new[] { "ctf", "insta_ctf", "effic_ctf", "protect", "insta_protect", "effic_protect", "rugby" }
        },
        new() { Id = MapListId.Sp, Name = "single player" },
        new() { Id = MapListId.Concept, Name = "concept" },
        new() { Id = MapListId.Rpg, Name = "rpg" },
    };

    private static MapList[]? _all;

    /// <summary>
    /// Active map lists array. Loaded once from <see cref="ResourceDir"/> and overlay directories;
    /// falls back to <see cref="Defaults"/>. Once loaded/reloaded, hot-path lookup is O(1) direct indexing.
    /// </summary>
    public static MapList[] All => _all ??= Load();

    /// <summary>Lookup by typed <see cref="MapListId"/>.</summary>
    public static MapList Get(MapListId id) => Get((int)id);

    /// <summary>Clamped lookup matching engine slot indexing.</summary>
    public static MapList Get(int index) => All[Math.Clamp(index, 0, All.Length - 1)];

    /// <summary>Lookup map list by index, or null if out of range.</summary>
    public static MapList? TryGet(int index) => index >= 0 && index < All.Length ? All[index] : null;

    /// <summary>Lookup map list by typed <see cref="MapListId"/>, or null if out of range.</summary>
    public static MapList? TryGet(MapListId id) => TryGet((int)id);

    /// <summary>Look a list up by its stable id or display name (case-insensitive), or null.</summary>
    public static MapList? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        if (Enum.TryParse<MapListId>(id, ignoreCase: true, out var parsed) && parsed != MapListId.None)
        {
            var res = TryGet(parsed);
            if (res != null) return res;
        }

        foreach (var list in All)
        {
            if (string.Equals(list.Name, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(list.Id.ToString(), id, StringComparison.OrdinalIgnoreCase))
                return list;
        }

        return null;
    }

    /// <summary>Look up a list by id; throws when unknown.</summary>
    public static MapList Get(string id)
        => TryGet(id) ?? throw new KeyNotFoundException($"unknown map list '{id}'");

    /// <summary>Replace active map list catalogue (test/mod seam).</summary>
    public static void Override(MapList[] lists) => _all = lists;

    /// <summary>
    /// Reload active map list definitions from base directory and any override directories.
    /// </summary>
    public static void Reload(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        _all = Load(dir, modDir, userDir);
    }

    /// <summary>
    /// Load map list definitions following the ADR 0010 3-tier cascade:
    /// 1. Core base directory (<paramref name="dir"/>), falling back to <see cref="Defaults"/>
    /// 2. Mod overrides (<paramref name="modDir"/>)
    /// 3. User overrides (<paramref name="userDir"/>)
    /// </summary>
    public static MapList[] Load(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        return ResourceCascade.LoadCascade<MapList, MapList, MapListId>(
            Defaults,
            dir,
            modDir,
            userDir,
            def => def,
            () => new MapList());
    }

    /// <summary>
    /// The map list the front-end should offer for a mode, following the engine's
    /// maps GUI (<c>data/menus/_utility.cfg</c> <c>ML__SetupMode</c> → <c>ML_ModeMaps</c>).
    /// Checks <see cref="MapList.Modes"/> on loaded pools first, falling back to
    /// <see cref="ModeObjectiveResource.DefaultMapPool"/>.
    /// </summary>
    public static MapList? ForMode(GameModeInfo? mode)
    {
        if (mode == null) return TryGet(MapListId.Dm);

        // 1. Check if any map list explicitly includes this mode ID in its Modes list
        foreach (var list in All)
        {
            if (list.Modes != null && Array.Exists(list.Modes, m => string.Equals(m, mode.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return list;
            }
        }

        // 2. Fall back to mode's objective default map pool
        if (mode.Objective != null && mode.Objective.DefaultMapPool != MapListId.None)
        {
            return TryGet(mode.Objective.DefaultMapPool);
        }

        return TryGet(MapListId.Dm);
    }
}
