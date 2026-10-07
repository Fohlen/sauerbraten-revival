using Godot;

namespace SauerbratenRevival.World;

/// <summary>
/// Editor/world-mutation primitives ported from <c>octaedit.cpp</c> and the
/// structural helpers from <c>octa.cpp</c>. This is the "OctaEdit" module
/// referenced by docs/editor-and-commands.md: the algorithms, not the original's
/// rendering/GUI/undo plumbing.
///
/// M1a: the managed-octree structural primitives (copy, subdivide, paste, discard).
/// M1b: cube lookups (with subdivision), block-coordinate mapping, and the first
///      op handlers built on them.
///
/// The load-bearing divergence from the original: <see cref="Cube.Children"/> is
/// a managed array, not a pointer into a flat pool. There is no manual alloc/free;
/// we unwire children and let the GC collect, keeping <see cref="Octa.AllocNodes"/>
/// in step for bookkeeping/tests.
/// </summary>
public static class OctaEdit
{
    // ---- structural primitives (octaedit.cpp / octa.cpp) ----

    /// <summary>
    /// octaedit.cpp:644 <c>copycube(src, dst)</c> — deep-copy <paramref name="src"/>
    /// into <paramref name="dst"/>, allocating a fresh child block. Visibility,
    /// merge state and extended render info are cleared (the source owns its
    /// <c>va</c>/<c>ext</c> lifecycle in the original; here <see cref="CubeExt"/>
    /// is not shared on copy).
    /// </summary>
    public static void CopyCube(Octa octa, Cube src, Cube dst)
    {
        dst.Children = null;
        dst.Ext = null;
        dst.Faces = src.Faces;
        dst.Texture = src.Texture;
        dst.Material = src.Material;
        dst.Merged = 0;
        dst.Visible = 0;
        if (src.Children != null)
        {
            dst.Children = octa.NewCubes(Octa.F_EMPTY);
            for (int i = 0; i < 8; i++) CopyCube(octa, src.Children[i], dst.Children[i]);
        }
    }

    /// <summary>
    /// octaedit.cpp:657 <c>pastecube(src, dst)</c> — replace <paramref name="dst"/>
    /// with a copy of <paramref name="src"/>, discarding any existing subtree.
    /// </summary>
    public static void PasteCube(Octa octa, Cube src, Cube dst)
    {
        octa.DiscardChildren(dst);
        CopyCube(octa, src, dst);
    }

    /// <summary>
    /// octa.cpp:367 <c>subdividecube(c)</c> — split a leaf cube into eight
    /// children. Fully-solid / entirely-empty cubes inherit their state, material
    /// and textures; deformed cubes resample their edges (<c>getcubevector</c>/
    /// <c>midedge</c>) exactly as the original. Returns true (the original's
    /// "perfect" is used by remip; the editor callers ignore it).
    /// </summary>
    public static void SubdivideCube(Octa octa, Cube c)
    {
        if (c.Children != null) return;

        if (c.IsEmpty() || c.IsEntirelySolid())
        {
            c.Children = octa.NewCubes(c.IsEmpty() ? Octa.F_EMPTY : Octa.F_SOLID, c.Material);
            for (int i = 0; i < 8; i++)
                for (int l = 0; l < 6; l++) c.Children[i].Texture[l] = c.Texture[l];
            return;
        }

        Cube[] ch = c.Children = octa.NewCubes(Octa.F_SOLID, c.Material);
        var v = new Vector3I[8];
        for (int i = 0; i < 8; i++)
        {
            v[i] = CubeGeometry.GetCubeVector(c, i);
            v[i] *= 2;
        }
        SubdivideFaces(c, ch, v);
        ValidateC(ch);
    }

    /// <summary>
    /// octa.cpp:184 <c>validatec(c, size)</c> — sanity-check a node block. At the
    /// default size 0, any child whose face nibbles are malformed is emptied. The
    /// editor callers rely on this to reject degenerate subdivision output.
    ///
    /// The original's <c>size &gt; 0x1000</c> auto-subdivide path is not ported: it
    /// never fires on the editor path (cube sizes there are far below that) and it
    /// needs octa allocation this static helper does not own.
    /// </summary>
    public static void ValidateC(Cube[] c, int size = 0)
    {
        for (int i = 0; i < 8; i++)
        {
            if (c[i].Children != null)
            {
                if (size <= 1)
                {
                    c[i].SolidFaces();
                    // discardchildren(c[i], true): the managed port has no vtxarray
                    // to free, so collapsing to a solid leaf is the whole job.
                    c[i].Children = null;
                }
                else ValidateC(c[i].Children!, size >> 1);
            }
            else
            {
                for (int j = 0; j < 3; j++)
                {
                    uint f = c[i].Faces[j];
                    uint e0 = f & 0x0F0F0F0Fu;
                    uint e1 = (f >> 4) & 0x0F0F0F0Fu;
                    if (e0 == e1 || (((e1 + 0x07070707u) | (e1 - e0)) & 0xF0F0F0F0u) != 0)
                    {
                        c[i].EmptyFaces();
                        break;
                    }
                }
            }
        }
    }

