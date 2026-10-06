using Godot;

namespace SauerbratenRevival.World;

// Core world octree — a C# port of src/engine/octa.h + src/engine/octa.cpp.
//
// Identifiers keep the original word tokens (OctaStep, IsEmpty, NewCubes,
// LookupMaterial, Cube, CubeExt, ...) but use C# PascalCase, so they stay
// greppable against the C++ source. File layout mirrors Lee's split:
// Octa / OctaEdit / OctaRender / WorldIo.
//
// Divergences from the original, documented for future reference:
//   * `Cube` is a reference type (class) with `Children` as a managed array,
//     not a POD struct in a contiguous `cube[8]` block. This is the C.1
//     decision: simple/readable first; move to an index pool if profiling
//     demands. GC/heap behaviour therefore differs from the original.
//   * The `edges[12]` / `faces[3]` union is stored as three uint face words
//     (`Faces3`), with byte-level edge access via CubeEdge/SetCubeEdge. C#
//     cannot alias the same storage, so `edgeset(cubeedge(...), ...)` (which
//     mutated through a C++ lvalue) becomes get + SetCubeEdge.
//   * `vec`/`ivec` are Godot's Vector3 / Vector3I (decision D), not the
//     original's float/int vectors.

/// <summary>octa.h: <c>struct cube</c> face word storage (the faces[3] half of the union).</summary>
public struct Faces3
{
    public uint F0, F1, F2;

    public uint this[int d]
    {
        get => d switch { 0 => F0, 1 => F1, _ => F2 };
        set { if (d == 0) F0 = value; else if (d == 1) F1 = value; else F2 = value; }
    }
}

/// <summary>octa.h: <c>ushort texture[6]</c>, inlined so a cube allocates no arrays.</summary>
public struct Texture6
{
    public ushort T0, T1, T2, T3, T4, T5;

    public ushort this[int i]
    {
        get => i switch { 0 => T0, 1 => T1, 2 => T2, 3 => T3, 4 => T4, _ => T5 };
        set
        {
            switch (i)
            {
                case 0: T0 = value; break;
                case 1: T1 = value; break;
                case 2: T2 = value; break;
                case 3: T3 = value; break;
                case 4: T4 = value; break;
                default: T5 = value; break;
            }
        }
    }
}

/// <summary>octa.h: face orientations.</summary>
public enum Orient { Left = 0, Right, Back, Front, Bottom, Top }

/// <summary>octa.h: <c>struct cube</c>.</summary>
public sealed class Cube
{
    public Cube[]? Children { get; set; }   // 8 children, or null
    public CubeExt? Ext { get; set; }        // extended info, or null
    // Faces/Texture are mutable inline structs. C# cannot mutate a struct
    // through a property (the getter returns a copy), so these two are public
    // FIELDS, not properties — the one deviation from the E decision.
    public Faces3 Faces;                     // union faces[3] / edges[12]
    public Texture6 Texture;                 // texture[6], one per orient
    public ushort Material { get; set; }     // empty-space material
    public byte Merged { get; set; }
    public byte Visible { get; set; }        // union escaped/visible

    /// <summary>octa.h: <c>isempty(c)</c>.</summary>
    public bool IsEmpty() => Faces[0] == Octa.F_EMPTY;

    /// <summary>octa.h: <c>isentirelysolid(c)</c>.</summary>
    public bool IsEntirelySolid() =>
        Faces[0] == Octa.F_SOLID && Faces[1] == Octa.F_SOLID && Faces[2] == Octa.F_SOLID;

    /// <summary>octa.h: <c>setfaces(c, face)</c>.</summary>
    public void SetFaces(uint face) { Faces[0] = face; Faces[1] = face; Faces[2] = face; }

    /// <summary>octa.h: <c>solidfaces(c)</c>.</summary>
    public void SolidFaces() => SetFaces(Octa.F_SOLID);

    /// <summary>octa.h: <c>emptyfaces(c)</c>.</summary>
    public void EmptyFaces() => SetFaces(Octa.F_EMPTY);

    // octa.h: cubeedge(c, d, x, y) -> byte (y*2+x) of face word d, which is the
    // byte-level view of edges[12].
    public byte CubeEdge(int d, int x, int y)
    {
        uint f = Faces[d];
        int shift = ((y << 1) + x) * 8;
        return (byte)((f >> shift) & 0xFF);
    }

    public void SetCubeEdge(int d, int x, int y, byte v)
    {
        int shift = ((y << 1) + x) * 8;
        uint f = Faces[d] & ~(0xFFu << shift);
        Faces[d] = f | ((uint)v << shift);
    }

