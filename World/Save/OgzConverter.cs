using System.Collections.Generic;
using System.IO;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Save;

/// <summary>
/// Converts a legacy <c>.ogz</c> map into the native format (docs/ogz-import.md,
/// unblocked by M5). This is the offline import path: parse the OGZ, wrap its octree
/// and entities in a <see cref="WorldSnapshot"/>, resolve the metadata catalogue into
/// a <see cref="ResourceManifest"/>, and write a map directory
/// (<c>world.res</c> + <c>Level.tscn</c> + <c>materials/manifest.res</c>) via
/// <see cref="MapStore"/>.
///
/// **No OGZ at runtime** — this runs once as a build-time tool; new maps use our
/// compact binary format only. The manifest stores the **full engine path** of every
/// texture, model and sound so the converted map is self-describing and does not
/// depend on the original engine's global registries.
/// </summary>
public static class OgzConverter
{
    /// <summary>
    /// Convert a parsed OGZ map and write a native map directory. When
    /// <paramref name="catalogue"/> is supplied it is baked into a
    /// <c>materials/manifest.res</c> resource manifest; without one the map is
    /// written geometry-and-entities-only.
    /// </summary>
    public static WorldSnapshot Convert(OgzMap map, string outDir, MetadataCatalogue? catalogue = null)
    {
        var entities = new List<MapEntity>(map.Entities.Count);
        foreach (var e in map.Entities) entities.Add(MapEntity.FromOgz(e));

        var snapshot = new WorldSnapshot
        {
            Scale = WorldScaleOf(map.Header.WorldSize),
            Root = map.WorldRoot,
            Entities = entities,
        };

        ManifestData? manifest = catalogue != null ? ManifestBuilder.Build(catalogue) : null;
        MapStore.Save(outDir, snapshot, history: null, compact: true, manifest: manifest);
        return snapshot;
    }

    /// <summary>
    /// Parse an OGZ file, load its metadata catalogue (by map basename, matching the
    /// committed <c>metadata-catalogue/&lt;map&gt;.json</c>), then convert it.
    /// </summary>
    public static WorldSnapshot ConvertFile(string ogzPath, string outDir, string catalogueDir = "res://metadata-catalogue")
    {
        var map = OgzReader.Read(ogzPath);
        string name = Path.GetFileNameWithoutExtension(ogzPath);
        var catalogue = TryLoadCatalogue(catalogueDir, name);
        return Convert(map, outDir, catalogue);
    }

    /// <summary>Load <c>&lt;catalogueDir&gt;/&lt;mapName&gt;.json</c>, or null if absent.</summary>
    public static MetadataCatalogue? TryLoadCatalogue(string catalogueDir, string mapName)
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath(catalogueDir), $"{mapName}.json");
        return File.Exists(path) ? MetadataCatalogue.Load(path) : null;
    }

    private static int WorldScaleOf(int worldSize)
    {
        int scale = 0;
        while ((1 << scale) < worldSize) scale++;
        return scale;
    }
}
