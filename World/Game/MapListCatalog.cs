using System;
using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// The active classic-map lists, loaded from the <c>.tres</c> files under
/// <see cref="ResourceDir"/>. These are the engine's own <c>MapList_*</c> sets
/// from <c>data/lists.cfg</c> (SVN r6889), imported verbatim so the front-end
/// offers the same per-mode map pools.
///
/// Mirrors <see cref="GameModeCatalog"/>: data-first content, editable in the
/// inspector and extendable by a mod pack mounting its own list into the
/// directory, with a lookup seam (<see cref="Override"/>) for tests.
/// </summary>
public static class MapListCatalog
{
    /// <summary>Directory scanned for authored map-list <c>.tres</c> files.</summary>
    public const string ResourceDir = "res://resources/maplists";

    private static MapList[]? _all;

    /// <summary>
    /// The active catalog, loaded from <see cref="ResourceDir"/>. Cached after
    /// first access; use <see cref="Override"/> to replace it (mods/tests).
    /// </summary>
    public static MapList[] All => _all ??= Load();

    /// <summary>Load every list in <paramref name="dir"/> (ordered by filename for determinism).</summary>
    public static MapList[] Load(string dir = ResourceDir)
    {
        var lists = new List<MapList>();
        using var dirAccess = DirAccess.Open(dir);
        if (dirAccess == null) return Array.Empty<MapList>();

        string[] files = dirAccess.GetFiles();
        Array.Sort(files, StringComparer.Ordinal);
        foreach (string file in files)
        {
            // Exported PCKs replace each authored `foo.tres` with a
            // `foo.tres.remap` sidecar, and DirAccess lists only the sidecar.
            // Strip it so Load follows the remap to the packed resource.
            string resource = file.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)
                ? file[..^".remap".Length]
                : file;
            if (!resource.EndsWith(".tres", StringComparison.OrdinalIgnoreCase)) continue;
            var loaded = ResourceLoader.Load($"{dir.TrimEnd('/')}/{resource}") as MapList;
            if (loaded == null || string.IsNullOrEmpty(loaded.Id)) continue;
            lists.Add(loaded);
        }
        return lists.ToArray();
    }

    /// <summary>Look a list up by its stable id or display name (case-insensitive), or null.</summary>
    public static MapList? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var list in All)
        {
            if (string.Equals(list.Id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(list.Name, id, StringComparison.OrdinalIgnoreCase))
                return list;
        }
        return null;
    }

    /// <summary>Look up a list by id; throws when unknown.</summary>
    public static MapList Get(string id)
        => TryGet(id) ?? throw new KeyNotFoundException($"unknown map list '{id}'");

    /// <summary>Replace the cached catalog (test/mod seam).</summary>
    public static void Override(MapList[] lists) => _all = lists;

    /// <summary>Map lists whose modes the engine's maps GUI feeds from the capture pool.</summary>
    private static readonly HashSet<string> CaptureListModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "capture", "regen_capture", "hold", "insta_hold", "effic_hold",
    };

    /// <summary>
    /// The map list the front-end should offer for a mode, following the engine's
    /// maps GUI (<c>data/menus/_utility.cfg</c> <c>ML__SetupMode</c> →
    /// <c>ML_ModeMaps</c>): CTF/protect/collect use the CTF pool, capture and hold
    /// use the capture pool, and free-for-all/teamplay/edit use the deathmatch
    /// pool. (The engine additionally offers all pools as tabs for those modes;
    /// with a single left/right picker we start from the deathmatch pool.)
    /// </summary>
    public static MapList? ForMode(GameModeInfo? mode)
    {
        if (mode == null) return TryGet("dm");

        // Hold is an M_CTF mode in the engine, but its maps GUI uses the capture
        // list (ML_ModeType 4 -> ML_ModeMaps 1), so check it before the Ctf flag.
        if (CaptureListModes.Contains(mode.Id)) return TryGet("capture");
        if (mode.Has(ModeFlags.Capture) || mode.Has(ModeFlags.Hold)) return TryGet("capture");
        if (mode.Has(ModeFlags.Ctf)) return TryGet("ctf");
        return TryGet("dm");
    }
}
