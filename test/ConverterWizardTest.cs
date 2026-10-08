using Chickensoft.GoDotTest;
using Godot;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the converter wizard's persisted settings (<c>user://settings.cfg</c>),
/// the only piece of the GUI with testable pure logic.
/// </summary>
public class ConverterWizardTest : GoTestBase
{
    public ConverterWizardTest(Node testScene) : base(testScene) { }

    [Test]
    public void SauerbratenPathRoundTripsThroughSettings()
    {
        var cfg = new ConfigFile();
        cfg.Load(ConverterWizard.SettingsPath);
        bool had = cfg.HasSectionKey("settings", ConverterWizard.SauerbratenPathKey);
        string previous = had ? cfg.GetValue("settings", ConverterWizard.SauerbratenPathKey).AsString() : "";

        try
        {
            string path = "/tmp/sauer-" + System.Guid.NewGuid().ToString("N");
            ConverterWizard.SaveSetting(ConverterWizard.SauerbratenPathKey, path);
            TestAssert.Equal(path, ConverterWizard.LoadSetting(ConverterWizard.SauerbratenPathKey),
                "saved path is read back");
        }
        finally
        {
            if (had) ConverterWizard.SaveSetting(ConverterWizard.SauerbratenPathKey, previous);
            else
            {
                var restore = new ConfigFile();
                restore.Load(ConverterWizard.SettingsPath);
                restore.EraseSectionKey("settings", ConverterWizard.SauerbratenPathKey);
                restore.Save(ConverterWizard.SettingsPath);
            }
        }
    }

    [Test]
    public void MissingSettingReturnsEmpty()
    {
        TestAssert.Equal("", ConverterWizard.LoadSetting("nonexistent/" + System.Guid.NewGuid().ToString("N")),
            "unset key reads as empty");
    }
}
