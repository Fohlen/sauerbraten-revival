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

    // ---- selection iteration (octaedit.cpp) ----

    /// <summary>
    /// octaedit.cpp:290 <c>loopselxyz</c> — iterate every cube in a selection,
    /// yielding (x,y,z) grid coordinates plus the resolved cube and its origin/size.
    /// The iteration order matches the original's
    /// <c>loop(z, s[D[dim]]) loop(y, s[C[dim]]) loop(x, s[R[dim]])</c>.
    /// </summary>
    public static void ForEachSelCube(Octa octa, Commands.Block3 sel, int rgrid,
        System.Action<int, int, int, Cube> body)
    {
        int dim = sel.Orient >> 1;
        for (int z = 0; z < sel.S[Octa.D[dim]]; z++)
            for (int y = 0; y < sel.S[Octa.C[dim]]; y++)
                for (int x = 0; x < sel.S[Octa.R[dim]]; x++)
                {
                    Cube c = BlockCube(octa, x, y, z, sel, rgrid);
                    body(x, y, z, c);
                }
    }

    /// <summary>
    /// octaedit.cpp:1970 <c>getmaterial(c)</c> — the common material of a cube's
    /// children (recursively), or <see cref="Octa.MAT_AIR"/> if they differ.
    /// </summary>
    public static ushort GetMaterial(Cube c)
    {
        if (c.Children == null) return c.Material;
        ushort mat = GetMaterial(c.Children[7]);
        for (int i = 0; i < 7; i++)
            if (mat != GetMaterial(c.Children[i])) return Octa.MAT_AIR;
        return mat;
    }

    // ---- block capture (octaedit.cpp) ----

    /// <summary>
    /// A captured block of cube data — the portable payload behind paste, undo and
    /// prefabs (the <c>ReplaceBlock</c> carrier, docs/editor-and-commands.md §5.3).
    /// Maps the original's <c>block3 + cube[]</c> buffer into managed cubes in the
    /// block's canonical (x,y,z) order, so it is world- and location-independent.
    /// </summary>
    public sealed class CapturedBlock
    {
        public Commands.Block3 Descriptor;
        public Cube[] Cubes = System.Array.Empty<Cube>();
    }

    /// <summary>
    /// octaedit.cpp:663 <c>blockcopy(s, rgrid)</c> — capture every cube in a
    /// selection into a portable block. The block's own <see cref="Commands.Block3"/>
    /// keeps the origin/size/grid/orient so it can be re-oriented on paste.
    /// </summary>
    public static CapturedBlock BlockCopy(Octa octa, Commands.Block3 s, int rgrid)
    {
        var cap = new CapturedBlock { Descriptor = s };
        int dim = s.Orient >> 1;
        int count = s.S[Octa.D[dim]] * s.S[Octa.C[dim]] * s.S[Octa.R[dim]];
        cap.Cubes = new Cube[count];
        int n = 0;
        for (int z = 0; z < s.S[Octa.D[dim]]; z++)
            for (int y = 0; y < s.S[Octa.C[dim]]; y++)
                for (int x = 0; x < s.S[Octa.R[dim]]; x++)
                {
                    var dst = new Cube();
                    CopyCube(octa, BlockCube(octa, x, y, z, s, rgrid), dst);
                    cap.Cubes[n++] = dst;
                }
        return cap;
    }

    /// <summary>
    /// octaedit.cpp:1276 <c>pasteblock(b, sel)</c> — transparent paste: for each
    /// cell in the block, paste it only if the source cell is non-empty, has
    /// children, or carries a non-air material. Empty source cells leave the
    /// destination untouched.
    /// </summary>
    public static void PasteBlock(Octa octa, CapturedBlock block, Commands.Block3 target, int rgrid)
    {
        int dim = block.Descriptor.Orient >> 1;
        int n = 0;
        for (int z = 0; z < block.Descriptor.S[Octa.D[dim]]; z++)
            for (int y = 0; y < block.Descriptor.S[Octa.C[dim]]; y++)
                for (int x = 0; x < block.Descriptor.S[Octa.R[dim]]; x++)
                {
                    Cube src = block.Cubes[n++];
                    if (!src.IsEmpty() || src.Children != null || src.Material != Octa.MAT_AIR)
                    {
                        Cube dst = BlockCube(octa, x, y, z, target, rgrid);
                        PasteCube(octa, src, dst);
                    }
                }
    }

    // ---- op handlers (octaedit.cpp) ----

    /// <summary>
    /// octaedit.cpp:2098 <c>mpdelcube</c> — delete the selected cubes: discard any
    /// children and empty the faces. This is the editor's "remove".
    /// </summary>
    public static void DeleteCube(Octa octa, Commands.Block3 sel)
    {
        ForEachSelCube(octa, sel, sel.Grid, (x, y, z, c) =>
        {
            octa.DiscardChildren(c, fixtex: true);
            c.EmptyFaces();
        });
    }

    /// <summary>
    /// octaedit.cpp:1983 <c>mpeditface</c> fill branch (mode 1) — fill or clear one
    /// grid-slice of the selection. <paramref name="dir"/> &gt; 0 pushes the slice
    /// to the positive side (empty faces, i.e. carve); &lt; 0 pulls it back (solid
    /// faces, i.e. fill), copying the backing cell's textures. Returns the origin the
    /// caller should advance to (the original mutates <c>sel.o[d]</c>).
    /// </summary>
    public static void Fill(Octa octa, Commands.Selection sel, int dir, int mode)
    {
        // octaedit.cpp:1986 — corner/edge state only permits mode 1 when centred.
        if (mode == 1 && (sel.Cx != 0 || sel.Cy != 0 || (sel.Cxs & 1) != 0 || (sel.Cys & 1) != 0))
            mode = 0;

        int d = Commands.Selection.Dimension(sel.Orient);
        int dc = Commands.Selection.DimCoord(sel.Orient);
        int seldir = dc != 0 ? -dir : dir;

        if (mode == 1)
        {
            int h = sel.O[d] + dc * sel.Grid;
            if (((dir > 0) == (dc != 0) && h <= 0) || ((dir < 0) == (dc != 0) && h >= octa.WorldSize)) return;
            if (dir < 0) sel.Move(seldir, d);
        }

        if (dc != 0) sel.O = SetAxis(sel.O, d, sel.O[d] + sel.Us(d) - sel.Grid);
        sel.S = SetAxis(sel.S, d, 1);

        var slab = new Commands.Block3(sel.O, sel.S, sel.Grid, sel.Orient);
        ForEachSelCube(octa, slab, sel.Grid, (x, y, z, c) =>
        {
            if (c.Children != null) c.SolidFaces();
            ushort mat = GetMaterial(c);
            octa.DiscardChildren(c, fixtex: true);
            c.Material = mat;
            if (mode == 1)
            {
                if (dir < 0)
                {
                    c.SolidFaces();
                    Cube o = BlockCube(octa, x, y, 1, slab, -sel.Grid);
                    for (int i = 0; i < 6; i++)
                        c.Texture[i] = o.Children != null ? Octa.DEFAULT_GEOM : o.Texture[i];
                }
                else c.EmptyFaces();
            }
        });

        if (mode == 1 && dir > 0)
            sel.O = SetAxis(sel.O, d, sel.O[d] + sel.Grid * seldir);
    }
}
