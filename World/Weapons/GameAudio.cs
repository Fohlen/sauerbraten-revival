using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// Represents an audio slot (file path relative to <c>packages/sounds/</c> without extension,
/// and relative volume scale where 100 is standard).
/// </summary>
public readonly record struct SoundSlot(string Path, int Volume);

/// <summary>
/// A registered game sound entry containing the primary sample and optional alternate variations
/// (<c>altsound</c> in Sauerbraten engine).
/// </summary>
public sealed class SoundConfig
{
    public SoundSlot Primary { get; }
    public SoundSlot[] Alts { get; }

    public SoundConfig(string path, int volume, SoundSlot[] alts)
    {
        Primary = new SoundSlot(path, volume);
        Alts = alts;
    }

    public int NumSlots => 1 + Alts.Length;

    public SoundSlot ChooseSlot(Random? rng = null)
    {
        if (Alts.Length == 0) return Primary;
        int choice = (rng ?? Random.Shared).Next(NumSlots);
        return choice == 0 ? Primary : Alts[choice - 1];
    }
}

/// <summary>
/// Sound manager that resolves <see cref="GameSound"/> IDs, loads streams, and triggers playback.
/// Checks <c>res://assets/sounds/</c> first and falls back to <see cref="MapRoots.ReferencePackages"/>/sounds.
/// </summary>
public static class GameAudio
{
    /// <summary>Global toggle for sound playback. False by default.</summary>
    public static bool Enabled { get; set; } = false;

    /// <summary>Master sound volume (engine <c>soundvol</c> 0..255; default 255).</summary>
    public static int SoundVol { get; set; } = 255;

    private static readonly Dictionary<string, AudioStream?> LoadedStreams = new();

