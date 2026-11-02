using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Content;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Models;

/// <summary>
/// Persistent cache for parsed <see cref="PackedScene"/> assets and shared <see cref="Material"/>
/// instances (ADR 0011). Holds strong references so Godot's resource garbage collection never
/// purges loaded models or materials during match gameplay, eliminating in-flight disk reads and
/// GLB parsing stutter.
/// </summary>
public static class ModelCache
{
    private static readonly ConcurrentDictionary<string, PackedScene> _scenes = new();
    private static readonly ConcurrentDictionary<string, Material> _skinCache = new();

    /// <summary>Shared material cache used across model instances (e.g. <see cref="ModelSkin"/>).</summary>
    public static IDictionary<string, Material> SkinCache => _skinCache;

    /// <summary>
    /// Gets a cached <see cref="PackedScene"/> if already preloaded, or loads and caches it from <paramref name="resPath"/>.
    /// Returns null if the resource does not exist or fails to load.
    /// </summary>
    public static PackedScene? GetOrLoadScene(string? resPath)
    {
        if (string.IsNullOrEmpty(resPath)) return null;
        if (_scenes.TryGetValue(resPath, out var cached)) return cached;

        if (!ResourceLoader.Exists(resPath)) return null;
        var loaded = ResourceLoader.Load<PackedScene>(resPath);
        if (loaded != null)
        {
            _scenes[resPath] = loaded;
        }
        return loaded;
    }

    /// <summary>Returns true if the given resource path is already parsed and retained in memory.</summary>
    public static bool HasScene(string? resPath) =>
        !string.IsNullOrEmpty(resPath) && _scenes.ContainsKey(resPath);

    /// <summary>
    /// Explicitly preloads a glTF / scene into the cache. Returns true if loaded or already cached.
    /// </summary>
    public static bool PreloadScene(string? resPath) => GetOrLoadScene(resPath) != null;

    /// <summary>
    /// Preloads the glTF scene for a model definition and all of its linked parts (e.g. hudguns).
    /// </summary>
    public static int PreloadModel(ModelInfo? info)
    {
        if (info == null) return 0;
        int count = 0;
        if (!string.IsNullOrEmpty(info.GlbPath) && PreloadScene(info.GlbPath))
            count++;

        if (info.Parts != null)
        {
            for (int i = 0; i < info.Parts.Count; i++)
            {
                var part = info.Parts[i];
                if (!string.IsNullOrEmpty(part.GlbPath) && PreloadScene(part.GlbPath))
                    count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Preloads a weapon's models (both first-person hudgun and third-person vwep) and their skins.
    /// </summary>
    public static void PreloadWeapon(
        string gunFile,
        string hudgunsDir,
        IModelCatalog? catalog,
        ITextureSource? textures)
    {
        if (catalog == null) return;

        // 1. Hudgun model (e.g. "mrfixit/hudguns/rifle")
        string hudgunPath = $"{hudgunsDir.TrimEnd('/')}/{gunFile}";
        if (catalog.Get(hudgunPath) is { } hudgunInfo)
        {
            PreloadModel(hudgunInfo);
            if (textures != null)
            {
                ModelSkin.Build(hudgunInfo, textures, _skinCache);
                if (hudgunInfo.Parts != null)
                {
                    foreach (var part in hudgunInfo.Parts)
                        ModelSkin.Build(part, textures, _skinCache);
                }
            }
        }

        // 2. Third-person view weapon (e.g. "vwep/rifle")
        string vwepPath = $"vwep/{gunFile}";
        if (catalog.Get(vwepPath) is { } vwepInfo)
        {
            PreloadModel(vwepInfo);
            if (textures != null)
                ModelSkin.Build(vwepInfo, textures, _skinCache);
        }
    }

    /// <summary>
    /// Preloads the player body, all 7 first-person hudguns, and all 7 third-person viewweapons
    /// for the given player model index (render.cpp:520-539).
    /// </summary>
    public static void PreloadPlayermodel(
        int modelIndex,
        IModelCatalog? catalog,
        ITextureSource? textures,
        int team = 0)
    {
        if (catalog == null) return;
        var info = Playermodels.Get(modelIndex);

        // Body model
        string bodyPath = info.Body(team);
        if (catalog.Get(bodyPath) is { } bodyInfo)
        {
            PreloadModel(bodyInfo);
            if (textures != null)
                ModelSkin.Build(bodyInfo, textures, _skinCache);
        }

        // All 7 weapons: fist, shotg, chaing, rocket, rifle, gl, pistol
        for (int g = Guns.First; g <= Guns.Last; g++)
        {
            var weapon = WeaponCatalog.Get((Gun)g);
            if (string.IsNullOrEmpty(weapon.File)) continue;
            PreloadWeapon(weapon.File, info.Hudguns, catalog, textures);
        }
    }

    /// <summary>
    /// Preloads dynamic projectile scenes (grenades and rockets) and prepares their materials.
    /// </summary>
    public static void PreloadProjectiles(ITextureSource? textures)
    {
        string grenadeGlb = ContentRoots.ProjectPath(ContentKind.Model, "projectiles_grenade.glb");
        PreloadScene(grenadeGlb);

        string rocketGlb = ContentRoots.ProjectPath(ContentKind.Model, "projectiles_rocket.glb");
        PreloadScene(rocketGlb);

        if (textures != null)
        {
            GetGrenadeMaterial(textures);
            GetRocketMaterial(textures);
        }
    }

    /// <summary>
    /// Cached StandardMaterial3D for grenades (BounceBody).
    /// </summary>
    public static Material GetGrenadeMaterial(ITextureSource textures)
    {
        const string key = "proj:grenade";
        if (_skinCache.TryGetValue(key, out var mat)) return mat;

        var standard = new StandardMaterial3D
        {
            Roughness = 0.3f,
            Metallic = 0.6f,
        };
        if (textures.Get("models/projectiles/grenade/skin.jpg") is { } skin)
        {
            standard.AlbedoTexture = skin;
            standard.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
        }
        else
        {
            standard.AlbedoColor = new Color(0.21f, 0.31f, 0.33f); // r6889 skin.jpg average
        }

        _skinCache[key] = standard;
        return standard;
    }

    /// <summary>
    /// Cached StandardMaterial3D for rockets (WeaponEffectsRenderer).
    /// </summary>
    public static Material GetRocketMaterial(ITextureSource textures)
    {
        const string key = "proj:rocket";
        if (_skinCache.TryGetValue(key, out var mat)) return mat;

        var standard = new StandardMaterial3D
        {
            Roughness = 0.3f,
            Metallic = 0.6f,
        };
        if (textures.Get("models/projectiles/rocket/skin.jpg") is { } skin)
        {
            standard.AlbedoTexture = skin;
            standard.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
        }
        else
        {
            standard.AlbedoColor = new Color(0.5f, 0.5f, 0.5f);
        }

        _skinCache[key] = standard;
        return standard;
    }

    /// <summary>
    /// Clears cached scenes and materials (for test teardown or memory resets).
    /// </summary>
    public static void Clear()
    {
        _scenes.Clear();
        _skinCache.Clear();
    }

    /// <summary>Total number of cached scenes currently held in memory.</summary>
    public static int CachedSceneCount => _scenes.Count;

    /// <summary>Total number of cached materials currently held in memory.</summary>
    public static int CachedMaterialCount => _skinCache.Count;
}
