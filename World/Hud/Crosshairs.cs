namespace SauerbratenRevival.World.Hud;

/// <summary>
/// The engine's crosshairs table (rendergl.cpp <c>crosshairs[MAXCROSSHAIRS]</c>).
/// Delegates to <see cref="CrosshairCatalog"/> per ADR 0010.
/// </summary>
public static class Crosshairs
{
    /// <summary>rendergl.cpp / fps.cpp built-in defaults.</summary>
    public static CrosshairInfo[] Defaults => CrosshairCatalog.Defaults;

    /// <summary>The active crosshairs catalogue.</summary>
    public static CrosshairInfo[] All => CrosshairCatalog.All;

    /// <summary>The total number of currently configured crosshairs.</summary>
    public static int Count => CrosshairCatalog.All.Length;

    /// <summary>Clamped lookup.</summary>
    public static CrosshairInfo Get(int index) => CrosshairCatalog.Get(index);

    /// <summary>Lookup by typed <see cref="CrosshairType"/>.</summary>
    public static CrosshairInfo Get(CrosshairType id) => CrosshairCatalog.Get(id);

    /// <summary>Lookup by index, or null when out of range.</summary>
    public static CrosshairInfo? TryGet(int index) => CrosshairCatalog.TryGet(index);

    /// <summary>Replace the active catalogue (test/mod seam).</summary>
    public static void Override(CrosshairInfo[] crosshairs) => CrosshairCatalog.Override(crosshairs);

    /// <summary>Reload the catalogue from the base directory and any overrides.</summary>
    public static void Reload(
        string dir = CrosshairCatalog.ResourceDir,
        string? modDir = CrosshairCatalog.ModOverrideDir,
        string? userDir = CrosshairCatalog.UserOverrideDir) =>
        CrosshairCatalog.Reload(dir, modDir, userDir);

    /// <summary>Load crosshairs following the 3-tier cascade.</summary>
    public static CrosshairInfo[] Load(
        string dir = CrosshairCatalog.ResourceDir,
        string? modDir = CrosshairCatalog.ModOverrideDir,
        string? userDir = CrosshairCatalog.UserOverrideDir) =>
        CrosshairCatalog.Load(dir, modDir, userDir);
}
