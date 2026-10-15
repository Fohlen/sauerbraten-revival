using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Lighting;

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
}