    // ---- face geometry (octa.cpp) ----

    /// <summary>octa.cpp:733 <c>flataxisface(c, orient)</c>.</summary>
    public bool FlatAxisFace(int orient)
    {
        uint face = Faces[orient >> 1];
        if ((orient & 1) != 0) face >>= 4;
        return (face & 0x0F0F0F0Fu) == 0x01010101u * (face & 0x0Fu);
    }

    /// <summary>octa.cpp:750 <c>touchingface(c, orient)</c>.</summary>
    public bool TouchingFace(int orient)
    {
        uint face = Faces[orient >> 1];
        return (orient & 1) != 0
            ? (face & 0xF0F0F0F0u) == 0x80808080u
            : (face & 0x0F0F0F0Fu) == 0u;
    }

    /// <summary>octa.cpp:673 <c>genfaceverts(c, orient, v)</c>.</summary>
    public void GenFaceVerts(int orient, System.Span<Vector3I> v) => CubeGeometry.GenFaceVerts(this, orient, v);

    /// <summary>octa.cpp:802 <c>faceconvexity(c, orient)</c>.</summary>
    public int FaceConvexity(int orient)
    {
        if (FlatAxisFace(orient)) return 0;
        System.Span<Vector3I> v = stackalloc Vector3I[4];
        GenFaceVerts(orient, v);
        return CubeGeometry.FaceConvexity(v);
    }

    /// <summary>octa.h: <c>edgemake(a, b)</c>.</summary>
    public static byte EdgeMake(int a, int b) => (byte)((b << 4) | a);

    /// <summary>octa.h: <c>edgeget(edge, coord)</c>.</summary>
    public static byte EdgeGet(byte edge, int coord) => coord != 0 ? (byte)(edge >> 4) : (byte)(edge & 0xF);

    /// <summary>octa.h: <c>edgeset(edge, coord, val)</c> (divergence: takes <c>ref</c>).</summary>
    public static void EdgeSet(ref byte edge, int coord, int val)
    {
        edge = coord != 0
            ? (byte)((edge & 0x0F) | ((val & 0xF) << 4))
            : (byte)((edge & 0xF0) | (val & 0xF));
    }
}

/// <summary>octa.h: <c>struct surfaceinfo</c> (rendering info per face).</summary>
public struct SurfaceInfo
{
    public byte Lmid0, Lmid1;
    public byte Verts, NumVerts;

    public int TotalVerts() =>
        (NumVerts & Octa.LAYER_DUP) != 0
            ? (NumVerts & Octa.MAXFACEVERTS) * 2
            : NumVerts & Octa.MAXFACEVERTS;

    /// <summary>octa.h: <c>surfaceinfo::used()</c>.</summary>
    public bool Used() => Lmid0 != 0 || Lmid1 != 0 || (NumVerts & ~Octa.LAYER_TOP) != 0;
}

/// <summary>octa.h: <c>struct vertinfo</c>.</summary>
public struct VertInfo
{
    public ushort X, Y, Z, U, V, Norm;
}

/// <summary>octa.h: <c>struct cubeext</c> (extended, per-cube render info).</summary>
public sealed class CubeExt
{
    public SurfaceInfo[] Surfaces { get; } = new SurfaceInfo[6];

    /// <summary>octa.h: <c>cubeext::verts()</c>. Flat storage indexed by SurfaceInfo.Verts.</summary>
    public VertInfo[] Verts { get; set; } = System.Array.Empty<VertInfo>();

    public int TJoints { get; set; } = -1;
    public byte MaxVerts { get; set; }
}

/// <summary>
/// The world octree: a C# port of the state and primitives from
/// src/engine/octa.cpp. Rendering, editing, collision and the file format live
/// in OctaRender / OctaEdit / OctaCollision / WorldIo.
/// </summary>
public sealed class Octa
{
    // octa.h / iengine.h / world.h constants, names kept verbatim.
    public const uint F_EMPTY = 0x00000000; // all edges range (0,0)
    public const uint F_SOLID = 0x80808080; // all edges range (0,8)

    public const ushort DEFAULT_SKY = 0;
    public const ushort DEFAULT_GEOM = 1;

