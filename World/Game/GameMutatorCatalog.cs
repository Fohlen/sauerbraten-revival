using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// Catalog of game mutator definitions, loaded following the ADR 0010 3-tier cascade:
/// 1. Core base directory (<see cref="ResourceDir"/>), falling back to <see cref="Defaults"/>
/// 2. Mod overrides (<see cref="ModOverrideDir"/>)
/// 3. User overrides (<see cref="UserOverrideDir"/>)
/// </summary>
public static class GameMutatorCatalog
{
    public const string ResourceDir = "res://resources/mutators";
    public const string ModOverrideDir = "res://resources/mutators/overrides";
    public const string UserOverrideDir = "user://mutators/overrides";

    public static readonly GameMutator[] Defaults =
    {
        new()
        {
            Id = "insta",
            Name = "instagib",
            SpawnHealth = 1,
            MaxHealth = 100,
            SpawnArmour = 0,
            SpawnArmourType = ArmourType.Blue,
            SpawnGun = Gun.Rifle,
            SpawnAmmo = new[] { 1, 0, 0, 0, 100, 0, 0 },
            RandomGunsCount = 0,
            RandomGunsAmmoScale = 1,
            AllowedItems = ItemSpawnMask.None,
        },
        new()
        {
            Id = "effic",
            Name = "efficiency",
            SpawnHealth = 100,
            MaxHealth = 100,
            SpawnArmour = 100,
            SpawnArmourType = ArmourType.Green,
            SpawnGun = Gun.Chaingun,
            SpawnAmmo = new[] { 1, 20, 20, 10, 10, 20, 0 },
            RandomGunsCount = 0,
            RandomGunsAmmoScale = 1,
            AllowedItems = ItemSpawnMask.None,
        },
        new()
        {
            Id = "tactics",
            Name = "tactics",
            SpawnHealth = 100,
            MaxHealth = 100,
            SpawnArmour = 100,
            SpawnArmourType = ArmourType.Green,
            SpawnGun = Gun.Pistol,
            SpawnAmmo = new[] { 1, 0, 0, 0, 0, 1, 40 },
            RandomGunsCount = 2,
            RandomGunsAmmoScale = 2,
            AllowedItems = ItemSpawnMask.None,
        },
        new()
        {
            Id = "capture_loadout",
            Name = "capture loadout",
            SpawnHealth = 100,
            MaxHealth = 100,
            SpawnArmour = 100,
            SpawnArmourType = ArmourType.Green,
            SpawnGun = Gun.Pistol,
            SpawnAmmo = new[] { 1, 0, 0, 0, 0, 0, 40 },
            RandomGunsCount = 2,
            RandomGunsAmmoScale = 1,
            AllowedItems = ItemSpawnMask.NonAmmo,
        },
        new()
        {
            Id = "regen_loadout",
            Name = "regen loadout",
            SpawnHealth = 100,
            MaxHealth = 100,
            SpawnArmour = 25,
            SpawnArmourType = ArmourType.Blue,
            SpawnGun = Gun.Pistol,
            SpawnAmmo = new[] { 1, 0, 0, 0, 0, 1, 40 },
            RandomGunsCount = 0,
            RandomGunsAmmoScale = 1,
            AllowedItems = ItemSpawnMask.None,
        },
        new()
        {
            Id = "rocket_arena",
            Name = "rocket arena",
            SpawnHealth = 100,
            MaxHealth = 100,
            SpawnArmour = 100,
            SpawnArmourType = ArmourType.Green,
            SpawnGun = Gun.RocketLauncher,
            SpawnAmmo = new[] { 1, 0, 0, 100, 0, 0, 0 },
            InfiniteAmmo = true,
            AllowedItems = ItemSpawnMask.None,
        },
    };

    private static GameMutator[]? _all;

    public static GameMutator[] All => _all ??= Load();

    public static GameMutator? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        foreach (var mut in All)
        {
            if (string.Equals(mut.Id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mut.Name, id, StringComparison.OrdinalIgnoreCase))
                return mut;
        }

        return null;
    }

    public static GameMutator Get(string id) =>
        TryGet(id) ?? throw new KeyNotFoundException($"unknown game mutator '{id}'");

    public static void Override(GameMutator[] mutators) => _all = mutators;

    public static void Reload(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        _all = Load(dir, modDir, userDir);
    }

    public static GameMutator[] Load(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        var list = new List<GameMutator>(Defaults);
        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Count; i++) byId[list[i].Id] = i;

        ApplyDirectory(dir, list, byId);
        ApplyDirectory(modDir, list, byId);
        ApplyDirectory(userDir, list, byId);

        return list.ToArray();
    }

    private static void ApplyDirectory(string? dir, List<GameMutator> list, Dictionary<string, int> byId)
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
            if (ResourceLoader.Load(fullPath) is GameMutator loaded && !string.IsNullOrEmpty(loaded.Id))
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
