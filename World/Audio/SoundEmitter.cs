using Godot;

namespace SauerbratenRevival.World.Audio;

/// <summary>
/// An <c>ET_SOUND</c> ambient sound. <c>attr1</c> indexes the map's
/// <c>mapsound</c> list, <c>attr2</c> is the audible radius, <c>attr3</c> (if
/// non-zero) the radius of full volume. Plays looping in the dev viewer; the
/// future game logic controls playback.
/// </summary>
[GlobalClass]
public partial class SoundEmitter : AudioStreamPlayer3D
{
    [Export] public int SoundIndex { get; set; }

    /// <summary>Resolved path relative to <c>packages/sounds/</c>.</summary>
    [Export] public string? SoundPath { get; set; }

    [Export] public int Radius { get; set; }
    [Export] public int Size { get; set; }
}