    public const ushort MAT_AIR      = 0;
    public const ushort MAT_WATER    = 1 << 2;
    public const ushort MAT_LAVA     = 2 << 2;
    public const ushort MAT_GLASS    = 3 << 2;
    public const ushort MAT_NOCLIP   = 1 << 5;
    public const ushort MAT_CLIP     = 2 << 5;
    public const ushort MAT_GAMECLIP = 3 << 5;
    public const ushort MAT_DEATH    = 1 << 8;
    public const ushort MAT_ALPHA    = 4 << 8;

    // iengine.h material masks.
    public const ushort MATF_INDEX  = 3;
    public const ushort MATF_VOLUME = 7 << 2;

    public const int MAXFACEVERTS = 15;
    public const int LAYER_TOP = 1 << 5;
    public const int LAYER_BOTTOM = 1 << 6;
    public const int LAYER_DUP = 1 << 7;

    // shared/geom.h:1125 — axis lookup.
    public static readonly int[] R = { 1, 2, 0 };
    public static readonly int[] C = { 2, 0, 1 };
    public static readonly int[] D = { 0, 1, 2 };

    public Cube[] WorldRoot { get; private set; }
    public int WorldSize { get; private set; }
    public int WorldScale { get; private set; }
    public int AllocNodes { get; private set; }

    public Octa(int worldscale = 10)
    {
        WorldRoot = System.Array.Empty<Cube>();
        NewMap(worldscale);
    }

    /// <summary>Build an Octa from an already-constructed root (used by the OGZ importer).</summary>
    internal Octa(int worldscale, int worldsize, Cube[] root, int allocNodes)
    {
        WorldScale = worldscale;
        WorldSize = worldsize;
        WorldRoot = root;
        AllocNodes = allocNodes;
    }

    /// <summary>Create an empty (solid) world at the given scale.</summary>
    public void NewMap(int worldscale)
    {
        AllocNodes = 0;
        WorldScale = worldscale;
        WorldSize = 1 << worldscale;
        WorldRoot = NewCubes(F_SOLID);
    }

    /// <summary>octa.h: <c>octastep(x, y, z, scale)</c>.</summary>
    public static int OctaStep(int x, int y, int z, int scale) =>
        ((((z >> scale) & 1) << 2) | (((y >> scale) & 1) << 1) | ((x >> scale) & 1));

    /// <summary>octa.h: <c>octaindex(d, x, y, z)</c>.</summary>
    public static int OctaIndex(int d, int x, int y, int z) =>
        ((z << D[d]) + (y << C[d]) + (x << R[d]));

    /// <summary>octa.cpp:49 <c>newcubes(face, mat)</c>.</summary>
    public Cube[] NewCubes(uint face, ushort mat = MAT_AIR)
    {
        var cubes = new Cube[8];
        for (int i = 0; i < 8; i++)
        {
            var c = new Cube { Material = mat };
            c.SetFaces(face);
            for (int l = 0; l < 6; l++) c.Texture[l] = DEFAULT_GEOM;
            cubes[i] = c;
        }
        AllocNodes++;
        return cubes;
    }

    /// <summary>iengine.h: <c>insideworld(o)</c>.</summary>
    public bool InsideWorld(Vector3I o) =>
        (uint)o.X < (uint)WorldSize && (uint)o.Y < (uint)WorldSize && (uint)o.Z < (uint)WorldSize;

    /// <summary>Descend to the leaf cube containing a world position.</summary>
    public Cube? LeafCube(Vector3I o)
    {
        if (!InsideWorld(o)) return null;
        int scale = WorldScale - 1;
        Cube c = WorldRoot[OctaStep(o.X, o.Y, o.Z, scale)];
        while (c.Children != null)
        {
            scale--;
            c = c.Children[OctaStep(o.X, o.Y, o.Z, scale)];
        }
        return c;
    }

    /// <summary>As <see cref="LeafCube(Vector3I)"/>, also returning the leaf cube's edge length.</summary>
    public Cube? LeafCube(Vector3I o, out int leafSize)
    {
        leafSize = 0;
        if (!InsideWorld(o)) return null;
        int scale = WorldScale - 1;
        Cube c = WorldRoot[OctaStep(o.X, o.Y, o.Z, scale)];
        while (c.Children != null)
        {
            scale--;
            c = c.Children[OctaStep(o.X, o.Y, o.Z, scale)];
        }
        leafSize = 1 << scale;
        return c;
    }

    /// <summary>octa.cpp:246 <c>lookupmaterial(v)</c>.</summary>
    public int LookupMaterial(Vector3 v)
    {
        var o = new Vector3I((int)v.X, (int)v.Y, (int)v.Z);
        return LeafCube(o)?.Material ?? MAT_AIR;
    }
}
