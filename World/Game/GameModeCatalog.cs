using Godot;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// The active game-mode definitions, loaded from the <c>.tres</c> files under
/// <see cref="ResourceDir"/> — data-first content, editable in the Godot inspector
/// and extendable by a mod (a mod pack can mount its own modes into the directory) —
/// with a built-in <see cref="Defaults"/> fallback so the four MVP modes always
/// exist even without files.
///
/// This mirrors <c>WeaponCatalog</c>: modes are <see cref="GameModeInfo"/> Resources
/// keyed by a stable string <see cref="GameModeInfo.Id"/>, replacing the engine's
/// integer <c>gamemode</c> index.
/// </summary>
public static class GameModeCatalog
{
    /// <summary>Directory scanned for authored per-mode <c>.tres</c> files.</summary>
    public const string ResourceDir = "res://resources/gamemodes";

    /// <summary>
    /// Built-in default modes. Values are the engine <c>gamemodes[]</c> flag sets for
    /// the four MVP modes (fpsgame/game.h:104); the ids are ours.
    /// </summary>
    public static readonly GameModeInfo[] Defaults =
    {
        new() { Id = "ffa",       Name = "ffa",       Flags = ModeFlags.Lobby },
        new() { Id = "coop_edit", Name = "coop edit", Flags = ModeFlags.Edit },
        new() { Id = "insta_ctf", Name = "insta ctf", Flags = ModeFlags.NoItems | ModeFlags.Insta | ModeFlags.Ctf | ModeFlags.Team },
        new() { Id = "effic_ctf", Name = "effic ctf", Flags = ModeFlags.NoItems | ModeFlags.Efficiency | ModeFlags.Ctf | ModeFlags.Team },
    };

    private static GameModeInfo[]? _all;

    /// <summary>
    /// The active catalog, loaded from <see cref="ResourceDir"/> (falling back to
    /// <see cref="Defaults"/>). Cached after first access; use <see cref="Override"/>
    /// to replace it (mods/tests).
    /// </summary>
    public static GameModeInfo[] All => _all ??= Load();

    /// <summary>Look up a mode by its stable id, or null when unknown.</summary>
    public static GameModeInfo? TryGet(string id)
    {
        foreach (var mode in All)
            if (mode.Id == id) return mode;
        return null;
    }

    /// <summary>Look up a mode by its stable id; throws when unknown.</summary>
    public static GameModeInfo Get(string id)
        => TryGet(id) ?? throw new System.Collections.Generic.KeyNotFoundException($"unknown game mode '{id}'");

    /// <summary>Replace the cached catalog (test/mod seam).</summary>
    public static void Override(GameModeInfo[] modes) => _all = modes;

    /// <summary>
    /// Merge the <c>.tres</c> files in <paramref name="dir"/> onto <see cref="Defaults"/>:
    /// a file whose <see cref="GameModeInfo.Id"/> matches a default replaces it,
    /// anything else is added (so a mod can introduce new modes). New files are
    /// applied in ordinal filename order for determinism; files that are missing or
    /// do not deserialize to a <see cref="GameModeInfo"/> are ignored. Pure function
    /// of the filesystem, so a mod can point at its own directory.
    /// </summary>
    public static GameModeInfo[] Load(string dir = ResourceDir)
    {
        var modes = new System.Collections.Generic.List<GameModeInfo>(Defaults);
        var byId = new System.Collections.Generic.Dictionary<string, int>();
        for (int i = 0; i < modes.Count; i++) byId[modes[i].Id] = i;

        using var dirAccess = DirAccess.Open(dir);
        if (dirAccess == null) return modes.ToArray();

        string[] files = dirAccess.GetFiles();
        System.Array.Sort(files, System.StringComparer.Ordinal);
        foreach (string file in files)
        {
            if (!file.EndsWith(".tres")) continue;
            var loaded = ResourceLoader.Load<GameModeInfo>($"{dir.TrimEnd('/')}/{file}");
            if (loaded == null || string.IsNullOrEmpty(loaded.Id)) continue;
            if (byId.TryGetValue(loaded.Id, out int at)) modes[at] = loaded;
            else { byId[loaded.Id] = modes.Count; modes.Add(loaded); }
        }
        return modes.ToArray();
    }
}
