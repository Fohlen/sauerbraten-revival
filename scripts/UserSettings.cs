using Godot;
using SauerbratenRevival.World.Lighting;
using SauerbratenRevival.World.Weapons;

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

    private const string LightingModeKey = "lighting_mode";
    private const string SoundEnabledKey = "sound_enabled";
    private const string SoundVolumeKey = "sound_volume";
    private const string SensitivityKey = "sensitivity";
    private const string SensitivityScaleKey = "sensitivity_scale";
    private const string MouseAccelKey = "mouse_accel";
    private const string InvertMouseKey = "invert_mouse";
    private const string FovKey = "fov";

    // Engine value ranges (rendergl.cpp VARPs).
    private const float MinSensitivity = 0.001f;
    private const float MaxSensitivity = 1000f;
    private const float MaxMouseAccel = 1000f;
    private const float MinFov = 10f;
    private const float MaxFov = 150f;

    // Engine defaults (rendergl.cpp).
    private const float DefaultSensitivity = 3f;
    private const float DefaultSensitivityScale = 1f;
    private const float DefaultFov = 100f;

    private const string StaticAmbientToken = "static_ambient";
    private const string RadianceHintsToken = "radiance_hints";

    /// <summary>World lighting quality mode (docs/adr/0002-radiance-hints.md §7).</summary>
    public RadianceQualityMode LightingMode { get; init; } = RadianceQualityMode.RadianceHints;

    /// <summary>Whether game audio plays at all.</summary>
    public bool SoundEnabled { get; init; } = true;

    /// <summary>Master sound volume in the engine's <c>soundvol</c> range 0..255.</summary>
    public int SoundVolume { get; init; } = GameAudio.MaxSoundVol;

    /// <summary>Original <c>sensitivity</c> (rendergl.cpp; default 3).</summary>
    public float Sensitivity { get; init; } = DefaultSensitivity;

    /// <summary>Original <c>sensitivityscale</c> (rendergl.cpp; default 1).</summary>
    public float SensitivityScale { get; init; } = DefaultSensitivityScale;

    /// <summary>Original <c>mouseaccel</c> (rendergl.cpp; 0 disables).</summary>
    public float MouseAccel { get; init; } = 0f;

    /// <summary>Original <c>invmouse</c> (rendergl.cpp): invert the vertical look axis.</summary>
    public bool InvertMouse { get; init; } = false;

    /// <summary>Original <c>fov</c>, horizontal degrees (rendergl.cpp; default 100).</summary>
    public float Fov { get; init; } = DefaultFov;

    /// <summary>Read the settings file; a missing file or key keeps the field defaults.</summary>
    public static UserSettings Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) != Error.Ok) return new UserSettings();
        return new UserSettings
        {
            LightingMode = ParseLightingMode(cfg.GetValue(Section, LightingModeKey, "").AsString()),
            SoundEnabled = cfg.GetValue(Section, SoundEnabledKey, true).AsBool(),
            SoundVolume = Mathf.Clamp(cfg.GetValue(Section, SoundVolumeKey, GameAudio.MaxSoundVol).AsInt32(), 0, GameAudio.MaxSoundVol),
            Sensitivity = Mathf.Clamp(cfg.GetValue(Section, SensitivityKey, DefaultSensitivity).AsSingle(), MinSensitivity, MaxSensitivity),
            SensitivityScale = Mathf.Clamp(cfg.GetValue(Section, SensitivityScaleKey, DefaultSensitivityScale).AsSingle(), MinSensitivity, MaxSensitivity),
            MouseAccel = Mathf.Clamp(cfg.GetValue(Section, MouseAccelKey, 0f).AsSingle(), 0f, MaxMouseAccel),
            InvertMouse = cfg.GetValue(Section, InvertMouseKey, false).AsBool(),
            Fov = Mathf.Clamp(cfg.GetValue(Section, FovKey, DefaultFov).AsSingle(), MinFov, MaxFov),
        };
    }

    /// <summary>
    /// Write the record to <see cref="SettingsPath"/>. The file is rewritten from
    /// scratch, so keys from an older layout are dropped rather than carried over.
    /// </summary>
    public void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue(Section, LightingModeKey, LightingModeToken(LightingMode));
        cfg.SetValue(Section, SoundEnabledKey, SoundEnabled);
        cfg.SetValue(Section, SoundVolumeKey, Mathf.Clamp(SoundVolume, 0, GameAudio.MaxSoundVol));
        cfg.SetValue(Section, SensitivityKey, Mathf.Clamp(Sensitivity, MinSensitivity, MaxSensitivity));
        cfg.SetValue(Section, SensitivityScaleKey, Mathf.Clamp(SensitivityScale, MinSensitivity, MaxSensitivity));
        cfg.SetValue(Section, MouseAccelKey, Mathf.Clamp(MouseAccel, 0f, MaxMouseAccel));
        cfg.SetValue(Section, InvertMouseKey, InvertMouse);
        cfg.SetValue(Section, FovKey, Mathf.Clamp(Fov, MinFov, MaxFov));
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