    public static readonly SoundConfig[] HardcodedSounds = new SoundConfig[]
    {
        /*  0 */ new("aard/jump", 100, Array.Empty<SoundSlot>()),
        /*  1 */ new("aard/land", 100, Array.Empty<SoundSlot>()),
        /*  2 */ new("q009/rifle", 80, new[] { new SoundSlot("q009/rifle2", 80), new SoundSlot("q009/rifle3", 80) }),
        /*  3 */ new("free/punch1", 100, Array.Empty<SoundSlot>()),
        /*  4 */ new("q009/shotgun", 110, new[] { new SoundSlot("q009/shotgun2", 110), new SoundSlot("q009/shotgun3", 110) }),
        /*  5 */ new("q009/minigun", 70, new[] { new SoundSlot("q009/minigun2", 70), new SoundSlot("q009/minigun3", 70) }),
        /*  6 */ new("q009/rlauncher", 100, new[] { new SoundSlot("q009/rlauncher2", 100), new SoundSlot("q009/rlauncher3", 100) }),
        /*  7 */ new("q009/ren", 200, new[] { new SoundSlot("q009/ren2", 200), new SoundSlot("q009/ren3", 200) }),
        /*  8 */ new("aard/weapload", 100, Array.Empty<SoundSlot>()),
        /*  9 */ new("aard/itempick", 100, Array.Empty<SoundSlot>()),
        /* 10 */ new("free/itempick", 100, Array.Empty<SoundSlot>()),
        /* 11 */ new("free/tick", 100, Array.Empty<SoundSlot>()),
        /* 12 */ new("free/acid6", 100, Array.Empty<SoundSlot>()),
        /* 13 */ new("free/itemback", 100, Array.Empty<SoundSlot>()),
        /* 14 */ new("q009/teleport", 255, Array.Empty<SoundSlot>()),
        /* 15 */ new("aard/outofammo", 100, Array.Empty<SoundSlot>()),
        /* 16 */ new("free/acid5", 100, Array.Empty<SoundSlot>()),
        /* 17 */ new("aard/pain1", 100, Array.Empty<SoundSlot>()),
        /* 18 */ new("aard/pain2", 100, Array.Empty<SoundSlot>()),
        /* 19 */ new("aard/pain3", 100, Array.Empty<SoundSlot>()),
        /* 20 */ new("aard/pain4", 100, Array.Empty<SoundSlot>()),
        /* 21 */ new("aard/pain5", 100, Array.Empty<SoundSlot>()),
        /* 22 */ new("aard/pain6", 100, Array.Empty<SoundSlot>()),
        /* 23 */ new("aard/die1", 100, Array.Empty<SoundSlot>()),
        /* 24 */ new("aard/die2", 100, Array.Empty<SoundSlot>()),
        /* 25 */ new("q009/glauncher", 100, Array.Empty<SoundSlot>()),
        /* 26 */ new("q009/grenade", 200, new[] { new SoundSlot("q009/grenade2", 200), new SoundSlot("q009/grenade3", 200) }),
        /* 27 */ new("free/splash1", 100, Array.Empty<SoundSlot>()),
        /* 28 */ new("free/splash2", 100, Array.Empty<SoundSlot>()),
        /* 29 */ new("aard/grunt1", 100, Array.Empty<SoundSlot>()),
        /* 30 */ new("aard/grunt2", 100, Array.Empty<SoundSlot>()),
        /* 31 */ new("free/rumble", 100, Array.Empty<SoundSlot>()),
        /* 32 */ new("ppm/paino", 100, Array.Empty<SoundSlot>()),
        /* 33 */ new("ppm/painr", 100, Array.Empty<SoundSlot>()),
        /* 34 */ new("ppm/deathr", 100, Array.Empty<SoundSlot>()),
        /* 35 */ new("ppm/paine", 100, Array.Empty<SoundSlot>()),
        /* 36 */ new("ppm/deathe", 100, Array.Empty<SoundSlot>()),
        /* 37 */ new("ppm/pains", 100, Array.Empty<SoundSlot>()),
        /* 38 */ new("ppm/deaths", 100, Array.Empty<SoundSlot>()),
        /* 39 */ new("ppm/painb", 100, Array.Empty<SoundSlot>()),
        /* 40 */ new("ppm/deathb", 100, Array.Empty<SoundSlot>()),
        /* 41 */ new("ppm/painp", 100, Array.Empty<SoundSlot>()),
        /* 42 */ new("free/piggrunt2", 100, Array.Empty<SoundSlot>()),
        /* 43 */ new("ppm/painh", 100, Array.Empty<SoundSlot>()),
        /* 44 */ new("ppm/deathh", 100, Array.Empty<SoundSlot>()),
        /* 45 */ new("ppm/paind", 100, Array.Empty<SoundSlot>()),
        /* 46 */ new("ppm/deathd", 100, Array.Empty<SoundSlot>()),
        /* 47 */ new("free/piggrunt1", 100, Array.Empty<SoundSlot>()),
        /* 48 */ new("ppm/iceball", 100, Array.Empty<SoundSlot>()),
        /* 49 */ new("ppm/slimeball", 100, Array.Empty<SoundSlot>()),
        /* 50 */ new("nieb/jumppad", 255, Array.Empty<SoundSlot>()),
        /* 51 */ new("q009/pistol", 80, new[] { new SoundSlot("q009/pistol2", 80), new SoundSlot("q009/pistol3", 80) }),
        /* 52 */ new("blindabuser/base_captured", 150, Array.Empty<SoundSlot>()),
        /* 53 */ new("blindabuser/base_lost", 150, Array.Empty<SoundSlot>()),
        /* 54 */ new("blindabuser/fight", 150, Array.Empty<SoundSlot>()),
        /* 55 */ new("blindabuser/health_boost", 150, Array.Empty<SoundSlot>()),
        /* 56 */ new("blindabuser/health_boost_in_10_seconds", 150, Array.Empty<SoundSlot>()),
        /* 57 */ new("blindabuser/quad_damage", 150, Array.Empty<SoundSlot>()),
        /* 58 */ new("blindabuser/quad_damage_in_10_seconds", 150, Array.Empty<SoundSlot>()),
        /* 59 */ new("blindabuser/respawn_point_set", 150, Array.Empty<SoundSlot>()),
        /* 60 */ new("ctf/flagpickup", 100, Array.Empty<SoundSlot>()),
        /* 61 */ new("ctf/flagdrop", 100, Array.Empty<SoundSlot>()),
        /* 62 */ new("ctf/flagreturn", 100, Array.Empty<SoundSlot>()),
        /* 63 */ new("ctf/flagscore", 100, Array.Empty<SoundSlot>()),
        /* 64 */ new("ctf/flagreturn", 100, Array.Empty<SoundSlot>()),
        /* 65 */ new("soundsnap/burn", 100, Array.Empty<SoundSlot>()),
        /* 66 */ new("soundsnap/chainsaw_attack", 150, Array.Empty<SoundSlot>()),
        /* 67 */ new("soundsnap/chainsaw_idle", 230, Array.Empty<SoundSlot>()),
        /* 68 */ new("free/hit", 60, Array.Empty<SoundSlot>()),
        /* 69 */ new("ctf/flagfail", 200, Array.Empty<SoundSlot>()),
    };

