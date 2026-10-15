using Chickensoft.GoDotTest;
using Godot;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the converter wizard's persisted settings: the remembered Sauerbraten
/// install path round-trips through <see cref="UserSettings"/>
/// (<c>user://settings.cfg</c>).
/// </summary>
public class ConverterWizardTest : GoTestBase
{
    public ConverterWizardTest(Node testScene) : base(testScene) { }

    [Test]
    public void SauerbratenPathRoundTripsThroughSettings()
    {
        UserSettings previous = UserSettings.Load();
        try
        {
            string path = "/tmp/sauer-" + System.Guid.NewGuid().ToString("N");
            (previous with { SauerbratenPath = path }).Save();
            TestAssert.Equal(path, UserSettings.Load().SauerbratenPath, "saved path is read back");
        }
        finally
        {
            previous.Save();
        }
    }
}
