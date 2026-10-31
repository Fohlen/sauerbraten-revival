using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Audio;

/// <summary>
/// A collection of sound definitions, loaded from a single <c>.tres</c> (e.g. the core sounds catalog).
/// </summary>
[Tool]
[GlobalClass]
public partial class SoundCatalogResource : Resource, IResourceCatalog<SoundDefinition>
{
    [Export] public Godot.Collections.Array<SoundDefinition> Sounds { get; set; } = new();

    public IEnumerable<SoundDefinition> Definitions => Sounds;

    public static SoundCatalogResource CreateFromDefaults(SoundConfig[] defaults)
    {
        var catalog = new SoundCatalogResource();
        for (int i = 0; i < defaults.Length; i++)
        {
            var sound = (GameSound)i;
            var config = defaults[i];
            var alts = new SoundSlotResource[config.Alts.Length];
            for (int a = 0; a < config.Alts.Length; a++)
            {
                alts[a] = new SoundSlotResource(config.Alts[a].Path, config.Alts[a].Volume);
            }
            var def = new SoundDefinition(sound, sound.ToString().ToLowerInvariant(), config.Primary.Path, config.Primary.Volume, alts);
            catalog.Sounds.Add(def);
        }
        return catalog;
    }
}
