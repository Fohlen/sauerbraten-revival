using System.Collections.Generic;

namespace SauerbratenRevival.World.Ogz;

/// <summary>Geometry ground truth, matching the <c>geometry</c> block the engine patch exports.</summary>
public sealed class OgzGeometryStats
{
    public int Nodes;
    public int Leaves;
    public int Empty;
    public int Solid;
    public int Normal;
    public int Surfaces;
    public int Verts;
    public ulong Checksum;
    public SortedDictionary<int, int> TextureFaces = new();
}

/// <summary>
/// Computes the same geometry summary the <c>ogz-metadata.patch</c> exporter
/// writes, so a parsed map can be asserted against the engine's ground truth.
/// The checksum is FNV-1a/64 over the canonical cube fields; see
/// tools/refharness/patches/ogz-metadata.patch (engine/metadata.cpp).
/// </summary>
public static class OgzGeometry
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static OgzGeometryStats Compute(OgzCube[] root)
    {
        var stats = new OgzGeometryStats { Checksum = FnvOffset };
        Walk(root, root.Length, stats);
        return stats;
    }

    private static void Walk(OgzCube[] cubes, int n, OgzGeometryStats stats)
    {
        for (int i = 0; i < n; i++)
        {
            var c = cubes[i];
            if (c.Children != null)
            {
                stats.Nodes++;
                HashByte(stats, 0);
                Walk(c.Children, c.Children.Length, stats);
                continue;
            }

            stats.Leaves++;
            byte type;
            if (c.Type == OgzCubeType.Empty) { type = 1; stats.Empty++; }
            else if (c.Type == OgzCubeType.Solid) { type = 2; stats.Solid++; }
            else { type = 3; stats.Normal++; }

            if (c.Surfaces != null)
            {
                for (int j = 0; j < 6; j++)
                {
                    var surf = c.Surfaces[j];
                    if (surf == null) continue;
                    int totalVerts = surf.TotalVerts;
                    if (totalVerts > 0) stats.Surfaces++;
                    stats.Verts += totalVerts;
                }
            }

            HashByte(stats, type);
            HashUShort(stats, c.Material);
            for (int j = 0; j < 6; j++)
            {
                ushort t = c.Texture[j];
                stats.TextureFaces.TryGetValue(t, out int count);
                stats.TextureFaces[t] = count + 1;
                HashUShort(stats, t);
            }
            if (type == 3)
                for (int j = 0; j < 12; j++) HashByte(stats, c.Edges[j]);
        }
    }

    private static void HashByte(OgzGeometryStats s, byte b) => s.Checksum = (s.Checksum ^ b) * FnvPrime;

    private static void HashUShort(OgzGeometryStats s, ushort v)
    {
        HashByte(s, (byte)(v & 0xFF));
        HashByte(s, (byte)((v >> 8) & 0xFF));
    }
}
