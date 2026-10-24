using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Lighting;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the typed user settings record (<c>user://settings.cfg</c>): lighting
/// mode round-trip, token parsing/fallback, and that updating one field leaves the
/// others intact (docs/vision.md §11, docs/adr/0002-radiance-hints.md §7).
/// </summary>
public class UserSettingsTest : GoTestBase
{
    public UserSettingsTest(Node testScene) : base(testScene) { }

    [Test]
    public void LightingModeRoundTrips()
    {
        UserSettings previous = UserSettings.Load();
        try
        {
            (previous with { LightingMode = RadianceQualityMode.StaticAmbient }).Save();
            TestAssert.Equal(RadianceQualityMode.StaticAmbient, UserSettings.Load().LightingMode,
                "static ambient mode round-trips");

            (previous with { LightingMode = RadianceQualityMode.RadianceHints }).Save();
            TestAssert.Equal(RadianceQualityMode.RadianceHints, UserSettings.Load().LightingMode,
                "radiance hints mode round-trips");
        }
        finally
        {
            previous.Save();
        }
    }

    [Test]
    public void UnknownLightingModeFallsBackToRadianceHints()
    {
        TestAssert.Equal(RadianceQualityMode.RadianceHints, UserSettings.ParseLightingMode("nonsense"),
            "unknown token falls back to radiance hints");
        TestAssert.Equal(RadianceQualityMode.RadianceHints, UserSettings.ParseLightingMode(""),
            "empty token falls back to radiance hints");
    }

    [Test]
    public void LightingModeTokenIsCaseAndWhitespaceInsensitive()
    {
        TestAssert.Equal(RadianceQualityMode.StaticAmbient, UserSettings.ParseLightingMode("  Static_Ambient "),
            "token is normalized");
        TestAssert.Equal("static_ambient", UserSettings.LightingModeToken(RadianceQualityMode.StaticAmbient),
            "token is stable");
        TestAssert.Equal("radiance_hints", UserSettings.LightingModeToken(RadianceQualityMode.RadianceHints),
            "token is stable");
    }

    [Test]
    public void UpdatingOneFieldKeepsTheOthers()
    {
        UserSettings previous = UserSettings.Load();
        try
        {
            new UserSettings { Sensitivity = 7.5f, LightingMode = RadianceQualityMode.StaticAmbient }.Save();

            UserSettings loaded = UserSettings.Load();
            TestAssert.Near(7.5f, loaded.Sensitivity, 1e-4f, "sensitivity persists");
            TestAssert.Equal(RadianceQualityMode.StaticAmbient, loaded.LightingMode, "mode persists");

            (loaded with { LightingMode = RadianceQualityMode.RadianceHints }).Save();
            TestAssert.Near(7.5f, UserSettings.Load().Sensitivity, 1e-4f, "unrelated field is untouched");
        }
        finally
        {
            previous.Save();
        }
    }

    [Test]
    public void SoundSettingsRoundTrip()
    {
        UserSettings previous = UserSettings.Load();
        try
        {
            (previous with { SoundEnabled = false, SoundVolume = 128 }).Save();
            UserSettings loaded = UserSettings.Load();
            TestAssert.False(loaded.SoundEnabled, "sound_enabled persists");
            TestAssert.Equal(128, loaded.SoundVolume, "sound_volume persists");
        }
        finally
        {
            previous.Save();
        }
    }

    [Test]
    public void SoundDefaultsAreOnAtFullVolume()
    {
        var defaults = new UserSettings();
        TestAssert.True(defaults.SoundEnabled, "sound is enabled by default");
        TestAssert.Equal(GameAudio.MaxSoundVol, defaults.SoundVolume, "full volume by default");
    }

    [Test]
    public void OutOfRangeSoundVolumeClampsOnLoad()
    {
        UserSettings previous = UserSettings.Load();
        var cfg = new ConfigFile();
        cfg.Load(UserSettings.SettingsPath);
        cfg.SetValue(UserSettings.Section, "sound_volume", 999);
        cfg.Save(UserSettings.SettingsPath);
        try
        {
            TestAssert.Equal(GameAudio.MaxSoundVol, UserSettings.Load().SoundVolume, "volume clamps to max");
        }
        finally
        {
            previous.Save();
        }
    }

    [Test]
    public void LookSettingsRoundTrip()
    {
        UserSettings previous = UserSettings.Load();
        try
        {
            (previous with
            {
                Sensitivity = 7.5f,
                SensitivityScale = 2f,
                MouseAccel = 0.5f,
                InvertMouse = true,
                Fov = 120f,
            }).Save();

            UserSettings loaded = UserSettings.Load();
            TestAssert.Near(7.5f, loaded.Sensitivity, 1e-4f, "sensitivity persists");
            TestAssert.Near(2f, loaded.SensitivityScale, 1e-4f, "sensitivity_scale persists");
            TestAssert.Near(0.5f, loaded.MouseAccel, 1e-4f, "mouse_accel persists");
            TestAssert.True(loaded.InvertMouse, "invert_mouse persists");
            TestAssert.Near(120f, loaded.Fov, 1e-4f, "fov persists");
        }
        finally
        {
            previous.Save();
        }
    }

    [Test]
    public void LookDefaultsMatchEngine()
    {
        var defaults = new UserSettings();
        TestAssert.Near(3f, defaults.Sensitivity, 1e-4f, "sensitivity default 3");
        TestAssert.Near(1f, defaults.SensitivityScale, 1e-4f, "sensitivityscale default 1");
        TestAssert.Near(0f, defaults.MouseAccel, 1e-4f, "mouseaccel default 0");
        TestAssert.False(defaults.InvertMouse, "invmouse default off");
        TestAssert.Near(100f, defaults.Fov, 1e-4f, "fov default 100");
    }

    [Test]
    public void OutOfRangeLookValuesClampOnLoad()
    {
        UserSettings previous = UserSettings.Load();
        var cfg = new ConfigFile();
        cfg.Load(UserSettings.SettingsPath);
        cfg.SetValue(UserSettings.Section, "sensitivity_scale", 0.0);
        cfg.SetValue(UserSettings.Section, "fov", 999.0);
        cfg.Save(UserSettings.SettingsPath);
        try
        {
            UserSettings loaded = UserSettings.Load();
            TestAssert.True(loaded.SensitivityScale > 0f, "sensitivity scale stays above zero (no divide by zero)");
            TestAssert.Near(150f, loaded.Fov, 1e-4f, "fov clamps to max");
        }
        finally
        {
            previous.Save();
        }
    }

    [Test]
    public void ScreenSettingsRoundTrip()
    {
        UserSettings previous = UserSettings.Load();
        try
        {
            (previous with
            {
                ScreenMode = ScreenMode.BorderlessFullscreen,
                WindowWidth = 1600,
                WindowHeight = 900,
                Vsync = true,
                MaxFps = 144,
            }).Save();

            UserSettings loaded = UserSettings.Load();
            TestAssert.Equal(ScreenMode.BorderlessFullscreen, loaded.ScreenMode, "screen_mode persists");
            TestAssert.Equal(1600, loaded.WindowWidth, "window_width persists");
            TestAssert.Equal(900, loaded.WindowHeight, "window_height persists");
            TestAssert.True(loaded.Vsync, "vsync persists");
            TestAssert.Equal(144, loaded.MaxFps, "max_fps persists");
        }
        finally
        {
            previous.Save();
        }
    }

    [Test]
    public void ScreenDefaultsMatchEngine()
    {
        var defaults = new UserSettings();
        TestAssert.Equal(ScreenMode.Windowed, defaults.ScreenMode, "windowed by default");
        TestAssert.Equal(ScreenSettings.DefaultWidth, defaults.WindowWidth, "default width 1024");
        TestAssert.Equal(ScreenSettings.DefaultHeight, defaults.WindowHeight, "default height 768");
        TestAssert.False(defaults.Vsync, "vsync default off");
        TestAssert.Equal(ScreenSettings.DefaultMaxFps, defaults.MaxFps, "maxfps default 200");
    }

    [Test]
    public void OutOfRangeScreenValuesClampOnLoad()
    {
        UserSettings previous = UserSettings.Load();
        var cfg = new ConfigFile();
        cfg.Load(UserSettings.SettingsPath);
        cfg.SetValue(UserSettings.Section, "window_width", 1);
        cfg.SetValue(UserSettings.Section, "window_height", 999999);
        cfg.SetValue(UserSettings.Section, "max_fps", -5);
        cfg.Save(UserSettings.SettingsPath);
        try
        {
            UserSettings loaded = UserSettings.Load();
            TestAssert.Equal(ScreenSettings.MinWidth, loaded.WindowWidth, "width clamps to min");
            TestAssert.Equal(ScreenSettings.MaxDimension, loaded.WindowHeight, "height clamps to max");
            TestAssert.Equal(0, loaded.MaxFps, "negative maxfps clamps to unlimited");
        }
        finally
        {
            previous.Save();
        }
    }

    [Test]
    public void ScreenModeTokensAreStableAndCaseInsensitive()
    {
        TestAssert.Equal("windowed", UserSettings.ScreenModeToken(ScreenMode.Windowed), "windowed token stable");
        TestAssert.Equal("fullscreen", UserSettings.ScreenModeToken(ScreenMode.ExclusiveFullscreen), "fullscreen token stable");
        TestAssert.Equal("borderless", UserSettings.ScreenModeToken(ScreenMode.BorderlessFullscreen), "borderless token stable");
        TestAssert.Equal(ScreenMode.ExclusiveFullscreen, UserSettings.ParseScreenMode("  FullScreen "), "token is normalized");
        TestAssert.Equal(ScreenMode.Windowed, UserSettings.ParseScreenMode("nonsense"), "unknown falls back to windowed");
        TestAssert.Equal(ScreenMode.Windowed, UserSettings.ParseScreenMode(""), "empty falls back to windowed");
    }

    [Test]
    public void ResolveWindowSizeClampsToEngineRange()
    {
        var desktop = new Vector2I(1920, 1080);
        TestAssert.Equal(new Vector2I(ScreenSettings.MinWidth, ScreenSettings.MinHeight),
            ScreenSettings.ResolveWindowSize(ScreenMode.Windowed, 1, 1, desktop), "below-min clamps up");
        TestAssert.Equal(new Vector2I(ScreenSettings.MaxDimension, ScreenSettings.MaxDimension),
            ScreenSettings.ResolveWindowSize(ScreenMode.Windowed, 999999, 999999, desktop), "above-max clamps down");
        TestAssert.Equal(new Vector2I(1280, 720),
            ScreenSettings.ResolveWindowSize(ScreenMode.Windowed, 1280, 720, desktop), "in-range is untouched");
    }

    [Test]
    public void ResolveWindowSizeCapsBorderlessToDesktop()
    {
        var desktop = new Vector2I(1280, 720);
        TestAssert.Equal(new Vector2I(1280, 720),
            ScreenSettings.ResolveWindowSize(ScreenMode.BorderlessFullscreen, 2560, 1440, desktop), "borderless caps to desktop");
        TestAssert.Equal(new Vector2I(800, 600),
            ScreenSettings.ResolveWindowSize(ScreenMode.BorderlessFullscreen, 800, 600, desktop), "smaller borderless size is kept");
    }
}
