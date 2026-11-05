using System;
using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// The active game-mode definitions, loaded from the <c>.tres</c> files following the
/// ADR 0010 3-tier cascade:
/// 1. Core base directory (<see cref="ResourceDir"/>), falling back to <see cref="Defaults"/>
/// 2. Mod overrides (<see cref="ModOverrideDir"/>)
/// 3. User overrides (<see cref="UserOverrideDir"/>)
///
/// Modes are <see cref="GameModeInfo"/> Resources keyed by a stable string <see cref="GameModeInfo.Id"/>,
/// pre-baked for zero-allocation hot-path reads.
/// </summary>
public static class GameModeCatalog
{
    /// <summary>Directory scanned for authored per-mode <c>.tres</c> files.</summary>
    public const string ResourceDir = "res://resources/gamemodes";

    /// <summary>Directory scanned for modded game mode overrides.</summary>
    public const string ModOverrideDir = "res://resources/gamemodes/overrides";

    /// <summary>Directory scanned for user-specific game mode overrides.</summary>
    public const string UserOverrideDir = "user://gamemodes/overrides";

    /// <summary>
    /// Built-in default modes (DM, Teamplay, Coop Edit, and CTF across normal, instagib, efficiency, and tactics loadouts).
    /// </summary>
    public static readonly GameModeInfo[] Defaults;

    static GameModeCatalog()
    {
        Defaults = new GameModeInfo[]
        {
            new() { Id = "ffa",           Name = "ffa",           ObjectiveId = "dm",   TeamPlay = false, Mutator = "" },
            new() { Id = "coop_edit",     Name = "coop edit",     ObjectiveId = "edit", TeamPlay = false, Mutator = "" },
            new() { Id = "teamplay",      Name = "teamplay",      ObjectiveId = "dm",   TeamPlay = true,  Mutator = "" },
            new() { Id = "insta",         Name = "instagib",      ObjectiveId = "dm",   TeamPlay = false, Mutator = "insta" },
            new() { Id = "instateam",     Name = "insta team",    ObjectiveId = "dm",   TeamPlay = true,  Mutator = "insta" },
            new() { Id = "effic",         Name = "efficiency",    ObjectiveId = "dm",   TeamPlay = false, Mutator = "effic" },
            new() { Id = "efficteam",     Name = "effic team",    ObjectiveId = "dm",   TeamPlay = true,  Mutator = "effic" },
            new() { Id = "tactics",       Name = "tactics",       ObjectiveId = "dm",   TeamPlay = false, Mutator = "tactics" },
            new() { Id = "tactics_team",  Name = "tac team",      ObjectiveId = "dm",   TeamPlay = true,  Mutator = "tactics" },
            new() { Id = "ctf",           Name = "ctf",           ObjectiveId = "ctf",  TeamPlay = true,  Mutator = "" },
            new() { Id = "insta_ctf",     Name = "insta ctf",     ObjectiveId = "ctf",  TeamPlay = true,  Mutator = "insta" },
            new() { Id = "effic_ctf",     Name = "effic ctf",     ObjectiveId = "ctf",  TeamPlay = true,  Mutator = "effic" },
        };

        foreach (var mode in Defaults)
        {
            mode.Bake();
        }
    }

    private static GameModeInfo[]? _all;

    /// <summary>
    /// The active catalog, loaded following the 3-tier cascade.
    /// </summary>
    public static GameModeInfo[] All => _all ??= Load();

    /// <summary>Look up a mode by its stable id or name, or null when unknown.</summary>
    public static GameModeInfo? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        foreach (var mode in All)
        {
            if (string.Equals(mode.Id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode.Name, id, StringComparison.OrdinalIgnoreCase))
                return mode;
        }

        string norm = Normalize(id);
        foreach (var mode in All)
        {
            if (Normalize(mode.Id) == norm || Normalize(mode.Name) == norm)
                return mode;
        }

        if (norm is "instagib" or "insta")
            return FindById("insta");
        if (norm is "efficiency" or "effic")
            return FindById("effic");
        if (norm is "instateam" or "instagibteam")
            return FindById("instateam");
        if (norm is "efficteam" or "efficiencyteam")
            return FindById("efficteam");
        if (norm is "tacteam" or "tacticsteam" or "tactics_team")
            return FindById("tactics_team");

        return null;
    }

    private static GameModeInfo? FindById(string id)
    {
        foreach (var mode in All)
        {
            if (mode.Id == id) return mode;
        }
        return null;
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Look up a mode by its stable id; throws when unknown.</summary>
    public static GameModeInfo Get(string id)
        => TryGet(id) ?? throw new KeyNotFoundException($"unknown game mode '{id}'");

    /// <summary>Default fallback game mode (<c>ffa</c>).</summary>
    public static GameModeInfo Default => Get("ffa");

    /// <summary>Replace the cached catalog (test/mod seam).</summary>
    public static void Override(GameModeInfo[] modes)
    {
        foreach (var m in modes) m.Bake();
        _all = modes;
    }

    /// <summary>Reload active game mode definitions following the 3-tier cascade.</summary>
    public static void Reload(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        _all = Load(dir, modDir, userDir);
    }

    /// <summary>
    /// Load game mode definitions following the ADR 0010 3-tier cascade.
    /// </summary>
    public static GameModeInfo[] Load(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        var modes = new List<GameModeInfo>(Defaults);
        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < modes.Count; i++) byId[modes[i].Id] = i;

        ApplyDirectory(dir, modes, byId);
        ApplyDirectory(modDir, modes, byId);
        ApplyDirectory(userDir, modes, byId);

        foreach (var mode in modes)
        {
            mode.Bake();
        }

        return modes.ToArray();
    }

    private static void ApplyDirectory(string? dir, List<GameModeInfo> modes, Dictionary<string, int> byId)
    {
        if (string.IsNullOrEmpty(dir)) return;

        using var dirAccess = DirAccess.Open(dir);
        if (dirAccess == null) return;

        string[] files = dirAccess.GetFiles();
        Array.Sort(files, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            string resource = file.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)
                ? file[..^".remap".Length]
                : file;
            if (!resource.EndsWith(".tres", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(resource)) continue;

            string fullPath = $"{dir.TrimEnd('/')}/{resource}";
            var loaded = ResourceLoader.Load(fullPath) as GameModeInfo;
            if (loaded == null || string.IsNullOrEmpty(loaded.Id)) continue;
            if (byId.TryGetValue(loaded.Id, out int at)) modes[at] = loaded;
            else { byId[loaded.Id] = modes.Count; modes.Add(loaded); }
        }
    }

    /// <summary>
    /// Engine <c>cmode ? cmode->getspawngroup(d) : 0</c> (fpsgame/fps.cpp:327, ctf.h:1052).
    /// In CTF, returns 1 for "good" team or 2 for "evil" team.
    /// In Hold flag placement or non-team/FFA modes, returns 0.
    /// </summary>
    public static int GetSpawnGroup(GameModeInfo? mode, int team)
    {
        if (mode == null || !mode.TeamPlay) return 0;
        if (Ctf.FlagManager.IsHoldMode(mode)) return 0;
        if (mode.Objective is Objectives.FlagObjectiveResource || mode.ObjectiveId == "ctf")
        {
            return team is 1 or 2 ? team : 0;
        }
        return 0;
    }
}
