namespace SauerbratenRevival.World.Models;

/// <summary>
/// The engine's fixed player-model table (render.cpp <c>playermodels[5]</c>).
/// Delegates to <see cref="PlayerModelCatalog"/> per ADR 0010.
/// </summary>
public static class Playermodels
{
    /// <summary>render.cpp <c>playermodels[5]</c> built-in defaults.</summary>
    public static PlayerModelInfo[] Defaults => PlayerModelCatalog.Defaults;

    /// <summary>The active player models catalogue.</summary>
    public static PlayerModelInfo[] All => PlayerModelCatalog.All;

    /// <summary>The total number of currently configured player models.</summary>
    public static int Count => PlayerModelCatalog.Count;

    /// <summary>Clamped lookup (<c>getplayermodelinfo(int)</c>).</summary>
    public static PlayerModelInfo Get(int index) => PlayerModelCatalog.Get(index);

    /// <summary>Lookup by typed <see cref="PlayerModelId"/>.</summary>
    public static PlayerModelInfo Get(PlayerModelId id) => PlayerModelCatalog.Get(id);

    /// <summary>Look up a player model by index, or null when out of range.</summary>
    public static PlayerModelInfo? TryGet(int index) => PlayerModelCatalog.TryGet(index);

    /// <summary>render.cpp <c>chooserandomplayermodel</c>.</summary>
    public static int ChooseRandom(int seed) => PlayerModelCatalog.ChooseRandom(seed);

    /// <summary>Replace the active catalogue (test/mod seam).</summary>
    public static void Override(PlayerModelInfo[] models) => PlayerModelCatalog.Override(models);

    /// <summary>Reload the catalogue from the base directory and any overrides.</summary>
    public static void Reload(
        string dir = PlayerModelCatalog.ResourceDir,
        string? modDir = PlayerModelCatalog.ModOverrideDir,
        string? userDir = PlayerModelCatalog.UserOverrideDir) =>
        PlayerModelCatalog.Reload(dir, modDir, userDir);

    /// <summary>Load player models following the 3-tier cascade.</summary>
    public static PlayerModelInfo[] Load(
        string dir = PlayerModelCatalog.ResourceDir,
        string? modDir = PlayerModelCatalog.ModOverrideDir,
        string? userDir = PlayerModelCatalog.UserOverrideDir) =>
        PlayerModelCatalog.Load(dir, modDir, userDir);
}
