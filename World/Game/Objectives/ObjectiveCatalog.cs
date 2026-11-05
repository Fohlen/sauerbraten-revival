using System;
using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Game.Objectives;

/// <summary>
/// Catalog of active match objective definitions, loaded following the ADR 0010 3-tier cascade:
/// 1. Core base directory (<see cref="ResourceDir"/>), falling back to <see cref="Defaults"/>
/// 2. Mod overrides (<see cref="ModOverrideDir"/>)
/// 3. User overrides (<see cref="UserOverrideDir"/>)
/// </summary>
public static class ObjectiveCatalog
{
    public const string ResourceDir = "res://resources/objectives";
    public const string ModOverrideDir = "res://resources/objectives/overrides";
    public const string UserOverrideDir = "user://objectives/overrides";

    public static readonly ModeObjectiveResource[] Defaults =
    {
        new DeathmatchObjectiveResource { Id = "dm", Name = "deathmatch", DefaultTeamPlay = false, DefaultMapPool = MapListId.Dm, DefaultSpawnArmour = 25 },
        new EditObjectiveResource { Id = "edit", Name = "coop edit", DefaultTeamPlay = false, IsEditMode = true, DefaultMapPool = MapListId.Dm, DefaultSpawnArmour = 25 },
        new FlagObjectiveResource
        {
            Id = "ctf",
            Name = "ctf",
            DefaultTeamPlay = true,
            DefaultMapPool = MapListId.Ctf,
            DefaultSpawnArmour = 50,
            Placement = FlagPlacementMode.MapTeamFlags,
            ScoreRule = FlagScoreRule.RelayToFriendlyBase,
            ReturnRule = FlagReturnRule.TouchDropped,
            ResetDelayMillis = 10000,
        },
    };

    private static ModeObjectiveResource[]? _all;

    public static ModeObjectiveResource[] All => _all ??= Load();

    public static ModeObjectiveResource? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        foreach (var obj in All)
        {
            if (string.Equals(obj.Id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(obj.Name, id, StringComparison.OrdinalIgnoreCase))
                return obj;
        }

        return null;
    }

    public static ModeObjectiveResource Get(string id) =>
        TryGet(id) ?? throw new KeyNotFoundException($"unknown objective '{id}'");

    public static void Override(ModeObjectiveResource[] objectives) => _all = objectives;

    public static void Reload(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        _all = Load(dir, modDir, userDir);
    }

    public static ModeObjectiveResource[] Load(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        var list = new List<ModeObjectiveResource>(Defaults);
        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Count; i++) byId[list[i].Id] = i;

        ApplyDirectory(dir, list, byId);
        ApplyDirectory(modDir, list, byId);
        ApplyDirectory(userDir, list, byId);

        return list.ToArray();
    }

    private static void ApplyDirectory(string? dir, List<ModeObjectiveResource> list, Dictionary<string, int> byId)
    {
        if (string.IsNullOrEmpty(dir)) return;

        using var da = DirAccess.Open(dir);
        if (da == null) return;

        string[] files = da.GetFiles();
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
            if (ResourceLoader.Load(fullPath) is ModeObjectiveResource loaded && !string.IsNullOrEmpty(loaded.Id))
            {
                if (byId.TryGetValue(loaded.Id, out int at))
                    list[at] = loaded;
                else
                {
                    byId[loaded.Id] = list.Count;
                    list.Add(loaded);
                }
            }
        }
    }
}
