using Godot;
using SauerbratenRevival.World.Lighting;

namespace SauerbratenRevival;

/// <summary>
/// Typed, in-memory view of the persistent user settings file,
/// <c>user://settings.cfg</c> (docs/vision.md §11: user settings are a Godot
/// <see cref="ConfigFile"/> in <c>user://</c>; no CubeScript).
///
/// The record models the whole <c>[settings]</c> section: <see cref="Load"/> reads
/// it into typed fields and <see cref="Save"/> rewrites the file with
/// <see cref="ConfigFile.Save"/>. Update fields with a <c>with</c> expression and
/// save the copy. Keys use underscores, following the ConfigFile convention.
/// </summary>
public sealed record UserSettings
{
    /// <summary>Config file under <c>user://</c> holding all persistent user settings.</summary>
    public const string SettingsPath = "user://settings.cfg";

    /// <summary>Section holding the general user settings.</summary>
    public const string Section = "settings";

    private const string SauerbratenPathKey = "sauerbraten_path";
    private const string LightingModeKey = "lighting_mode";

    private const string StaticAmbientToken = "static_ambient";
    private const string RadianceHintsToken = "radiance_hints";

    /// <summary>Remembered Sauerbraten install path (converter wizard).</summary>
    public string SauerbratenPath { get; init; } = "";

    /// <summary>World lighting quality mode (docs/adr/0002-radiance-hints.md §7).</summary>
    public RadianceQualityMode LightingMode { get; init; } = RadianceQualityMode.RadianceHints;

    /// <summary>Read the settings file; a missing file or key keeps the field defaults.</summary>
    public static UserSettings Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) != Error.Ok) return new UserSettings();
        return new UserSettings
        {
            SauerbratenPath = cfg.GetValue(Section, SauerbratenPathKey, "").AsString(),
            LightingMode = ParseLightingMode(cfg.GetValue(Section, LightingModeKey, "").AsString()),
        };
    }

    /// <summary>
    /// Write the record to <see cref="SettingsPath"/>. The file is rewritten from
    /// scratch, so keys from an older layout are dropped rather than carried over.
    /// </summary>
    public void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue(Section, SauerbratenPathKey, SauerbratenPath);
        cfg.SetValue(Section, LightingModeKey, LightingModeToken(LightingMode));
        cfg.Save(SettingsPath);
    }

    /// <summary>Config-file token for a mode (stable; used as the saved value).</summary>
    public static string LightingModeToken(RadianceQualityMode mode) =>
        mode == RadianceQualityMode.StaticAmbient ? StaticAmbientToken : RadianceHintsToken;

    /// <summary>Parse a mode token; unknown/empty falls back to <see cref="RadianceQualityMode.RadianceHints"/>.</summary>
    public static RadianceQualityMode ParseLightingMode(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        StaticAmbientToken => RadianceQualityMode.StaticAmbient,
        _ => RadianceQualityMode.RadianceHints,
    };
}
