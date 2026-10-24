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
}
