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
    public const ushort MATF_CLIP   = 7 << 5;
    public const ushort MATF_FLAGS  = 0xFF << 8;

    // iengine.h material predicates (MATF_VOLUME-shifted values).
    public static bool IsLiquid(ushort mat) => mat == MAT_WATER || mat == MAT_LAVA;
    public static bool IsClipped(ushort mat) => mat == MAT_GLASS;
    public static bool IsDeadly(ushort mat) => mat == MAT_LAVA;

    /// <summary>octa.h: <c>opposite(orient)</c>.</summary>
    public static int Opposite(int orient) => orient ^ 1;

    public const int MAXFACEVERTS = 15;
    public const int LAYER_TOP = 1 << 5;
    public const int LAYER_BOTTOM = 1 << 6;
    public const int LAYER_DUP = 1 << 7;
    public const int LAYER_BLEND = LAYER_TOP | LAYER_BOTTOM;

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

    /// <summary>
    /// Deep-copy this world into a fresh <see cref="Octa"/>: the same scale,
    /// geometry, textures and materials, with an independent node tree. Only
    /// subtrees that exist are copied, so cloning a sparsely-subdivided world is
    /// much cheaper than rebuilding it from scratch. Cube render caches
    /// (<see cref="Cube.Ext"/>, <see cref="Cube.Visible"/>, <see cref="Cube.Merged"/>) are
    /// cleared, matching <see cref="OctaEdit.CopyCube"/>.
    /// </summary>
    public Octa Clone() => new(WorldScale, WorldSize, CloneCubes(WorldRoot), AllocNodes);

    /// <summary>
    /// Deep-copy a cube block and, recursively, every subtree that exists. Shared
    /// with <see cref="Ogz.OgzMap.Clone"/> so both clone paths stay in step.
    /// </summary>
    public static Cube[] CloneCubes(Cube[] src)
    {
        var dst = new Cube[8];
        for (int i = 0; i < 8; i++)
        {
            Cube s = src[i];
            dst[i] = new Cube
            {
                Faces = s.Faces,
                Texture = s.Texture,
                Material = s.Material,
                Ext = CloneExt(s.Ext),
                Children = s.Children != null ? CloneCubes(s.Children) : null,
            };
        }
        return dst;
    }

    /// <summary>Deep-copy a cube's extended surface data, or null if it has none.</summary>
    private static CubeExt? CloneExt(CubeExt? src)
    {
        if (src == null) return null;
        var ext = new CubeExt { TJoints = src.TJoints, MaxVerts = src.MaxVerts };
        System.Array.Copy(src.Surfaces, ext.Surfaces, src.Surfaces.Length);
        ext.Verts = (VertInfo[])src.Verts.Clone();
        return ext;
    }

    /// <summary>Create an empty (solid) world at the given scale.</summary>
    public void NewMap(int worldscale)
    {
        AllocNodes = 0;
        WorldScale = worldscale;
        WorldSize = 1 << worldscale;
        WorldRoot = NewCubes(F_SOLID);
    }

    /// <summary>
    /// world.cpp:1250 <c>emptymap</c> — build the fresh editor world: eight root cubes
    /// with the **bottom half solid** (a floor) and the top half empty, so the editor
    /// starts with ground to build on. Distinct from <see cref="NewMap"/> (a fully
    /// solid block, used for construction). The scale is clamped to 10…16 as the
    /// original does.
    ///
    /// For worlds larger than 4096 it pre-subdivides (<see cref="SplitOcta"/>) so that
    /// **no leaf exceeds 0x1000**, matching the original. That invariant is enforced
    /// lazily elsewhere (`validatec`, `subdividecube`); doing it eagerly here means a
    /// new map starts legal rather than waiting for the first lookup.
    /// </summary>
    public void EmptyMap(int scale)
    {
        WorldScale = System.Math.Clamp(scale, 10, 16);
        WorldSize = 1 << WorldScale;
        AllocNodes = 0;
        WorldRoot = NewCubes(F_EMPTY);
        for (int i = 0; i < 4; i++) WorldRoot[i].SolidFaces(); // low-Z octants = floor
        if (WorldSize > 0x1000) SplitOcta(WorldRoot, WorldSize >> 1);
    }

    /// <summary>
    /// world.cpp:1277 <c>splitocta</c> — recursively subdivide every node larger than
    /// 0x1000, so no leaf exceeds that size. Empty nodes split into empty children,
    /// solid into solid; geometry is preserved.
    /// </summary>
    public void SplitOcta(Cube[] c, int size)
    {
        if (size <= 0x1000) return;
        for (int i = 0; i < 8; i++)
        {
            if (c[i].Children == null)
                c[i].Children = NewCubes(c[i].IsEmpty() ? F_EMPTY : F_SOLID); // mat defaults to MAT_AIR, as original
            SplitOcta(c[i].Children!, size >> 1);
        }
    }

    /// <summary>octa.h: <c>octastep(x, y, z, scale)</c>.</summary>
    public static int OctaStep(int x, int y, int z, int scale) =>
        ((((z >> scale) & 1) << 2) | (((y >> scale) & 1) << 1) | ((x >> scale) & 1));

    /// <summary>octa.h: <c>octaindex(d, x, y, z)</c>.</summary>
    public static int OctaIndex(int d, int x, int y, int z) =>
        ((z << D[d]) + (y << C[d]) + (x << R[d]));

    /// <summary>octa.h: <c>oppositeocta(d, i)</c> — the child index mirrored across axis d.</summary>
    public static int OppositeOcta(int d, int i) => i ^ (1 << D[d]);

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

    /// <summary>octa.cpp:68 <c>familysize(c)</c> — total nodes in a subtree (root counts as 1).</summary>
    public static int FamilySize(Cube c)
    {
        int size = 1;
        if (c.Children != null)
            for (int i = 0; i < 8; i++) size += FamilySize(c.Children[i]);
        return size;
    }

    /// <summary>
    /// octa.cpp:77 <c>freeocta(c)</c> — release a node block. In the managed port
    /// there is nothing to free; this only unwires the children so the GC can
    /// collect them, and adjusts <see cref="AllocNodes"/> to match the original's
    /// bookkeeping.
    /// </summary>
    public void FreeOcta(Cube[] c)
    {
        if (c == null) return;
        for (int i = 0; i < 8; i++) DiscardChildren(c[i]);
        AllocNodes--;
    }

    /// <summary>
    /// octa.cpp:94 <c>discardchildren(c, fixtex, depth)</c> — collapse a node back
    /// to a leaf. Optionally re-mips the parent texture from its children
    /// (<paramref name="fixtex"/>), matching the original's default depth-0 call.
    /// Returns the mask of child faces that were non-empty (the caller-facing part
    /// of the original's <c>filled</c> accumulator).
    /// </summary>
    public uint DiscardChildren(Cube c, bool fixtex = false, int depth = 0)
    {
        c.Material = MAT_AIR;
        c.Visible = 0;
        c.Merged = 0;
        // Managed port: Ext is dropped with the cube. The original also frees the
        // vertex array / entities / t-joints here; those live outside the octree.
        c.Ext = null;

        uint filled = 0;
        if (c.Children != null)
        {
            filled = F_EMPTY;
            for (int i = 0; i < 8; i++)
            {
                DiscardChildren(c.Children[i], fixtex, depth + 1);
                filled |= c.Children[i].Faces[0];
            }
            if (fixtex)
            {
                for (int i = 0; i < 6; i++) c.Texture[i] = GetMippedTexture(c, i);
                if (depth > 0 && filled != F_EMPTY) c.Faces[0] = F_SOLID;
            }
            c.Children = null;
            AllocNodes--;
        }
        return filled;
    }

    /// <summary>octa.cpp:294 <c>getmippedtexture(p, orient)</c> — pick the parent texture for a face from the children.</summary>
    public static ushort GetMippedTexture(Cube p, int orient)
    {
        Cube[] c = p.Children!;
        int d = orient >> 1, dc = orient & 1;
        var texs = new ushort[4];
        int numtexs = 0;
        for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            {
                int n = OctaIndex(d, x, y, dc);
                if (c[n].IsEmpty())
                {
                    n = OppositeOcta(d, n);
                    if (c[n].IsEmpty()) continue;
                }
                ushort tex = c[n].Texture[orient];
                if (tex > DEFAULT_SKY)
                    for (int i = 0; i < numtexs; i++)
                        if (texs[i] == tex) return tex;
                texs[numtexs++] = tex;
            }
        for (int i = numtexs - 1; i >= 0; i--)
            if (i == 0 || texs[i] > DEFAULT_SKY) return texs[i];
        return DEFAULT_GEOM;
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

    /// <summary>octa.cpp:263 <c>neighbourcube</c> — the leaf/node beside a face (node size ≤ the queried size).</summary>
    public Cube NeighbourCube(Cube c, int orient, Vector3I co, int size, out Vector3I ro, out int rsize)
    {
        var n = co;
        int dim = orient >> 1;
        uint diff = (uint)n[dim];
        if ((orient & 1) != 0) n[dim] += size; else n[dim] -= size;
        diff ^= (uint)n[dim];
        if (diff >= (uint)WorldSize) { ro = n; rsize = size; return c; }
        int scale = WorldScale - 1;
        Cube nc = WorldRoot[OctaStep(n.X, n.Y, n.Z, scale)];
        while ((size >> scale) == 0 && nc.Children != null)
        {
            scale--;
            nc = nc.Children[OctaStep(n.X, n.Y, n.Z, scale)];
        }
        rsize = 1 << scale;
        ro = new Vector3I(n.X & ~(rsize - 1), n.Y & ~(rsize - 1), n.Z & ~(rsize - 1));
        return nc;
    }

    /// <summary>octa.cpp:246 <c>lookupmaterial(v)</c>.</summary>
    public int LookupMaterial(Vector3 v)
    {
        var o = new Vector3I((int)v.X, (int)v.Y, (int)v.Z);
        return LeafCube(o)?.Material ?? MAT_AIR;
    }
}
