using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Godot;

namespace SauerbratenRevival.World.Runtime.Formats.Ogz;

/// <summary>
/// Reader for the binary OGZ map format (SVN r6889, MAPVERSION 33). Mirrors the
/// load path in src/engine/worldio.cpp up to and including the octree; lightmaps,
/// PVS and the blend map are not parsed yet.
///
/// The octree is built directly into the runtime <see cref="Cube"/>/<see cref="CubeExt"/>
/// structures, so an imported map is immediately usable by the mesher, collision,
/// and editor.
///
/// The published (incoherency.co.uk) documentation describes version 29; r6889
/// adds a <c>numvslots</c> header field and the modern <c>surfaceinfo</c> vertex
/// encoding, both handled here.
/// </summary>
public sealed class OgzReader
{
    // world.h: MAXVERSION 33; older formats use the surfacecompat path we do not support.
    public const int MapVersion = 33;
    private const int MaxEnts = 10000;

    // iengine.h MATF_* shifts (used by convertoldmaterial).
    private const int MatfVolumeShift = 2, MatfClipShift = 5, MatfFlagShift = 8;

    // texture.h VSLOT_* bit indices.
    private const int VSlotShParam = 0, VSlotScale = 1, VSlotRotation = 2, VSlotOffset = 3,
                      VSlotScroll = 4, VSlotLayer = 5, VSlotAlpha = 6, VSlotColor = 7;

    private readonly BinaryReader _r;
    private readonly int _version;

    private OgzReader(BinaryReader reader, int version)
    {
        _r = reader;
        _version = version;
    }

