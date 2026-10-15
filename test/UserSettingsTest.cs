using Chickensoft.GoDotTest;
using Godot;
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
            string path = "/tmp/sauer-" + System.Guid.NewGuid().ToString("N");
            new UserSettings { SauerbratenPath = path, LightingMode = RadianceQualityMode.StaticAmbient }.Save();

            UserSettings loaded = UserSettings.Load();
            TestAssert.Equal(path, loaded.SauerbratenPath, "path persists");
            TestAssert.Equal(RadianceQualityMode.StaticAmbient, loaded.LightingMode, "mode persists");

            (loaded with { LightingMode = RadianceQualityMode.RadianceHints }).Save();
            TestAssert.Equal(path, UserSettings.Load().SauerbratenPath, "unrelated field is untouched");
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
}