    private static void SubdivideFaces(Cube c, Cube[] ch, Vector3I[] v)
    {
        for (int j = 0; j < 6; j++)
        {
            int d = Selection_Dimension(j), z = j & 1;
            Vector3I v00 = v[Octa.OctaIndex(d, 0, 0, z)];
            Vector3I v10 = v[Octa.OctaIndex(d, 1, 0, z)];
            Vector3I v01 = v[Octa.OctaIndex(d, 0, 1, z)];
            Vector3I v11 = v[Octa.OctaIndex(d, 1, 1, z)];
            var e = new int[3, 3];
            bool perfect = true;

            // corners
            e[0, 0] = v00[d];
            e[0, 2] = v01[d];
            e[2, 0] = v10[d];
            e[2, 2] = v11[d];
            // edges
            e[0, 1] = MidEdge(v00, v01, Octa.C[d], d, ref perfect);
            e[1, 0] = MidEdge(v00, v10, Octa.R[d], d, ref perfect);
            e[1, 2] = MidEdge(v11, v01, Octa.R[d], d, ref perfect);
            e[2, 1] = MidEdge(v11, v10, Octa.C[d], d, ref perfect);
            // center
            bool p1 = perfect, p2 = perfect;
            int c1 = MidEdge(v00, v11, Octa.R[d], d, ref p1);
            int c2 = MidEdge(v01, v10, Octa.R[d], d, ref p2);
            if (z != 0 ? c1 > c2 : c1 < c2)
            {
                e[1, 1] = c1;
                perfect = p1 && (c1 == c2 || CrossCenter(v00, v11, Octa.C[d], Octa.R[d]));
            }
            else
            {
                e[1, 1] = c2;
                perfect = p2 && (c1 == c2 || CrossCenter(v01, v10, Octa.C[d], Octa.R[d]));
            }

            for (int i = 0; i < 8; i++)
            {
                ch[i].Texture[j] = c.Texture[j];
                int rd = (i >> Octa.R[d]) & 1, cd = (i >> Octa.C[d]) & 1, dd = (i >> Octa.D[d]) & 1;
                SetEdgeNibble(ch[i], d, 0, 0, z, Clamp(e[rd, cd] - dd * 8, 0, 8));
                SetEdgeNibble(ch[i], d, 1, 0, z, Clamp(e[1 + rd, cd] - dd * 8, 0, 8));
                SetEdgeNibble(ch[i], d, 0, 1, z, Clamp(e[rd, 1 + cd] - dd * 8, 0, 8));
                SetEdgeNibble(ch[i], d, 1, 1, z, Clamp(e[1 + rd, 1 + cd] - dd * 8, 0, 8));
            }
        }
    }

    /// <summary>octa.h: <c>dimension(orient)</c>.</summary>
    private static int Selection_Dimension(int orient) => orient >> 1;

