using System;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class GameAudioTest : TestClass
{
    public GameAudioTest(Node testScene) : base(testScene) { }

    [Test]
    public void HardcodedSoundsTableMatchesCount()
    {
        // Engine sounds.cfg registers 70 hardcoded sounds (0 to 69)
        TestAssert.Equal(70, GameAudio.HardcodedSounds.Length, "HardcodedSounds table must have exactly 70 entries");
    }

    [Test]
    public void WeaponSoundsAreConfigured()
    {
        // Check core weapons have valid non-empty sound configurations
        var rifle = GameAudio.GetConfig(GameSound.Rifle);
        TestAssert.True(rifle != null, "Rifle sound config should exist");
        TestAssert.Equal("q009/rifle", rifle!.Primary.Path, "Rifle primary sample path");
        TestAssert.Equal(80, rifle.Primary.Volume, "Rifle volume is 80");
        TestAssert.Equal(2, rifle.Alts.Length, "Rifle has 2 alts");
        TestAssert.Equal(3, rifle.NumSlots, "Rifle total slots is 3");

        var sg = GameAudio.GetConfig(GameSound.Sg);
        TestAssert.True(sg != null, "Shotgun sound config should exist");
        TestAssert.Equal("q009/shotgun", sg!.Primary.Path, "Shotgun primary sample path");
        TestAssert.Equal(110, sg.Primary.Volume, "Shotgun volume is 110");

        var cg = GameAudio.GetConfig(GameSound.Cg);
        TestAssert.True(cg != null, "Chaingun sound config should exist");
        TestAssert.Equal("q009/minigun", cg!.Primary.Path, "Chaingun primary sample path");
        TestAssert.Equal(70, cg.Primary.Volume, "Chaingun volume is 70");

        var pistol = GameAudio.GetConfig(GameSound.Pistol);
        TestAssert.True(pistol != null, "Pistol sound config should exist");
        TestAssert.Equal("q009/pistol", pistol!.Primary.Path, "Pistol primary sample path");
        TestAssert.Equal(80, pistol.Primary.Volume, "Pistol volume is 80");
    }

    [Test]
    public void ChooseSlotRespectsAlts()
    {
        var rifle = GameAudio.GetConfig(GameSound.Rifle)!;
        // Using a seeded Random to test deterministic slot picking
        var rng0 = new Random(0);
        var slot0 = rifle.ChooseSlot(rng0);
        TestAssert.True(slot0.Path.StartsWith("q009/rifle"), "Slot path starts with q009/rifle");

        var punch = GameAudio.GetConfig(GameSound.Punch1)!;
        TestAssert.Equal(0, punch.Alts.Length, "Punch has no alts");
        TestAssert.Equal("free/punch1", punch.ChooseSlot().Path, "Punch chooses only primary slot");
    }

    [Test]
    public void SoundStreamsCanBeLoaded()
    {
        var stream = GameAudio.LoadSound("q009/rifle");
        TestAssert.True(stream != null, "Rifle sound stream should load from assets or dump fallback");

        var punchStream = GameAudio.LoadSound("free/punch1");
        TestAssert.True(punchStream != null, "Punch1 sound stream should load from assets or dump fallback");
    }

    [Test]
    public void ApplySetsEnabledAndClampsVolume()
    {
        bool previousEnabled = GameAudio.Enabled;
        int previousVolume = GameAudio.SoundVol;
        try
        {
            GameAudio.Apply(enabled: true, volume: 999);
            TestAssert.True(GameAudio.Enabled, "enabled applies");
            TestAssert.Equal(GameAudio.MaxSoundVol, GameAudio.SoundVol, "volume clamps to max");

            GameAudio.Apply(enabled: false, volume: 128);
            TestAssert.False(GameAudio.Enabled, "disabled applies");
            TestAssert.Equal(128, GameAudio.SoundVol, "volume applies");
        }
        finally
        {
            GameAudio.Apply(previousEnabled, previousVolume);
        }
    }

    [Test]
    public void SoundIsOffByDefault()
    {
        TestAssert.False(GameAudio.Enabled, "GameAudio.Enabled should be false by default");
    }
}
