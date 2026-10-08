using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Save;

/// <summary>
/// Converts a legacy <c>.ogz</c> map into the native format (docs/ogz-import.md,
/// unblocked by M5). This is the offline import path: parse the OGZ, wrap its octree
/// and entities in a <see cref="WorldSnapshot"/>, and write a map directory
/// (<c>world.res</c> + <c>Level.tscn</c>) via <see cref="MapStore"/>.
///
/// **No OGZ at runtime** — this runs once as a build-time tool; new maps use our
/// compact binary format only. Materials are a separate manifest (§7.1) and are not
/// emitted here yet; texture indices survive as-is and resolve through the manifest.
/// </summary>
public static class OgzConverter
{
    /// <summary>Convert a parsed OGZ map and write a native map directory.</summary>
    public static WorldSnapshot Convert(OgzMap map, string outDir)
    {
        var entities = new List<MapEntity>(map.Entities.Count);
        foreach (var e in map.Entities) entities.Add(MapEntity.FromOgz(e));

        var snapshot = new WorldSnapshot
        {
            Scale = WorldScaleOf(map.Header.WorldSize),
            Root = map.WorldRoot,
            Entities = entities,
        };

        MapStore.Save(outDir, snapshot, history: null, compact: true);
        return snapshot;
    }

    /// <summary>Convenience: parse an OGZ file, then convert it.</summary>
    public static WorldSnapshot ConvertFile(string ogzPath, string outDir) =>
        Convert(OgzReader.Read(ogzPath), outDir);

    private static int WorldScaleOf(int worldSize)
    {
        int scale = 0;
        while ((1 << scale) < worldSize) scale++;
        return scale;
    }
}