    private static void SetEdgeNibble(Cube c, int d, int x, int y, int coord, int value)
    {
        uint f = c.Faces[d];
        int shift = (((y << 1) + x) * 8) + (coord != 0 ? 4 : 0);
        f = (f & ~(0xFu << shift)) | ((uint)(value & 0xF) << shift);
        c.Faces[d] = f;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>octa.cpp:338 <c>midedge(a, b, xd, yd, perfect)</c>.</summary>
    private static int MidEdge(Vector3I a, Vector3I b, int xd, int yd, ref bool perfect)
    {
        int ax = a[xd], ay = a[yd], bx = b[xd], by = b[yd];
        if (ay == by) return ay;
        if (ax == bx) { perfect = false; return ay; }
        bool crossx = (ax < 8 && bx > 8) || (ax > 8 && bx < 8);
        bool crossy = (ay < 8 && by > 8) || (ay > 8 && by < 8);
        if (crossy && !crossx) { MidEdge(a, b, yd, xd, ref perfect); return 8; }
        if (ax <= 8 && bx <= 8) return ax > bx ? ay : by;
        if (ax >= 8 && bx >= 8) return ax < bx ? ay : by;
        int risex = (by - ay) * (8 - ax) * 256;
        int s = risex / (bx - ax);
        int y = s / 256 + ay;
        if (((System.Math.Abs(s) & 0xFF) != 0) || (crossy && y != 8) || (y < 0 || y > 16)) perfect = false;
        return crossy ? 8 : Clamp(y, 0, 16);
    }

    /// <summary>octa.cpp:356 <c>crosscenter(a, b, xd, yd)</c>.</summary>
    private static bool CrossCenter(Vector3I a, Vector3I b, int xd, int yd)
    {
        int ax = a[xd], ay = a[yd], bx = b[xd], by = b[yd];
        return (((ax <= 8 && bx <= 8) || (ax >= 8 && bx >= 8)) &&
                ((ay <= 8 && by <= 8) || (ay >= 8 && by >= 8))) ||
               (ax + bx == 16 && ay + by == 16);
    }

    // ---- cube lookups (octa.cpp) ----

    /// <summary>
    /// octa.cpp:219 <c>lookupcube(to, tsize, ro, rsize)</c> — resolve a world
    /// position to the cube that contains it. A positive <paramref name="tsize"/>
    /// forces subdivision down to (at least) that size; the resolved cube's origin
    /// and size are returned via <paramref name="ro"/>/<paramref name="rsize"/>.
    /// </summary>
    public static Cube LookupCube(Octa octa, Vector3I to, int tsize, out Vector3I ro, out int rsize)
    {
        int tx = Clamp(to.X, 0, octa.WorldSize - 1);
        int ty = Clamp(to.Y, 0, octa.WorldSize - 1);
        int tz = Clamp(to.Z, 0, octa.WorldSize - 1);
        int scale = octa.WorldScale - 1, csize = System.Math.Abs(tsize);
        Cube c = octa.WorldRoot[Octa.OctaStep(tx, ty, tz, scale)];

        if ((csize >> scale) == 0)
        {
            while (true)
            {
                if (c.Children == null)
                {
                    if (tsize > 0)
                    {
                        do
                        {
                            SubdivideCube(octa, c);
                            scale--;
                            c = c.Children![Octa.OctaStep(tx, ty, tz, scale)];
                        } while ((csize >> scale) == 0);
                    }
                    break;
                }
                scale--;
                c = c.Children[Octa.OctaStep(tx, ty, tz, scale)];
                if ((csize >> scale) != 0) break;
            }
        }

        // octa.cpp:241: ro = ivec(tx,ty,tz).mask(~0U << scale) — clear the low
        // `scale` bits (keep the cube's size-aligned origin).
        ro = new Vector3I(tx & (-1 << scale), ty & (-1 << scale), tz & (-1 << scale));
        rsize = 1 << scale;
        return c;
    }

    /// <summary>
    /// octaedit.cpp:282 <c>blockcube(x, y, z, b, rgrid)</c> — look up a cube inside
    /// a block by (x,y,z) grid coordinates, mapping through the block's orientation.
    /// </summary>
    public static Cube BlockCube(Octa octa, int x, int y, int z, Commands.Block3 b, int rgrid)
    {
        return BlockCube(octa, x, y, z, b, rgrid, out _, out _);
    }

    /// <summary>
    /// octaedit.cpp:282 <c>blockcube(x, y, z, b, rgrid)</c>, also returning the
    /// resolved cube origin/size via <paramref name="ro"/>/<paramref name="rsize"/>.
    /// </summary>
    public static Cube BlockCube(Octa octa, int x, int y, int z, Commands.Block3 b, int rgrid,
        out Vector3I ro, out int rsize)
    {
        int dim = b.Orient >> 1, dc = b.Orient & 1;
        var s = new Vector3I(0, 0, 0);
        s = SetAxis(s, dim, dc * (b.S[dim] - 1) * b.Grid);
        s = SetAxis(s, Octa.R[dim], x * b.Grid);
        s = SetAxis(s, Octa.C[dim], y * b.Grid);
        s += b.O;
        s = SetAxis(s, dim, s[dim] + (dc != 0 ? -z * b.Grid : z * b.Grid));
        return LookupCube(octa, s, rgrid, out ro, out rsize);
    }

    private static Vector3I SetAxis(Vector3I v, int d, int value) => d switch
    {
        0 => new Vector3I(value, v.Y, v.Z),
        1 => new Vector3I(v.X, value, v.Z),
        _ => new Vector3I(v.X, v.Y, value),
    };
}
