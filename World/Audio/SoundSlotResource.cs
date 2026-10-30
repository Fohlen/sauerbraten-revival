using Godot;

namespace SauerbratenRevival.World.Audio;

/// <summary>
/// A single audio slot definition holding a file path and relative volume scale (where 100 is standard).
/// </summary>
[Tool]
[GlobalClass]
public partial class SoundSlotResource : Resource
{
    [Export] public string Path { get; set; } = "";
    [Export] public int Volume { get; set; } = 100;

    public SoundSlotResource() { }

    public SoundSlotResource(string path, int volume)
    {
        Path = path;
        Volume = volume;
    }

    public SoundSlot ToSoundSlot() => new(Path, Volume);
}
