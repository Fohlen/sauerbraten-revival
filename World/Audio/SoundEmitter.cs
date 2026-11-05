using Godot;

namespace SauerbratenRevival.World.Audio;

/// <summary>
/// An <c>ET_SOUND</c> ambient sound. <c>attr1</c> indexes the map's
/// <c>mapsound</c> list, <c>attr2</c> is the audible radius, <c>attr3</c> (if
/// non-zero) the radius of full volume. Synchronized with user sound settings
/// via <see cref="GameAudio.SettingsChanged"/>.
/// </summary>
[GlobalClass]
public partial class SoundEmitter : AudioStreamPlayer3D
{
    public const float MutedDb = -80.0f;

    [Export] public int SoundIndex { get; set; }

    /// <summary>Resolved path relative to <c>packages/sounds/</c>.</summary>
    [Export] public string? SoundPath { get; set; }

    [Export] public int Radius { get; set; }
    [Export] public int Size { get; set; }

    /// <summary>Base relative volume scale where 100 is standard (engine default).</summary>
    [Export] public int BaseVolume { get; set; } = 100;

    public override void _EnterTree()
    {
        GameAudio.SettingsChanged += ApplyAudioSettings;
        if (IsNodeReady())
        {
            ApplyAudioSettings();
        }
    }

    public override void _Ready()
    {
        ApplyAudioSettings();
    }

    public override void _ExitTree()
    {
        GameAudio.SettingsChanged -= ApplyAudioSettings;
    }

    /// <summary>
    /// Synchronizes volume and playback state with <see cref="GameAudio.Enabled"/> and <see cref="GameAudio.SoundVol"/>.
    /// </summary>
    public void ApplyAudioSettings()
    {
        if (!GameAudio.Enabled || GameAudio.SoundVol <= 0)
        {
            VolumeDb = MutedDb;
            if (Playing)
            {
                Stop();
            }
            return;
        }

        float masterScale = GameAudio.SoundVol / (float)GameAudio.MaxSoundVol;
        float volumeLinear = Mathf.Clamp((BaseVolume / 100.0f) * masterScale, 0.0001f, 4.0f);
        VolumeDb = Mathf.LinearToDb(volumeLinear);

        if (Stream != null && IsInsideTree() && !Playing)
        {
            Play();
        }
    }
}
