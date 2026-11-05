using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SoundEmitter = SauerbratenRevival.World.Audio.SoundEmitter;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies ET_SOUND representation against bt_falls, which registers 3 map sounds
/// (outdoors.ogg, waterfall.ogg, hum7.wav) and places 5 sound entities.
/// </summary>
public class EntitySoundTest : GoTestBase
{
    public EntitySoundTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsSoundEmitters()
    {
        var map = TestData.ReadMap("bt_falls");
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePathFor("bt_falls"));

        TestAssert.Equal(3, catalogue.Sounds.Count, "catalogue sounds");
        TestAssert.Equal("soundtransit/waterfall.ogg", catalogue.SoundPath(1), "sound 1 path");

        var root = SoundBuilder.Build(map, catalogue);

        int count = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is not SoundEmitter sound) continue;
            int index = int.Parse(sound.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];

            TestAssert.Equal((int)entity.Attr1, sound.SoundIndex, $"{sound.Name} index");
            TestAssert.Equal((int)entity.Attr2, sound.Radius, $"{sound.Name} radius");
            TestAssert.Equal((int)entity.Attr3, sound.Size, $"{sound.Name} size");
            TestAssert.Equal(catalogue.SoundPath(entity.Attr1), sound.SoundPath, $"{sound.Name} path");
            TestAssert.True(sound.Position.DistanceTo(entity.Position) < 0.01f, $"{sound.Name} position");
            TestAssert.True(sound.Stream != null, $"{sound.Name} stream loaded");
            count++;
        }
        TestAssert.Equal(5, count, "sound entities");
        root.Free();
    }

    [Test]
    public void SoundEmittersWiredToSoundSettings()
    {
        bool prevEnabled = GameAudio.Enabled;
        int prevVol = GameAudio.SoundVol;
        try
        {
            GameAudio.Apply(enabled: false, volume: 255);

            var map = TestData.ReadMap("bt_falls");
            var catalogue = MetadataCatalogue.Load(TestData.CataloguePathFor("bt_falls"));
            var root = SoundBuilder.Build(map, catalogue);
            TestScene.AddChild(root);

            var firstEmitter = root.GetChildren().OfType<SoundEmitter>().First();
            TestAssert.Equal(SoundEmitter.MutedDb, firstEmitter.VolumeDb, "emitter is muted when sound is disabled");
            TestAssert.False(firstEmitter.Playing, "emitter is not playing when sound is disabled");

            // Turn sound on at full volume (255)
            GameAudio.Apply(enabled: true, volume: 255);
            TestAssert.Near(0.0f, firstEmitter.VolumeDb, 0.01f, "volume is 0 dB at full volume");
            TestAssert.True(firstEmitter.Playing, "emitter starts playing when sound is enabled");

            // Change sound volume to 128 (~50%)
            GameAudio.Apply(enabled: true, volume: 128);
            float expectedDb = Mathf.LinearToDb(128.0f / 255.0f);
            TestAssert.Near(expectedDb, firstEmitter.VolumeDb, 0.01f, "volume matches scaled dB at volume 128");
            TestAssert.True(firstEmitter.Playing, "emitter keeps playing at scaled volume");

            // Turn sound off
            GameAudio.Apply(enabled: false, volume: 128);
            TestAssert.Equal(SoundEmitter.MutedDb, firstEmitter.VolumeDb, "emitter muted after disabling sound");
            TestAssert.False(firstEmitter.Playing, "emitter stops playing after disabling sound");

            // Set volume to 0 while enabled
            GameAudio.Apply(enabled: true, volume: 0);
            TestAssert.Equal(SoundEmitter.MutedDb, firstEmitter.VolumeDb, "volume 0 mutes emitter");
            TestAssert.False(firstEmitter.Playing, "volume 0 stops playback");

            TestScene.RemoveChild(root);
            root.Free();
        }
        finally
        {
            GameAudio.Apply(prevEnabled, prevVol);
        }
    }

    [Test]
    public void SoundEmitterUnsubscribesOnExitTree()
    {
        bool prevEnabled = GameAudio.Enabled;
        int prevVol = GameAudio.SoundVol;
        try
        {
            GameAudio.Apply(enabled: true, volume: 255);
            var emitter = new SoundEmitter
            {
                BaseVolume = 100,
            };
            TestScene.AddChild(emitter);
            TestAssert.Near(0.0f, emitter.VolumeDb, 0.01f, "emitter volume initialized in tree");

            TestScene.RemoveChild(emitter);
            // After removing from tree, SettingsChanged should not update it
            GameAudio.Apply(enabled: true, volume: 128);
            TestAssert.Near(0.0f, emitter.VolumeDb, 0.01f, "emitter volume untouched after exiting tree");

            emitter.Free();
        }
        finally
        {
            GameAudio.Apply(prevEnabled, prevVol);
        }
    }
}
