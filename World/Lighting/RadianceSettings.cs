using Godot;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// Lighting quality mode for the radiance hints subsystem (docs/radiance-hints.md §8 Stage 5).
/// </summary>
public enum RadianceQualityMode
{
    /// <summary>
    /// Simple static ambient lighting: avoids probe raycasts and 3D texture lookups,
    /// shading geometry with constant ambient color. Suitable for low-end hardware or fast editing.
    /// </summary>
    StaticAmbient = 0,

    /// <summary>
    /// Full L1 SH radiance hints probe volume (sky, sun, direct entity lights, indirect bounce).
    /// </summary>
    RadianceHints = 1,
}

/// <summary>
/// Quality and performance knobs for <see cref="RadianceVolume"/> (docs/radiance-hints.md §8 Stage 5).
/// Allows tuning ray counts, temporal slicing / frame budget, ray travel distance,
/// grid resolution, and global quality mode.
/// </summary>
public sealed class RadianceSettings
{
    /// <summary>Global quality switch: full radiance hints volume vs simple static ambient color.</summary>
    public RadianceQualityMode Mode { get; set; } = RadianceQualityMode.RadianceHints;

    /// <summary>Number of hemisphere rays cast per probe for sky &amp; bounce passes (default 16).</summary>
    public int SkyRays { get; set; } = 16;

    /// <summary>Maximum dirty probes rebaked per frame during incremental updates (default 256).</summary>
    public int BudgetPerFrame { get; set; } = 256;

    /// <summary>
    /// Maximum raycast travel distance (in Sauerbraten units).
    /// If null or &lt;= 0, defaults to 1.5 * grid extent.
    /// Clamping this distance speeds up ray traversal on large outdoor maps.
    /// </summary>
    public float? MaxRayDistance { get; set; } = null;

    /// <summary>Probe grid resolution per axis (default 32).</summary>
    public int Resolution { get; set; } = RadianceGrid.DefaultResolution;

    /// <summary>Creates a default quality configuration (32³, 16 sky rays, 256 budget, auto distance).</summary>
    public static RadianceSettings Default => new();

    /// <summary>Creates a static ambient configuration (no raycasts, minimal overhead).</summary>
    public static RadianceSettings StaticAmbient => new()
    {
        Mode = RadianceQualityMode.StaticAmbient,
    };

    /// <summary>Creates a low-quality configuration (fewer rays, smaller frame budget, clamped ray distance).</summary>
    public static RadianceSettings Low => new()
    {
        Mode = RadianceQualityMode.RadianceHints,
        SkyRays = 4,
        BudgetPerFrame = 128,
        MaxRayDistance = 256f,
        Resolution = 32,
    };

    /// <summary>Creates a medium-quality configuration (8 rays, 256 budget).</summary>
    public static RadianceSettings Medium => new()
    {
        Mode = RadianceQualityMode.RadianceHints,
        SkyRays = 8,
        BudgetPerFrame = 256,
        Resolution = 32,
    };

    /// <summary>Creates a high-quality configuration (16 rays, 256 budget).</summary>
    public static RadianceSettings High => new()
    {
        Mode = RadianceQualityMode.RadianceHints,
        SkyRays = 16,
        BudgetPerFrame = 256,
        Resolution = 32,
    };

    /// <summary>Creates an ultra-quality configuration (32 rays, 512 budget, 48 resolution).</summary>
    public static RadianceSettings Ultra => new()
    {
        Mode = RadianceQualityMode.RadianceHints,
        SkyRays = 32,
        BudgetPerFrame = 512,
        Resolution = 48,
    };

    /// <summary>Creates a deep copy of these settings.</summary>
    public RadianceSettings Clone() => new()
    {
        Mode = Mode,
        SkyRays = SkyRays,
        BudgetPerFrame = BudgetPerFrame,
        MaxRayDistance = MaxRayDistance,
        Resolution = Resolution,
    };
}