    /// <summary>
    /// Read and parse an OGZ file through Godot's virtual filesystem, so the map
    /// can live on disk (<c>user://</c>, an OS path) or inside a packed PCK
    /// (<c>res://</c>). Throws <see cref="FileNotFoundException"/> if it is absent.
    /// </summary>
    public static OgzMap Read(string path)
    {
        using Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)
            ?? throw new FileNotFoundException($"OGZ file not found: '{path}'", path);
        // Godot.FileAccess is not a System.IO.Stream; copy the compressed bytes and
        // reuse the gzip-decompressing Stream overload below.
        byte[] bytes = file.GetBuffer((long)file.GetLength());
        using var stream = new MemoryStream(bytes, writable: false);
        return Read(stream);
    }

    /// <summary>Read and parse an OGZ map from an already open, gzip-compressed stream.</summary>
    public static OgzMap Read(Stream stream)
    {
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.ASCII, leaveOpen: true);
        return Read(reader);
    }

    /// <summary>Read and parse a decompressed OGZ map.</summary>
    public static OgzMap Read(BinaryReader reader)
    {
        var map = new OgzMap();
        ReadHeader(reader, map.Header);
        if (map.Header.Version < 32)
            throw new NotSupportedException(
                $"OGZ version {map.Header.Version} uses the legacy surface format; only v32+ is supported.");
        var self = new OgzReader(reader, map.Header.Version);
        self.ReadVars(map);
        self.ReadGameIdent(map);
        self.ReadExtras(map);
        self.ReadTexMru(map);
        self.ReadEntities(map);
        self.ReadVSlots(map);
        map.WorldRoot = self.ReadChildren(new[] { 0, 0, 0 }, map.Header.WorldSize >> 1);
        self.ReadLightmaps(map);
        return map;
    }

    private static void ReadHeader(BinaryReader r, OgzHeader header)
    {
        header.Magic = Encoding.ASCII.GetString(ReadExact(r, 4));
        header.Version = r.ReadInt32();
        header.HeaderSize = r.ReadInt32();
        header.WorldSize = r.ReadInt32();
        header.NumEnts = r.ReadInt32();
        header.NumPvs = r.ReadInt32();
        header.Lightmaps = r.ReadInt32();

        if (header.Version <= 28)
            throw new NotSupportedException($"OGZ version {header.Version} is not supported (need >= 29).");

        header.BlendMap = r.ReadInt32();
        header.NumVars = r.ReadInt32();
        header.NumVSlots = header.Version <= 29 ? 0 : r.ReadInt32();
    }

    private void ReadVars(OgzMap map)
    {
        for (int i = 0; i < map.Header.NumVars; i++)
        {
            var v = new OgzVar { Type = _r.ReadByte() };
            int nameLen = _r.ReadUInt16();
            v.Name = ReadFixedString(nameLen);
            switch (v.Type)
            {
                case 0: v.IntValue = _r.ReadInt32(); break;
                case 1: v.FloatValue = _r.ReadSingle(); break;
                case 2:
                    int valueLen = _r.ReadUInt16();
                    v.StringValue = ReadFixedString(valueLen);
                    break;
                default:
                    throw new InvalidDataException($"unknown map variable type {v.Type}");
            }
            map.Vars.Add(v);
        }
    }

    private void ReadGameIdent(OgzMap map)
    {
        int len = _r.ReadByte();
        map.GameIdent = ReadFixedString(len + 1);
    }

    private void ReadExtras(OgzMap map)
    {
        map.ExtraEntInfoSize = _r.ReadUInt16();
        int extrasLength = _r.ReadUInt16();
        if (extrasLength > 0) ReadExact(_r, extrasLength);
    }

    private void ReadTexMru(OgzMap map)
    {
        int count = _r.ReadUInt16();
        for (int i = 0; i < count; i++) map.TexMru.Add(_r.ReadUInt16());
    }

    private void ReadEntities(OgzMap map)
    {
        bool sameGame = map.GameIdent == "fps"; // game::gameident()
        int count = Math.Min(map.Header.NumEnts, MaxEnts);

        for (int i = 0; i < count; i++)
        {
            var e = new OgzEntity
            {
                Position = new Vector3(_r.ReadSingle(), _r.ReadSingle(), _r.ReadSingle()),
                Attr1 = _r.ReadInt16(),
                Attr2 = _r.ReadInt16(),
                Attr3 = _r.ReadInt16(),
                Attr4 = _r.ReadInt16(),
                Attr5 = _r.ReadInt16(),
                Type = _r.ReadByte(),
                Reserved = _r.ReadByte(),
            };
            // fpsgame::extraentinfosize() == 0, so `sameGame` entities carry no extras.
            if (!sameGame && map.ExtraEntInfoSize > 0) ReadExact(_r, map.ExtraEntInfoSize);
            map.Entities.Add(e);
        }

        if (map.Header.NumEnts > count)
        {
            int remaining = map.Header.NumEnts - count;
            int perEnt = sameGame ? 24 : map.ExtraEntInfoSize;
            _r.BaseStream.Seek((long)remaining * perEnt, SeekOrigin.Current);
        }
    }

    private void ReadVSlots(OgzMap map)
    {
        int remaining = map.Header.NumVSlots;
        while (remaining > 0)
        {
            int changed = _r.ReadInt32();
            if (changed < 0)
            {
                for (int i = 0; i < -changed; i++) map.VSlots.Add(new OgzVSlot());
                remaining += changed;
                continue;
            }

            var vs = new OgzVSlot { Changed = changed, Prev = _r.ReadInt32() };
            if ((changed & (1 << VSlotShParam)) != 0)
            {
                int numParams = _r.ReadUInt16();
                for (int p = 0; p < numParams; p++)
                {
                    int nameLen = _r.ReadUInt16();
                    var param = new OgzShaderParam { Name = ReadFixedString(nameLen) };
                    for (int k = 0; k < 4; k++) param.Value[k] = _r.ReadSingle();
                    vs.Params.Add(param);
                }
            }
            if ((changed & (1 << VSlotScale)) != 0) vs.Scale = _r.ReadSingle();
            if ((changed & (1 << VSlotRotation)) != 0) vs.Rotation = _r.ReadInt32();
            if ((changed & (1 << VSlotOffset)) != 0) { vs.OffsetX = _r.ReadInt32(); vs.OffsetY = _r.ReadInt32(); }
            if ((changed & (1 << VSlotScroll)) != 0) { vs.ScrollX = _r.ReadSingle(); vs.ScrollY = _r.ReadSingle(); }
            if ((changed & (1 << VSlotLayer)) != 0) vs.Layer = _r.ReadInt32();
            if ((changed & (1 << VSlotAlpha)) != 0) { vs.AlphaFront = _r.ReadSingle(); vs.AlphaBack = _r.ReadSingle(); }
            if ((changed & (1 << VSlotColor)) != 0)
                vs.ColorScale = new Vector3(_r.ReadSingle(), _r.ReadSingle(), _r.ReadSingle());

            map.VSlots.Add(vs);
            remaining--;
        }
    }

    // ---- lightmaps (worldio.cpp load_world, after the octree) ----

    /// <summary>
    /// Read the baked lightmap atlases. They follow the octree and precede the
    /// PVS and blendmap sections (worldio.cpp:1219). Each atlas is:
    ///   type byte (0x80 = a 1x1 unlit/ambient texel follows, 0x10 = LM_ALPHA,
    ///   low nibble = LM_TYPE: 0 diffuse, 1 bump0, 2 bump1)
    ///   [unlitx, unlity] (ushort, only when 0x80)
    ///   bpp * 512 * 512 raw pixels, where bpp = 4 iff (LM_ALPHA &amp;&amp; type != BUMPMAP1).
    /// The pixel buffer is stored unflipped; the engine only flips it when dumping
    /// with dumplms(). See test/OgzLightmapTest.cs for the byte-exact oracle.
    /// </summary>
    private void ReadLightmaps(OgzMap map)
    {
        const int PackW = 512, PackH = 512;
        for (int i = 0; i < map.Header.Lightmaps; i++)
        {
            int type = _r.ReadByte();
            var lm = new OgzLightMap
            {
                Type = type,
                HasUnlit = (type & 0x80) != 0,
            };
            if (lm.HasUnlit)
            {
                lm.UnlitX = _r.ReadUInt16();
                lm.UnlitY = _r.ReadUInt16();
            }
            int lmType = type & 0x0F;
            lm.Bpp = (type & 0x10) != 0 && lmType != 2 ? 4 : 3; // LM_ALPHA && LM_TYPE != LM_BUMPMAP1
            lm.Pixels = ReadExact(_r, lm.Bpp * PackW * PackH);
            map.Lightmaps.Add(lm);
        }
    }

    // ---- octree (worldio.cpp loadchildren / loadc, mapversion >= 32 path) ----

    private Cube[] ReadChildren(int[] co, int size)
    {
        var cubes = new Cube[8];
        for (int i = 0; i < 8; i++)
        {
            var childCo = new[]
            {
                co[0] + ((i & 1) >> 0) * size,
                co[1] + ((i & 2) >> 1) * size,
                co[2] + ((i & 4) >> 2) * size,
            };
            cubes[i] = ReadCube(childCo, size);
        }
        return cubes;
    }

    private Cube ReadCube(int[] co, int size)
    {
        var cube = new Cube();
        int octsav = _r.ReadByte();
        int octtype = octsav & 0x7;
        bool hasChildren = false;

        switch (octtype)
        {
            case 0: // OCTSAV_CHILDREN
                cube.Children = ReadChildren(co, size >> 1);
                return cube;
            case 4: // OCTSAV_LODCUBE
                hasChildren = true;
                break;
            case 1: cube.EmptyFaces(); break;
            case 2: cube.SolidFaces(); break;
            case 3: PackEdges(cube, ReadExact(_r, 12)); break;
            default: throw new InvalidDataException($"unknown octree cube type {octtype}");
        }

        for (int j = 0; j < 6; j++) cube.Texture[j] = _r.ReadUInt16();

        // mapversion >= 32 (the modern surfaceinfo encoding)
        if ((octsav & 0x40) != 0)
            cube.Material = _version <= 32 ? ConvertOldMaterial(_r.ReadByte()) : _r.ReadUInt16();
        if ((octsav & 0x80) != 0) cube.Merged = _r.ReadByte();
        if ((octsav & 0x20) != 0) ReadSurfaces(cube, co, size);

        if (hasChildren) cube.Children = ReadChildren(co, size >> 1);
        return cube;
    }

    private void ReadSurfaces(Cube cube, int[] co, int size)
    {
        int surfmask = _r.ReadByte();
        _r.ReadByte(); // totalverts (only used for allocation in the engine)

        var ext = new CubeExt();
        var verts = new List<VertInfo>();
        for (int i = 0; i < 6; i++)
        {
            if ((surfmask & (1 << i)) == 0) continue;

            byte lmid0 = _r.ReadByte(), lmid1 = _r.ReadByte();
            byte vertmask = _r.ReadByte(), numvertsByte = _r.ReadByte();
            int numverts = (numvertsByte & Octa.LAYER_DUP) != 0
                ? (numvertsByte & Octa.MAXFACEVERTS) * 2
                : numvertsByte & Octa.MAXFACEVERTS;

            var surf = new SurfaceInfo { Lmid0 = lmid0, Lmid1 = lmid1, NumVerts = numvertsByte };
            if (numverts == 0)
            {
                ext.Surfaces[i] = surf;
                continue;
            }

            surf.Verts = (byte)verts.Count;
            verts.AddRange(ReadSurfaceVertices(cube, i, co, size, vertmask, numvertsByte, numverts));
            ext.Surfaces[i] = surf;
        }

        ext.Verts = verts.ToArray();
        cube.Ext = ext;
    }

    private VertInfo[] ReadSurfaceVertices(Cube cube, int orient, int[] co, int size, byte vertmask, byte numvertsByte, int numverts)
    {
        var verts = new VertInfo[numverts];
        int layerverts = numvertsByte & Octa.MAXFACEVERTS;
        int dim = orient >> 1;
        int vc = Octa.C[dim], vr = Octa.R[dim];
        var vo = new Vector3I((co[0] & 0xFFF) << 3, (co[1] & 0xFFF) << 3, (co[2] & 0xFFF) << 3);
        System.Span<Vector3I> v = stackalloc Vector3I[4];
        CubeGeometry.GenFaceVerts(cube, orient, v);
        Vector3I n = Vector3I.Zero;
        int bias = 0;

        bool hasxyz = (vertmask & 0x04) != 0;
        bool hasuv = (vertmask & 0x40) != 0;
        bool hasnorm = (vertmask & 0x80) != 0;

        if (hasxyz)
        {
            n = CubeGeometry.Cross(v[1] - v[0], v[2] - v[0]);
            if (n == Vector3I.Zero) n = CubeGeometry.Cross(v[2] - v[0], v[3] - v[0]);
            bias = -CubeGeometry.Dot(n, v[0] * size + vo);
        }
        else
        {
            int vis = layerverts < 4 ? (((vertmask & 0x02) != 0) ? 2 : 1) : 3;
            int order = (vertmask & 0x01) != 0 ? 1 : 0;
            int k = 0;
            SetXyz(verts, k++, v[order], size, vo);
            if ((vis & 1) != 0) SetXyz(verts, k++, v[(order + 1) & 3], size, vo);
            SetXyz(verts, k++, v[(order + 2) & 3], size, vo);
            if ((vis & 2) != 0) SetXyz(verts, k++, v[(order + 3) & 3], size, vo);
        }

        if (layerverts == 4)
        {
            if (hasxyz && (vertmask & 0x01) != 0)
            {
                ushort c1 = _r.ReadUInt16(), r1 = _r.ReadUInt16(), c2 = _r.ReadUInt16(), r2 = _r.ReadUInt16();
                SetXyzPlane(verts, 0, c1, r1, vc, vr, dim, n, bias, vo);
                SetXyzPlane(verts, 1, c1, r2, vc, vr, dim, n, bias, vo);
                SetXyzPlane(verts, 2, c2, r2, vc, vr, dim, n, bias, vo);
                SetXyzPlane(verts, 3, c2, r1, vc, vr, dim, n, bias, vo);
                hasxyz = false;
            }
            if (hasuv && (vertmask & 0x02) != 0)
            {
                int uvorder = (vertmask & 0x30) >> 4;
                ref VertInfo v0 = ref verts[uvorder];
                ref VertInfo v1 = ref verts[(uvorder + 1) & 3];
                ref VertInfo v2 = ref verts[(uvorder + 2) & 3];
                ref VertInfo v3 = ref verts[(uvorder + 3) & 3];
                v0.U = _r.ReadUInt16(); v0.V = _r.ReadUInt16();
                v2.U = _r.ReadUInt16(); v2.V = _r.ReadUInt16();
                v1.U = v0.U; v1.V = v2.V;
                v3.U = v2.U; v3.V = v0.V;
                if ((numvertsByte & Octa.LAYER_DUP) != 0)
                {
                    ref VertInfo b0 = ref verts[4 + uvorder];
                    ref VertInfo b1 = ref verts[4 + ((uvorder + 1) & 3)];
                    ref VertInfo b2 = ref verts[4 + ((uvorder + 2) & 3)];
                    ref VertInfo b3 = ref verts[4 + ((uvorder + 3) & 3)];
                    b0.U = _r.ReadUInt16(); b0.V = _r.ReadUInt16();
                    b2.U = _r.ReadUInt16(); b2.V = _r.ReadUInt16();
                    b1.U = b0.U; b1.V = b2.V;
                    b3.U = b2.U; b3.V = b0.V;
                }
                hasuv = false;
            }
        }

        if (hasnorm && (vertmask & 0x08) != 0)
        {
            ushort norm = _r.ReadUInt16();
            for (int k = 0; k < layerverts; k++) verts[k].Norm = norm;
            hasnorm = false;
        }

        if (hasxyz || hasuv || hasnorm)
        {
            for (int k = 0; k < layerverts; k++)
            {
                if (hasxyz)
                {
                    int c = _r.ReadUInt16(), r = _r.ReadUInt16();
                    SetXyzPlane(verts, k, c, r, vc, vr, dim, n, bias, vo);
                }
                if (hasuv) { verts[k].U = _r.ReadUInt16(); verts[k].V = _r.ReadUInt16(); }
                if (hasnorm) verts[k].Norm = _r.ReadUInt16();
            }
        }

        if ((numvertsByte & Octa.LAYER_DUP) != 0)
        {
            for (int k = 0; k < layerverts; k++)
            {
                ref VertInfo dv = ref verts[k + layerverts];
                VertInfo t = verts[k];
                dv.X = t.X; dv.Y = t.Y; dv.Z = t.Z;
                if (hasuv) { dv.U = _r.ReadUInt16(); dv.V = _r.ReadUInt16(); }
                dv.Norm = t.Norm;
            }
        }

        return verts;
    }

    private static void PackEdges(Cube cube, byte[] edges)
    {
        for (int d = 0; d < 3; d++)
            cube.Faces[d] =
                (uint)edges[d * 4] |
                ((uint)edges[d * 4 + 1] << 8) |
                ((uint)edges[d * 4 + 2] << 16) |
                ((uint)edges[d * 4 + 3] << 24);
    }

    // ---- surface-vertex helpers (worldio.cpp loadsurface, mapversion >= 32) ----

    private static void SetXyz(VertInfo[] verts, int k, Vector3I v, int size, Vector3I vo)
    {
        verts[k].X = (ushort)(v.X * size + vo.X);
        verts[k].Y = (ushort)(v.Y * size + vo.Y);
        verts[k].Z = (ushort)(v.Z * size + vo.Z);
    }

    private static void SetXyzPlane(VertInfo[] verts, int k, int c, int r, int vc, int vr, int dim, Vector3I n, int bias, Vector3I vo)
    {
        System.Span<int> xyz = stackalloc int[3];
        xyz[vc] = c;
        xyz[vr] = r;
        int nd = Component(n, dim);
        xyz[dim] = nd != 0
            ? -(bias + Component(n, vc) * xyz[vc] + Component(n, vr) * xyz[vr]) / nd
            : Component(vo, dim);
        verts[k].X = (ushort)xyz[0];
        verts[k].Y = (ushort)xyz[1];
        verts[k].Z = (ushort)xyz[2];
    }

    private static int Component(Vector3I v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static ushort ConvertOldMaterial(int mat) =>
        (ushort)(((mat & 7) << MatfVolumeShift) | (((mat >> 3) & 3) << MatfClipShift) | (((mat >> 5) & 7) << MatfFlagShift));

    private string ReadFixedString(int len)
    {
        if (len <= 0) return "";
        var bytes = ReadExact(_r, len);
        int end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, end);
    }

    private static byte[] ReadExact(BinaryReader r, int count)
    {
        var bytes = r.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException($"expected {count} bytes, got {bytes.Length}");
        return bytes;
    }
}
