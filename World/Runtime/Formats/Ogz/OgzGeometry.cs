using System.Collections.Generic;

namespace SauerbratenRevival.World.Runtime.Formats.Ogz;

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
/// tools/ogztool/patches/ogz-metadata.patch (engine/metadata.cpp).
/// </summary>
public static class OgzGeometry
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static OgzGeometryStats Compute(Cube[] root)
    {
        var stats = new OgzGeometryStats { Checksum = FnvOffset };
        Walk(root, root.Length, stats);
        return stats;
    }

    private static void Walk(Cube[] cubes, int n, OgzGeometryStats stats)
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
            if (c.IsEmpty()) { type = 1; stats.Empty++; }
            else if (c.IsEntirelySolid()) { type = 2; stats.Solid++; }
            else { type = 3; stats.Normal++; }

            if (c.Ext != null)
            {
                for (int j = 0; j < 6; j++)
                {
                    var surf = c.Ext.Surfaces[j];
                    int totalVerts = surf.TotalVerts();
                    if (totalVerts > 0)
                    {
                        stats.Surfaces++;
                        // Baked-lightmap link, hashed exactly as engine/metadata.cpp:
                        // both lmid layers, the raw numverts byte, and each vertex's
                        // atlas UV + smooth normal. Only faces with geometry are
                        // hashed; the engine allocates empty cubeexts during render
                        // setup whose zero-vertex faces are not in the file.
                        HashByte(stats, surf.Lmid0);
                        HashByte(stats, surf.Lmid1);
                        HashByte(stats, surf.NumVerts);
                        for (int k = 0; k < totalVerts; k++)
                        {
                            var vert = c.Ext.Verts[surf.Verts + k];
                            HashUShort(stats, vert.U);
                            HashUShort(stats, vert.V);
                            HashUShort(stats, vert.Norm);
                        }
                    }
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
                for (int j = 0; j < 12; j++)
                    HashByte(stats, (byte)((c.Faces[j / 4] >> ((j % 4) * 8)) & 0xFF));
        }
    }

    private static void HashByte(OgzGeometryStats s, byte b) => s.Checksum = (s.Checksum ^ b) * FnvPrime;

    private static void HashUShort(OgzGeometryStats s, ushort v)
    {
        HashByte(s, (byte)(v & 0xFF));
        HashByte(s, (byte)((v >> 8) & 0xFF));
    }
}
