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
    private const string ScreenModeKey = "screen_mode";
    private const string WindowWidthKey = "window_width";
    private const string WindowHeightKey = "window_height";
    private const string VsyncKey = "vsync";
    private const string MaxFpsKey = "max_fps";

    // Engine value ranges (rendergl.cpp / main.cpp VARPs).
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

    private const string WindowedToken = "windowed";
    private const string FullscreenToken = "fullscreen";
    private const string BorderlessToken = "borderless";

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

    /// <summary>
    /// Window presentation mode (original <c>fullscreen</c> + <c>fullscreendesktop</c>,
    /// main.cpp). Defaults to windowed.
    /// </summary>
    public ScreenMode ScreenMode { get; init; } = ScreenMode.Windowed;

    /// <summary>Windowed width in physical pixels (original <c>scr_w</c>; default 1024).</summary>
    public int WindowWidth { get; init; } = ScreenSettings.DefaultWidth;

    /// <summary>Windowed height in physical pixels (original <c>scr_h</c>; default 768).</summary>
    public int WindowHeight { get; init; } = ScreenSettings.DefaultHeight;

    /// <summary>Original <c>vsync</c> (main.cpp; default off).</summary>
    public bool Vsync { get; init; } = ScreenSettings.DefaultVsync;

    /// <summary>Frame-rate cap in FPS (original <c>maxfps</c>; 0 = unlimited, default 200).</summary>
    public int MaxFps { get; init; } = ScreenSettings.DefaultMaxFps;

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
            ScreenMode = ParseScreenMode(cfg.GetValue(Section, ScreenModeKey, WindowedToken).AsString()),
            WindowWidth = Mathf.Clamp(cfg.GetValue(Section, WindowWidthKey, ScreenSettings.DefaultWidth).AsInt32(), ScreenSettings.MinWidth, ScreenSettings.MaxDimension),
            WindowHeight = Mathf.Clamp(cfg.GetValue(Section, WindowHeightKey, ScreenSettings.DefaultHeight).AsInt32(), ScreenSettings.MinHeight, ScreenSettings.MaxDimension),
            Vsync = cfg.GetValue(Section, VsyncKey, ScreenSettings.DefaultVsync).AsBool(),
            MaxFps = Mathf.Clamp(cfg.GetValue(Section, MaxFpsKey, ScreenSettings.DefaultMaxFps).AsInt32(), 0, ScreenSettings.MaxFpsLimit),
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
        cfg.SetValue(Section, ScreenModeKey, ScreenModeToken(ScreenMode));
        cfg.SetValue(Section, WindowWidthKey, Mathf.Clamp(WindowWidth, ScreenSettings.MinWidth, ScreenSettings.MaxDimension));
        cfg.SetValue(Section, WindowHeightKey, Mathf.Clamp(WindowHeight, ScreenSettings.MinHeight, ScreenSettings.MaxDimension));
        cfg.SetValue(Section, VsyncKey, Vsync);
        cfg.SetValue(Section, MaxFpsKey, Mathf.Clamp(MaxFps, 0, ScreenSettings.MaxFpsLimit));
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

    /// <summary>Config-file token for a screen mode (stable; used as the saved value).</summary>
    public static string ScreenModeToken(ScreenMode mode) => mode switch
    {
        ScreenMode.ExclusiveFullscreen => FullscreenToken,
        ScreenMode.BorderlessFullscreen => BorderlessToken,
        _ => WindowedToken,
    };

    /// <summary>Parse a screen mode token; unknown/empty falls back to <see cref="ScreenMode.Windowed"/>.</summary>
    public static ScreenMode ParseScreenMode(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        FullscreenToken => ScreenMode.ExclusiveFullscreen,
        BorderlessToken => ScreenMode.BorderlessFullscreen,
        _ => ScreenMode.Windowed,
    };
}

/// <summary>
/// Window presentation mode chosen by the user. Mirrors the original engine's
/// <c>fullscreen</c> / <c>fullscreendesktop</c> flags (main.cpp): windowed,
/// exclusive fullscreen, or borderless fullscreen at desktop resolution.
/// </summary>
public enum ScreenMode
{
    /// <summary>A resizable window at the configured width×height.</summary>
    Windowed,

    /// <summary>Exclusive fullscreen (SDL <c>SDL_WINDOW_FULLSCREEN</c>).</summary>
    ExclusiveFullscreen,

    /// <summary>Borderless fullscreen at the desktop resolution (SDL <c>SDL_WINDOW_FULLSCREEN_DESKTOP</c>).</summary>
    BorderlessFullscreen,
}

