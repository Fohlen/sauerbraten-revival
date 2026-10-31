using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Audio;

/// <summary>
/// An authored or modded sound entry containing the primary sample and optional alternate variations.
/// Can be loaded standalone as an override or contained within a <see cref="SoundCatalogResource"/>.
/// </summary>
[Tool]
[GlobalClass]
public partial class SoundDefinition : Resource, IResourceDefinition<GameSound>
{
    /// <summary>Corresponding classic <see cref="GameSound"/> ID, or <see cref="GameSound.None"/> if custom/mod sound.</summary>
    [Export] public GameSound Id { get; set; } = GameSound.None;

    /// <summary>Logical name of the sound (e.g. "rifle", "jump").</summary>
    [Export] public string Name { get; set; } = "";

    /// <summary>Primary sample path relative to sounds directory (without extension).</summary>
    [Export] public string Path { get; set; } = "";

    /// <summary>Primary sample volume scale (100 is standard, 0..255).</summary>
    [Export] public int Volume { get; set; } = 100;

    /// <summary>Alternate sound variations (<c>altsound</c> in Sauerbraten engine).</summary>
    [Export] public Godot.Collections.Array<SoundSlotResource> Alts { get; set; } = new();

    public SoundDefinition() { }

    public SoundDefinition(GameSound id, string name, string path, int volume, SoundSlotResource[]? alts = null)
    {
        Id = id;
        Name = name;
        Path = path;
        Volume = volume;
        if (alts != null)
        {
            foreach (var alt in alts)
            {
                Alts.Add(alt);
            }
        }
    }

    public SoundConfig ToSoundConfig()
    {
        var altSlots = new SoundSlot[Alts.Count];
        for (int i = 0; i < Alts.Count; i++)
        {
            altSlots[i] = Alts[i].ToSoundSlot();
        }
        return new SoundConfig(Path, Volume, altSlots);
    }
}