    public static SoundConfig? GetConfig(GameSound sound)
    {
        int index = (int)sound;
        if (index < 0 || index >= HardcodedSounds.Length) return null;
        return HardcodedSounds[index];
    }

    /// <summary>
    /// Load an audio stream given a relative sound path (e.g. <c>q009/rifle</c>).
    /// </summary>
    public static AudioStream? LoadSound(string relativePath)
    {
        if (LoadedStreams.TryGetValue(relativePath, out var cached))
            return cached;

        AudioStream? stream = TryLoadFile(relativePath);
        LoadedStreams[relativePath] = stream;
        return stream;
    }

    private static AudioStream? TryLoadFile(string rel)
    {
        // Try Godot project imported sound directory first
        string projectPath = Path.Combine(ProjectSettings.GlobalizePath("res://assets/sounds"), rel);
        if (TryResolveStream(projectPath, out var stream)) return stream;

        // Try dump reference root fallback
        string dumpPath = Path.Combine(MapRoots.ReferencePackages, "sounds", rel);
        if (TryResolveStream(dumpPath, out stream)) return stream;

        return null;
    }

    private static bool TryResolveStream(string basePathWithoutExt, out AudioStream? stream)
    {
        stream = null;
        string[] exts = { ".ogg", ".wav", "" };
        foreach (var ext in exts)
        {
            string full = basePathWithoutExt + ext;
            if (File.Exists(full))
            {
                if (full.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                {
                    var ogg = AudioStreamOggVorbis.LoadFromFile(full);
                    if (ogg != null)
                    {
                        ogg.Loop = false;
                        stream = ogg;
                        return true;
                    }
                }
                else if (full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    var wav = AudioStreamWav.LoadFromFile(full);
                    if (wav != null)
                    {
                        wav.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
                        stream = wav;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Play a <see cref="GameSound"/> through a local 2D AudioStreamPlayer (e.g. for the local player's ears).
    /// </summary>
    public static void Play(this AudioStreamPlayer? player, GameSound sound)
    {
        if (!Enabled || SoundVol <= 0 || player == null || sound == GameSound.None) return;
        var config = GetConfig(sound);
        if (config == null) return;

        var slot = config.ChooseSlot();
        var stream = LoadSound(slot.Path);
        if (stream == null) return;

        player.Stream = stream;
        // Volume scale: 100 is 0 dB; scaled by master volume (SoundVol / 255)
        float masterScale = SoundVol / 255.0f;
        float volumeLinear = Mathf.Clamp((slot.Volume / 100.0f) * masterScale, 0.001f, 4.0f);
        player.VolumeDb = Mathf.LinearToDb(volumeLinear);
        player.Play();
    }

    /// <summary>
    /// Play a <see cref="GameSound"/> at a 3D location through an AudioStreamPlayer3D.
    /// </summary>
    public static void Play3D(this AudioStreamPlayer3D? player, GameSound sound)
    {
        if (!Enabled || SoundVol <= 0 || player == null || sound == GameSound.None) return;
        var config = GetConfig(sound);
        if (config == null) return;

        var slot = config.ChooseSlot();
        var stream = LoadSound(slot.Path);
        if (stream == null) return;

        player.Stream = stream;
        float masterScale = SoundVol / 255.0f;
        float volumeLinear = Mathf.Clamp((slot.Volume / 100.0f) * masterScale, 0.001f, 4.0f);
        player.VolumeDb = Mathf.LinearToDb(volumeLinear);
        player.Play();
    }
}
