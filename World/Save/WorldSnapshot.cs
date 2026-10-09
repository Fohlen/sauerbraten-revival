using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Save;

/// <summary>
/// A serializable, canonical snapshot of a world: the octree plus the entity list
/// (docs/editor-and-commands.md §4, §7). This is the payload behind <c>world.res</c>
/// and the unit of multiplayer parity hashing (§3.4, §7.4).
///
/// **Canonical**: a fixed, child-index-ordered traversal (0..7 at every node), and
/// a per-cube flag byte that marks which optional fields are present. Two logically
/// identical worlds encode to identical bytes, so the content hash is meaningful.
///
/// **Versioned**: a leading format version + world scale lets an older build
/// recognise a newer file and refuse (or skip) rather than misread it (§7.3).
///
/// **Baked render data is stored (v2).** <see cref="CubeExt"/> (per-face
/// <see cref="SurfaceInfo"/> + <see cref="VertInfo"/>) carries the baked-lightmap
/// link — which atlas each face layer samples and the per-vertex atlas UV + smooth
/// normal. That data is **not derivable** from the face/edge data, so it is
/// persisted (docs/lighting.md §3.2): v1 dropped it, which silently lost the
/// lightmap link on any native save. <c>Merged</c>/<c>Visible</c> are stored
/// because <see cref="OctaEdit.Remip"/> consults them.
///
/// v2 adds the optional per-cube surface section. It is a **breaking** change
/// (v1 files lack the section and would decode wrong), so the version guard
/// rejects v1.
public sealed class WorldSnapshot
{
    /// <summary>Bump when the octree/blob layout changes incompatibly.</summary>
    public const uint FormatVersion = 2;

    // Magic so a truncated/mismatched blob is rejected early.
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SRWS"); // SauerbratenReVival World Snapshot

    // Per-cube flag bits.
    private const byte FlagChildren = 1 << 0;
    private const byte FlagMaterial = 1 << 1;
    private const byte FlagMerged = 1 << 2;
    private const byte FlagVisible = 1 << 3;
    private const byte FlagSurface = 1 << 4;

    /// <summary>World scale (worldsize = 1 &lt;&lt; Scale).</summary>
    public int Scale;

    /// <summary>The octree roots (world root array, 8 cubes; each may have children).</summary>
    public Cube[] Root = Array.Empty<Cube>();

    /// <summary>The entities placed in the world (geometry commands remain the authority; §7.2).</summary>
    public List<MapEntity> Entities = new();

    /// <summary>Build a snapshot from a live octree (entities supplied separately; editor worlds have none yet).</summary>
    public static WorldSnapshot Capture(Octa octa, IEnumerable<MapEntity>? entities = null)
    {
        var snap = new WorldSnapshot { Scale = octa.WorldScale, Root = octa.WorldRoot };
        if (entities != null) snap.Entities.AddRange(entities);
        return snap;
    }

    /// <summary>Materialize a live octree from the snapshot.</summary>
    public Octa ToOcta() =>
        new(Scale, 1 << Scale, Root, CountAllocNodes(Root));

    private static int CountAllocNodes(Cube[] cubes)
    {
        int count = 1; // the root block
        foreach (var c in cubes)
            if (c.Children != null) count += CountAllocNodes(c.Children);
        return count;
    }

    // ---- encoding ----

    /// <summary>Encode to the canonical binary blob stored in <c>world.res</c> / sent on late join.</summary>
    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(Scale);
        WriteCubes(w, Root);

        w.Write(Entities.Count);
        foreach (var e in Entities) e.Write(w);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Decode a blob produced by <see cref="Encode"/>. Rejects unknown versions.</summary>
    public static WorldSnapshot Decode(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var magic = r.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !System.Linq.Enumerable.SequenceEqual(magic, Magic))
            throw new InvalidDataException("Not a world snapshot (bad magic).");

        uint version = r.ReadUInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported world snapshot version {version} (expected {FormatVersion}).");

        var snap = new WorldSnapshot { Scale = r.ReadInt32() };
        snap.Root = ReadCubes(r);

        int entityCount = r.ReadInt32();
        if (entityCount < 0) throw new InvalidDataException("Negative entity count.");
        for (int i = 0; i < entityCount; i++) snap.Entities.Add(MapEntity.Read(r));

