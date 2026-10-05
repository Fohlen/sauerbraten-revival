using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Godot;

namespace SauerbratenRevival.World.Ogz;

/// <summary>
/// Reader for the binary OGZ map format (SVN r6889, MAPVERSION 33). Mirrors the
/// load path in src/engine/worldio.cpp up to and including the octree; lightmaps,
/// PVS and the blend map are not parsed yet.
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

    /// <summary>Read and parse an OGZ file from disk.</summary>
    public static OgzMap Read(string path)
    {
        using var file = File.OpenRead(path);
        return Read(file);
    }

    /// <summary>Read and parse an OGZ map from an already open, gzip-compressed stream.</summary>
    public static OgzMap Read(Stream stream)
    {
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.ASCII, leaveOpen: true);
        return Read(reader);
    }

    /// <summary>Read and parse an OGZ map from a decompressed stream.</summary>
    public static OgzMap Read(BinaryReader reader)
    {
        var map = new OgzMap();
        ReadHeader(reader, map.Header);
        var self = new OgzReader(reader, map.Header.Version);
        self.ReadVars(map);
        self.ReadGameIdent(map);
        self.ReadExtras(map);
        self.ReadTexMru(map);
        self.ReadEntities(map);
        self.ReadVSlots(map);
        map.WorldRoot = self.ReadChildren(new[] { 0, 0, 0 }, map.Header.WorldSize >> 1);
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
        int infoSize = 0;                        // fpsgame::extraentinfosize() == 0
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
            if (sameGame)
            {
                if (infoSize > 0) ReadExact(_r, infoSize);
            }
            else if (map.ExtraEntInfoSize > 0) ReadExact(_r, map.ExtraEntInfoSize);
            map.Entities.Add(e);
        }

        if (map.Header.NumEnts > count)
        {
            int remaining = map.Header.NumEnts - count;
            int perEnt = sameGame ? 24 + infoSize : map.ExtraEntInfoSize;
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

    // ---- octree (worldio.cpp loadchildren / loadc, mapversion >= 32 path) ----

    private OgzCube[] ReadChildren(int[] co, int size)
    {
        var cubes = new OgzCube[8];
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

    private OgzCube ReadCube(int[] co, int size)
    {
        var cube = new OgzCube();
        int octsav = _r.ReadByte();
        int octtype = octsav & 0x7;
        bool hasChildren = false;

        switch (octtype)
        {
            case 0: // OCTSAV_CHILDREN
                cube.Type = OgzCubeType.Children;
                cube.Children = ReadChildren(co, size >> 1);
                return cube;
            case 4: // OCTSAV_LODCUBE
                cube.Type = OgzCubeType.Lod;
                hasChildren = true;
                break;
            case 1: cube.Type = OgzCubeType.Empty; break;
            case 2: cube.Type = OgzCubeType.Solid; break;
            case 3: cube.Type = OgzCubeType.Normal; cube.Edges = ReadExact(_r, 12); break;
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

    private void ReadSurfaces(OgzCube cube, int[] co, int size)
    {
        int surfmask = _r.ReadByte();
        _r.ReadByte(); // totalverts (only used for allocation in the engine)

        cube.Surfaces = new OgzSurface?[6];
        int offset = 0;
        for (int i = 0; i < 6; i++)
        {
            if ((surfmask & (1 << i)) == 0) continue;

            var surf = new OgzSurface
            {
                Lmid0 = _r.ReadByte(),
                Lmid1 = _r.ReadByte(),
                VertMask = _r.ReadByte(),
                NumVerts = _r.ReadByte(),
            };
            int numverts = surf.TotalVerts;
            if (numverts == 0)
            {
                surf.VertOffset = 0;
                cube.Surfaces[i] = surf;
                continue;
            }
            surf.VertOffset = offset;
            offset += numverts;
            surf.Vertices = ReadSurfaceVertices(cube, i, co, size, surf, numverts);
            cube.Surfaces[i] = surf;
        }
    }

    private VertInfo[] ReadSurfaceVertices(OgzCube cube, int orient, int[] co, int size, OgzSurface surf, int numverts)
    {
        var verts = new VertInfo[numverts];
        int vertmask = surf.VertMask;
        int layerverts = surf.NumVerts & Octa.MAXFACEVERTS;
        int dim = orient >> 1;
        int vc = Octa.C[dim], vr = Octa.R[dim];
        int[] vo = { (co[0] & 0xFFF) << 3, (co[1] & 0xFFF) << 3, (co[2] & 0xFFF) << 3 };
        int[][] v = GenFaceVerts(cube, orient);
        int[] n = { 0, 0, 0 };
        int bias = 0;

        bool hasxyz = (vertmask & 0x04) != 0;
        bool hasuv = (vertmask & 0x40) != 0;
        bool hasnorm = (vertmask & 0x80) != 0;

        if (hasxyz)
        {
            n = Cross(Sub(v[1], v[0]), Sub(v[2], v[0]));
            if (IsZero(n)) n = Cross(Sub(v[2], v[0]), Sub(v[3], v[0]));
            bias = -Dot(n, new[] { v[0][0] * size + vo[0], v[0][1] * size + vo[1], v[0][2] * size + vo[2] });
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
                if ((surf.NumVerts & Octa.LAYER_DUP) != 0)
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

        if ((surf.NumVerts & Octa.LAYER_DUP) != 0)
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

    // genfaceverts (octa.cpp): face corner bits, with x1/y1/z1 mapped to 1.
    // orient -> [vert][axis], axis = x/y/z in {0,1}.
    private static readonly int[][][] FaceCorner =
    {
        new[] { new[] { 0, 1, 1 }, new[] { 0, 1, 0 }, new[] { 0, 0, 0 }, new[] { 0, 0, 1 } }, // -X
        new[] { new[] { 1, 1, 1 }, new[] { 1, 0, 1 }, new[] { 1, 0, 0 }, new[] { 1, 1, 0 } }, // +X
        new[] { new[] { 1, 0, 1 }, new[] { 0, 0, 1 }, new[] { 0, 0, 0 }, new[] { 1, 0, 0 } }, // -Y
        new[] { new[] { 0, 1, 0 }, new[] { 0, 1, 1 }, new[] { 1, 1, 1 }, new[] { 1, 1, 0 } }, // +Y
        new[] { new[] { 0, 0, 0 }, new[] { 0, 1, 0 }, new[] { 1, 1, 0 }, new[] { 1, 0, 0 } }, // -Z
        new[] { new[] { 0, 0, 1 }, new[] { 1, 0, 1 }, new[] { 1, 1, 1 }, new[] { 0, 1, 1 } }, // +Z
    };

    private static int[][] GenFaceVerts(OgzCube c, int orient)
    {
        var res = new int[4][];
        for (int n = 0; n < 4; n++)
        {
            int x = FaceCorner[orient][n][0], y = FaceCorner[orient][n][1], z = FaceCorner[orient][n][2];
            res[n] = new[]
            {
                EdgeGet(c.Edges[z * 2 + y], x),
                EdgeGet(c.Edges[4 + x * 2 + z], y),
                EdgeGet(c.Edges[8 + y * 2 + x], z),
            };
        }
        return res;
    }

    private static int EdgeGet(byte edge, int coord) => coord != 0 ? (edge >> 4) & 0xF : edge & 0xF;

    private static void SetXyz(VertInfo[] verts, int k, int[] v, int size, int[] vo)
    {
        verts[k].X = (ushort)(v[0] * size + vo[0]);
        verts[k].Y = (ushort)(v[1] * size + vo[1]);
        verts[k].Z = (ushort)(v[2] * size + vo[2]);
    }

    private static void SetXyzPlane(VertInfo[] verts, int k, int c, int r, int vc, int vr, int dim, int[] n, int bias, int[] vo)
    {
        int[] xyz = new int[3];
        xyz[vc] = c;
        xyz[vr] = r;
        xyz[dim] = n[dim] != 0 ? -(bias + n[vc] * xyz[vc] + n[vr] * xyz[vr]) / n[dim] : vo[dim];
        verts[k].X = (ushort)xyz[0];
        verts[k].Y = (ushort)xyz[1];
        verts[k].Z = (ushort)xyz[2];
    }

    private static int[] Cross(int[] a, int[] b) =>
        new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

    private static int Dot(int[] a, int[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static int[] Sub(int[] a, int[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

    private static bool IsZero(int[] a) => a[0] == 0 && a[1] == 0 && a[2] == 0;

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
