using System;
using System.IO;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Builds <see cref="SoundEmitter"/> nodes from a map's <c>ET_SOUND</c> entities,
/// resolving the path through the metadata catalogue (<c>mapsound</c> names are
/// relative to <c>packages/sounds/</c>) and loading the .ogg/.wav from the dump.
/// </summary>
public static class SoundBuilder
{
    public static Node3D Build(OgzMap map, MetadataCatalogue? catalogue, string referenceRoot)
    {
        var root = new Node3D { Name = "Sounds" };
        for (int i = 0; i < map.Entities.Count; i++)
        {
            var e = map.Entities[i];
            if (e.Type != EntityType.Sound) continue;

            string? path = catalogue?.SoundPath(e.Attr1);
            var emitter = new SoundEmitter
            {
                Name = $"Sound_{i}",
                Position = e.Position,
                SoundIndex = e.Attr1,
                SoundPath = path,
                Radius = e.Attr2,
                Size = e.Attr3,
                Stream = path != null ? LoadSound(referenceRoot, path) : null,
                MaxDistance = e.Attr2 > 0 ? e.Attr2 : 1000.0f,
                Autoplay = true,
            };
            root.AddChild(emitter);
            EntityMarker.Add(emitter, null);
        }
        return root;
    }

    private static AudioStream? LoadSound(string referenceRoot, string path)
    {
        string full = Path.Combine(referenceRoot, "packages", "sounds", path);
        if (!File.Exists(full)) return null;

        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var wav = AudioStreamWav.LoadFromFile(full);
            if (wav != null) wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            return wav;
        }
        if (path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            var ogg = AudioStreamOggVorbis.LoadFromFile(full);
            if (ogg != null) ogg.Loop = true;
            return ogg;
        }
        return null;
    }
}
