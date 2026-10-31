using System;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Hud;

/// <summary>
/// The engine's crosshair table (engine/rendergl.cpp:2083 <c>crosshairs[MAXCROSSHAIRS]</c>, r6889)
/// and default crosshair mappings (fpsgame/fps.cpp:1110).
///
/// Follows ADR 0010:
/// - Core catalog: <c>res://resources/crosshairs/&lt;name&gt;.tres</c>
/// - Mod overrides: <c>res://resources/crosshairs/overrides/</c>
/// - User overrides: <c>user://crosshairs/overrides/</c>
/// - Hot path lookup: O(1) direct integer indexing via <see cref="All"/> or <see cref="Get(CrosshairType)"/>.
/// </summary>
public static class CrosshairCatalog
{
    /// <summary>Number of standard engine crosshair types (<c>Default</c>, <c>Teammate</c>, <c>Hit</c>).</summary>
    public const int Count = 3;

    /// <summary>Directory holding authored per-crosshair <c>.tres</c> files.</summary>
    public const string ResourceDir = "res://resources/crosshairs";

    /// <summary>Directory scanned for modded crosshair overrides.</summary>
    public const string ModOverrideDir = "res://resources/crosshairs/overrides";

    /// <summary>Directory scanned for user-specific crosshair overrides.</summary>
    public const string UserOverrideDir = "user://crosshairs/overrides";

    /// <summary>
    /// Built-in defaults: the ported <c>defaultcrosshair(int)</c> paths (fps.cpp:1110).
    /// Used as zero-failure fallback when a <c>.tres</c> is absent.
    /// </summary>
    public static readonly CrosshairInfo[] Defaults =
    {
        new()
        {
            Id = CrosshairType.Default,
            Name = "default",
            Path = "crosshairs/default.png",
        },
        new()
        {
            Id = CrosshairType.Teammate,
            Name = "teammate",
            Path = "crosshairs/default_teammate.png",
        },
        new()
        {
            Id = CrosshairType.Hit,
            Name = "hit",
            Path = "crosshairs/default_hit.png",
        },
    };

    private static CrosshairInfo[]? _all;

    /// <summary>
    /// Active crosshair definitions array. Loaded once from <see cref="ResourceDir"/> and overlay directories;
    /// falls back to <see cref="Defaults"/>. Once loaded/reloaded, hot-path lookup is O(1) direct indexing.
    /// </summary>
    public static CrosshairInfo[] All => _all ??= Load();

    /// <summary>Lookup by typed <see cref="CrosshairType"/>.</summary>
    public static CrosshairInfo Get(CrosshairType id) => Get((int)id);

    /// <summary>Clamped lookup matching engine slot indexing.</summary>
    public static CrosshairInfo Get(int index) => All[Math.Clamp(index, 0, All.Length - 1)];

    /// <summary>Lookup crosshair definition by index, or null if out of range.</summary>
    public static CrosshairInfo? TryGet(int index) => index >= 0 && index < All.Length ? All[index] : null;

    /// <summary>Replace active crosshair catalogue (test/mod seam).</summary>
    public static void Override(CrosshairInfo[] crosshairs) => _all = crosshairs;

    /// <summary>
    /// Reload active crosshair definitions from base directory and any override directories.
    /// </summary>
    public static void Reload(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        _all = Load(dir, modDir, userDir);
    }

    /// <summary>
    /// Load crosshair definitions following the ADR 0010 3-tier cascade:
    /// 1. Core base directory (<paramref name="dir"/>), falling back to <see cref="Defaults"/>
    /// 2. Mod overrides (<paramref name="modDir"/>)
    /// 3. User overrides (<paramref name="userDir"/>)
    /// </summary>
    public static CrosshairInfo[] Load(
        string dir = ResourceDir,
        string? modDir = ModOverrideDir,
        string? userDir = UserOverrideDir)
    {
        return ResourceCascade.LoadCascade<CrosshairInfo, CrosshairInfo, CrosshairType>(
            Defaults,
            dir,
            modDir,
            userDir,
            def => def,
            () => new CrosshairInfo());
    }
}