/// <summary>
/// Startup applier for the persisted screen settings (<see cref="UserSettings"/>):
/// window mode, windowed size, V-sync and frame-rate cap. The original engine
/// applies these from <c>setfullscreen</c>/<c>screenres</c>/<c>restorevsync</c>
/// (main.cpp); here they are pushed once onto the Godot <see cref="DisplayServer"/>
/// and <see cref="Engine"/> at startup.
///
/// Kept separate from the record so the value maths (<see cref="ResolveWindowSize"/>)
/// stays pure and testable while the value ranges/defaults stay next to the
/// config keys that use them.
/// </summary>
public static class ScreenSettings
{
    /// <summary>Original <c>SCR_MINW</c> (main.cpp).</summary>
    public const int MinWidth = 320;

    /// <summary>Original <c>SCR_MINH</c> (main.cpp).</summary>
    public const int MinHeight = 200;

    /// <summary>Original <c>SCR_MAXW</c>/<c>SCR_MAXH</c> (main.cpp).</summary>
    public const int MaxDimension = 10000;

    /// <summary>Original <c>SCR_DEFAULTW</c> (main.cpp).</summary>
    public const int DefaultWidth = 1024;

    /// <summary>Original <c>SCR_DEFAULTH</c> (main.cpp).</summary>
    public const int DefaultHeight = 768;

    /// <summary>Original <c>vsync</c> default (main.cpp <c>VARFP(vsync, 0, 0, 1)</c>).</summary>
    public const bool DefaultVsync = false;

    /// <summary>Upper bound of the original <c>maxfps</c> VARP (main.cpp).</summary>
    public const int MaxFpsLimit = 1000;

    /// <summary>Original <c>maxfps</c> default (main.cpp <c>VARP(maxfps, 0, 200, 1000)</c>).</summary>
    public const int DefaultMaxFps = 200;

    /// <summary>
    /// Apply the persisted screen settings to the running window: window mode,
    /// windowed size (fullscreen uses the desktop, capped at the configured size),
    /// V-sync and the frame-rate cap.
    /// </summary>
    public static void Apply(UserSettings settings)
    {
        Vector2I size = ResolveWindowSize(settings.ScreenMode, settings.WindowWidth, settings.WindowHeight, DesktopSize());
        ApplyMode(settings.ScreenMode, size);
        ApplyVsync(settings.Vsync);
        ApplyMaxFps(settings.MaxFps);
    }

    /// <summary>Current desktop resolution for the window's screen (original <c>desktopw</c>/<c>desktoph</c>).</summary>
    public static Vector2I DesktopSize() =>
        DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen());

    /// <summary>
    /// Clamp a requested window size to the engine range and, for borderless
    /// fullscreen, to the desktop size (original <c>screenres</c>, main.cpp).
    /// Pure; does not touch the window.
    /// </summary>
    public static Vector2I ResolveWindowSize(ScreenMode mode, int width, int height, Vector2I desktop)
    {
        int w = Mathf.Clamp(width, MinWidth, MaxDimension);
        int h = Mathf.Clamp(height, MinHeight, MaxDimension);
        if (mode == ScreenMode.BorderlessFullscreen)
        {
            if (desktop.X > 0) w = Mathf.Min(w, desktop.X);
            if (desktop.Y > 0) h = Mathf.Min(h, desktop.Y);
        }
        return new Vector2I(w, h);
    }

    /// <summary>
    /// Push a window mode. When windowed, the requested size is applied after the
    /// mode so returning from fullscreen restores it; for fullscreen the size is
    /// set first so exclusive fullscreen picks the requested resolution.
    /// </summary>
    public static void ApplyMode(ScreenMode mode, Vector2I size)
    {
        if (mode == ScreenMode.Windowed)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetSize(size);
        }
        else
        {
            DisplayServer.WindowSetSize(size);
            DisplayServer.WindowSetMode(mode == ScreenMode.ExclusiveFullscreen
                ? DisplayServer.WindowMode.ExclusiveFullscreen
                : DisplayServer.WindowMode.Fullscreen);
        }
    }

    /// <summary>Apply the V-sync preference (original <c>restorevsync</c>, main.cpp).</summary>
    public static void ApplyVsync(bool enabled) =>
        DisplayServer.WindowSetVsyncMode(enabled ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);

    /// <summary>Apply the frame-rate cap; 0 leaves it unlimited (original <c>maxfps</c>).</summary>
    public static void ApplyMaxFps(int maxFps) => Engine.MaxFps = Mathf.Clamp(maxFps, 0, MaxFpsLimit);
}
