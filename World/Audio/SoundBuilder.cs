using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.World.Audio;

/// <summary>
/// Builds <see cref="SoundEmitter"/> nodes from a map's <c>ET_SOUND</c> entities,
/// resolving each <c>mapsound</c> through the metadata catalogue (paths are relative
/// to <c>packages/sounds/</c>). Files are loaded through <see cref="SoundFiles"/>,
/// which prefers the shipped <c>res://assets/sounds/</c> pack (so ambience works from
/// a packed PCK) and falls back to the dev-only reference dump.
/// </summary>
public static class SoundBuilder
{
    public static Node3D Build(OgzMap map, MetadataCatalogue? catalogue) => Build(map.Entities, catalogue);

    public static Node3D Build<T>(IReadOnlyList<T> entities, MetadataCatalogue? catalogue) where T : IEntityData
    {
        var root = new Node3D { Name = "Sounds" };
        foreach (var (i, e) in entities.OfType(EntityType.Sound))
        {
            string? path = catalogue?.SoundPath(e.Attr1);
            string? file = path != null ? SoundFiles.Resolve(path) : null;
            var emitter = new SoundEmitter
            {
                Name = $"Sound_{i}",
                Position = e.Position,
                SoundIndex = e.Attr1,
                SoundPath = path,
                Radius = e.Attr2,
                Size = e.Attr3,
                Stream = file != null ? SoundFiles.Load(file, loop: true) : null,
                MaxDistance = e.Attr2 > 0 ? e.Attr2 : 1000.0f,
                Autoplay = true,
            };
            root.AddChild(emitter);
            EntityMarker.Add(emitter, null);
        }
        return root;
    }
}