        return snap;
    }

    /// <summary>Content hash of the *semantic* fields, walked in a fixed order (§5.1).</summary>
    public ulong Hash()
    {
        ulong h = 14695981039346656037UL;
        void MixByte(byte b) { h ^= b; h *= 1099511628211UL; }
        void MixU32(uint v) { for (int i = 0; i < 4; i++) MixByte((byte)(v >> (i * 8))); }

        MixU32((uint)Scale);

        void Walk(Cube[] cubes)
        {
            for (int i = 0; i < cubes.Length; i++)
            {
                var c = cubes[i];
                bool hasChildren = c.Children != null;
                MixByte((byte)(hasChildren ? 1 : 0));
                if (hasChildren)
                {
                    Walk(c.Children!);
                    continue;
                }
                for (int f = 0; f < 3; f++) MixU32(c.Faces[f]);
                for (int t = 0; t < 6; t++) { MixByte((byte)c.Texture[t]); MixByte((byte)(c.Texture[t] >> 8)); }
                MixByte((byte)c.Material); MixByte((byte)(c.Material >> 8));
                MixByte(c.Merged);
                MixByte(c.Visible);
                // Baked-lightmap link. Only faces with geometry are mixed, matching
                // the engine oracle (OgzGeometry / engine/metadata.cpp): the engine
                // allocates empty cubeexts during render setup whose zero-vertex
                // faces are not map content.
                if (c.Ext != null)
                {
                    for (int f = 0; f < 6; f++)
                    {
                        var surf = c.Ext.Surfaces[f];
                        int total = surf.TotalVerts();
                        if (total == 0) continue;
                        MixByte(surf.Lmid0);
                        MixByte(surf.Lmid1);
                        MixByte(surf.NumVerts);
                        for (int k = 0; k < total; k++)
                        {
                            var v = c.Ext.Verts[surf.Verts + k];
                            MixByte((byte)v.U); MixByte((byte)(v.U >> 8));
                            MixByte((byte)v.V); MixByte((byte)(v.V >> 8));
                            MixByte((byte)v.Norm); MixByte((byte)(v.Norm >> 8));
                        }
                    }
                }
            }
        }

        Walk(Root);
        foreach (var e in Entities) e.Hash(MixByte);
        return h;
    }

    // ---- octree ----

    private static void WriteCubes(BinaryWriter w, Cube[] cubes)
    {
        foreach (var c in cubes) WriteCube(w, c);
    }

    private static Cube[] ReadCubes(BinaryReader r)
    {
        var cubes = new Cube[8];
        for (int i = 0; i < 8; i++) cubes[i] = ReadCube(r);
        return cubes;
    }

    private static void WriteCube(BinaryWriter w, Cube c)
    {
        byte flags = 0;
        if (c.Children != null) flags |= FlagChildren;
        if (c.Material != Octa.MAT_AIR) flags |= FlagMaterial;
        if (c.Merged != 0) flags |= FlagMerged;
        if (c.Visible != 0) flags |= FlagVisible;
        if (c.Ext != null) flags |= FlagSurface;
        w.Write(flags);

        // Faces are always written (12 edge bytes); the common empty/solid cube is
        // cheap and the codec stays branch-free.
        w.Write(c.Faces[0]);
        w.Write(c.Faces[1]);
        w.Write(c.Faces[2]);
        for (int i = 0; i < 6; i++) w.Write(c.Texture[i]);
        if ((flags & FlagMaterial) != 0) w.Write(c.Material);
        if ((flags & FlagMerged) != 0) w.Write(c.Merged);
        if ((flags & FlagVisible) != 0) w.Write(c.Visible);
        if ((flags & FlagSurface) != 0) WriteSurfaces(w, c.Ext!);

        if ((flags & FlagChildren) != 0) WriteCubes(w, c.Children!);
    }

    private static Cube ReadCube(BinaryReader r)
    {
        var c = new Cube();
        byte flags = r.ReadByte();
        c.Faces[0] = r.ReadUInt32();
        c.Faces[1] = r.ReadUInt32();
        c.Faces[2] = r.ReadUInt32();
        for (int i = 0; i < 6; i++) c.Texture[i] = r.ReadUInt16();
        if ((flags & FlagMaterial) != 0) c.Material = r.ReadUInt16();
        if ((flags & FlagMerged) != 0) c.Merged = r.ReadByte();
        if ((flags & FlagVisible) != 0) c.Visible = r.ReadByte();
        if ((flags & FlagSurface) != 0) c.Ext = ReadSurfaces(r);
        if ((flags & FlagChildren) != 0) c.Children = ReadCubes(r);
        return c;
    }

    // ---- per-cube surface section (baked-lightmap link) ----

    // 6 surfaceinfo records (lmid0, lmid1, verts, numverts) + a ushort vertex count
    // + that many vertinfo records (x,y,z,u,v,norm). `maxverts`/`tjoints` are
    // runtime caches (recomputed/derived), so they are not stored.
    private static void WriteSurfaces(BinaryWriter w, CubeExt ext)
    {
        for (int f = 0; f < 6; f++)
        {
            var s = ext.Surfaces[f];
            w.Write(s.Lmid0);
            w.Write(s.Lmid1);
            w.Write(s.Verts);
            w.Write(s.NumVerts);
        }
        w.Write((ushort)ext.Verts.Length);
        foreach (var v in ext.Verts)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            w.Write(v.U); w.Write(v.V); w.Write(v.Norm);
        }
    }

    private static CubeExt ReadSurfaces(BinaryReader r)
    {
        var ext = new CubeExt();
        for (int f = 0; f < 6; f++)
        {
            ext.Surfaces[f] = new SurfaceInfo
            {
                Lmid0 = r.ReadByte(),
                Lmid1 = r.ReadByte(),
                Verts = r.ReadByte(),
                NumVerts = r.ReadByte(),
            };
        }
        int count = r.ReadUInt16();
        var verts = new VertInfo[count];
        for (int i = 0; i < count; i++)
        {
            verts[i] = new VertInfo
            {
                X = r.ReadUInt16(), Y = r.ReadUInt16(), Z = r.ReadUInt16(),
                U = r.ReadUInt16(), V = r.ReadUInt16(), Norm = r.ReadUInt16(),
            };
        }
        ext.Verts = verts;
        return ext;
    }
}
