using Godot;
using SauerbratenRevival.World.Physics;

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
    /// <summary>
    /// octaedit.cpp:1948 <c>mpeditface</c> — the three face/corner edit modes:
    /// 0 = push/pull faces (whole cube), 1 = fill/empty slice, 2 = push/pull a single
    /// corner. <paramref name="sel"/> is mutated (origin/slice) as in the original.
    /// </summary>
    public static void EditFace(Octa octa, Commands.Selection sel, int dir, int mode)
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
                return;
            }

            // Modes 0 and 2 operate on the cube's deformation edges.
            uint bak = c.Faces[d];
            var p = new byte[4];
            for (int k = 0; k < 4; k++) p[k] = (byte)((bak >> (k * 8)) & 0xFF);

            if (mode == 2)
            {
                LinkedPush(c, d, sel.Corner & 1, sel.Corner >> 1, dc, seldir); // corner command
            }
            else
            {
                for (int mx = 0; mx < 2; mx++)
                    for (int my = 0; my < 2; my++)
                    {
                        if (x == 0 && mx == 0 && sel.Cx != 0) continue;
                        if (y == 0 && my == 0 && sel.Cy != 0) continue;
                        if (x == sel.S[Octa.R[d]] - 1 && mx == 1 && ((sel.Cx + sel.Cxs) & 1) != 0) continue;
                        if (y == sel.S[Octa.C[d]] - 1 && my == 1 && ((sel.Cy + sel.Cys) & 1) != 0) continue;
                        int idx = mx + my * 2;
                        if (p[idx] != (byte)((bak >> (idx * 8)) & 0xFF)) continue;
                        LinkedPush(c, d, mx, my, dc, seldir);
                    }
            }

            // optiface: if both nibbles of every edge byte are equal, the face is flat -> empty.
            OptiFace(c);
        });

        if (mode == 1 && dir > 0)
            sel.O = SetAxis(sel.O, d, sel.O[d] + sel.Grid * seldir);
    }

    /// <summary>octaedit.cpp:1949 <c>bounded(n)</c> — clamp an edge nibble to [0,8].</summary>
    public static int Bounded(int n) => n < 0 ? 0 : n > 8 ? 8 : n;

    /// <summary>
    /// octaedit.cpp:1951 <c>pushedge(edge, dir, dc)</c> — nudge one nibble of an edge
    /// byte, keeping the two nibbles ordered (the second follows when it would invert).
    /// </summary>
    public static void PushEdge(Cube c, int d, int x, int y, int dir, int dc)
    {
        byte edge = c.CubeEdge(d, x, y);
        int ne = Bounded(Cube.EdgeGet(edge, dc) + dir);
        Cube.EdgeSet(ref edge, dc, ne);
        int oe = Cube.EdgeGet(edge, 1 - dc);
        if ((dir < 0 && dc != 0 && oe > ne) || (dir > 0 && dc == 0 && oe < ne))
            Cube.EdgeSet(ref edge, 1 - dc, ne);
        c.SetCubeEdge(d, x, y, edge);
    }

    /// <summary>
    /// octaedit.cpp:1957 <c>linkedpush(c, d, x, y, dc, dir)</c> — push the edge at
    /// (x,y) of face d, and every other edge that shares the same vertex value.
    /// </summary>
    public static void LinkedPush(Cube c, int d, int x, int y, int dc, int dir)
    {
        Vector3I v = CubeGeometry.GetCubeVector(c, d, x, y, dc);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                Vector3I p = CubeGeometry.GetCubeVector(c, d, i, j, dc);
                if (v == p) PushEdge(c, d, i, j, dir, dc);
            }
    }

    /// <summary>octa.cpp:157 <c>optiface</c> — if an edge byte's nibbles are equal on every edge, the face is flat, so empty it.</summary>
    public static void OptiFace(Cube c)
    {
        for (int d = 0; d < 3; d++)
        {
            uint f = c.Faces[d];
            if (((f >> 4) & 0x0F0F0F0Fu) == (f & 0x0F0F0F0Fu)) { c.Faces[d] = Octa.F_EMPTY; }
        }
    }

    /// <summary>Backwards-compatible wrapper: <c>Fill</c> is <see cref="EditFace"/> with the fill mode.</summary>
    public static void Fill(Octa octa, Commands.Selection sel, int dir, int mode) => EditFace(octa, sel, dir, mode);

    // ---- selection flip / rotate (octaedit.cpp mpflip / mprotate) ----

    /// <summary>octaedit.cpp:2643 <c>rotatequad(a,b,c,d)</c> — cyclic content rotation a<-b<-c<-d<-a.</summary>
    private static void RotateQuad(Cube a, Cube b, Cube c, Cube d)
        => SwapContents(a, b, c, d);

    /// <summary>SwapContents(a,b) then rotate: implemented as three content swaps for a 4-cycle.</summary>
    private static void SwapContents(Cube a, Cube b)
    {
        // Content-level swap (the Cube objects are references to octree slots).
        (a.Children, b.Children) = (b.Children, a.Children);
        (a.Faces.F0, b.Faces.F0) = (b.Faces.F0, a.Faces.F0);
        (a.Faces.F1, b.Faces.F1) = (b.Faces.F1, a.Faces.F1);
        (a.Faces.F2, b.Faces.F2) = (b.Faces.F2, a.Faces.F2);
        (a.Texture, b.Texture) = (b.Texture, a.Texture);
        (a.Material, b.Material) = (b.Material, a.Material);
        (a.Merged, b.Merged) = (b.Merged, a.Merged);
        (a.Visible, b.Visible) = (b.Visible, a.Visible);
    }

    /// <summary>Rotate four cubes a<-b<-c<-d<-a without a temporary allocation.</summary>
    private static void SwapContents(Cube a, Cube b, Cube c, Cube d)
    {
        SwapContents(a, b);
        SwapContents(b, c);
        SwapContents(c, d);
    }

    /// <summary>
    /// octaedit.cpp:2673 <c>mpflip</c> — mirror the whole selection along the oriented
    /// axis: flip each cube and swap the two halves.
    /// </summary>
    public static void FlipSelection(Octa octa, Commands.Selection sel)
    {
        int d = Commands.Selection.Dimension(sel.Orient);
        int r = Octa.R[d], cc = Octa.C[d], zz = Octa.D[d];
        int zs = sel.S[zz];
        var b = new Commands.Block3(sel);
        for (int y = 0; y < sel.S[cc]; y++)
            for (int x = 0; x < sel.S[r]; x++)
            {
                for (int z = 0; z < zs; z++) FlipCube(BlockCube(octa, x, y, z, b, sel.Grid), d);
                for (int z = 0; z < zs / 2; z++)
                    SwapContents(
                        BlockCube(octa, x, y, z, b, sel.Grid),
                        BlockCube(octa, x, y, zs - z - 1, b, sel.Grid));
            }
    }

    /// <summary>
    /// octaedit.cpp:2673 <c>mprotate</c> — rotate the selection's cubes by 90° about
    /// the oriented axis, permuting positions (<c>rotatequad</c>) and rotating each
    /// cube's faces (<c>rotatecube</c>). <paramref name="cw"/> &gt; 0 = clockwise.
    /// </summary>
    public static void RotateSelection(Octa octa, Commands.Selection sel, int cw)
    {
        int d = Commands.Selection.Dimension(sel.Orient);
        if (Commands.Selection.DimCoord(sel.Orient) == 0) cw = -cw;
        int r = Octa.R[d], cc = Octa.C[d], zz = Octa.D[d];
        int m = sel.S[cc] < sel.S[r] ? cc : r;
        int ss = System.Math.Max(sel.S[r], sel.S[cc]);
        sel.S = SetAxis(sel.S, m, ss);

        var b = new Commands.Block3(sel);
        for (int z = 0; z < sel.S[zz]; z++)
            for (int pass = 0; pass < (cw > 0 ? 1 : 3); pass++)
            {
                for (int y = 0; y < sel.S[cc]; y++)
                    for (int x = 0; x < sel.S[r]; x++)
                        RotateCube(BlockCube(octa, x, y, z, b, sel.Grid), d);

                for (int y = 0; y < ss / 2; y++)
                    for (int x = 0; x < ss - 1 - y * 2; x++)
                        RotateQuad(
                            BlockCube(octa, ss - 1 - y, x + y, z, b, sel.Grid),
                            BlockCube(octa, x + y, y, z, b, sel.Grid),
                            BlockCube(octa, y, ss - 1 - x - y, z, b, sel.Grid),
                            BlockCube(octa, ss - 1 - x - y, ss - 1 - y, z, b, sel.Grid));
            }
    }

    // ---- face / texture / material ops (octaedit.cpp) ----

    /// <summary>
    /// octaedit.cpp:2177 <c>edittexcube(c, tex, orient, findrep)</c> — set a cube's
    /// face texture, recursing into children. <paramref name="orient"/> &lt; 0 sets
    /// all six faces; otherwise only the (visible) face for that orientation.
    /// </summary>
    public static void SetFaceTexture(Cube c, int tex, int orient)
    {
        if (orient < 0)
        {
            for (int i = 0; i < 6; i++) c.Texture[i] = (ushort)tex;
        }
        else
        {
            int i = VisibleOrient(c, orient);
            c.Texture[i] = (ushort)tex;
        }
        if (c.Children != null)
            for (int i = 0; i < 8; i++) SetFaceTexture(c.Children[i], tex, orient);
    }

    /// <summary>octaedit.cpp:2555 <c>replacetexcube(c, oldtex, newtex)</c> — swap one texture index for another, recursing children.</summary>
    public static void ReplaceTexture(Cube c, int oldtex, int newtex)
    {
        for (int i = 0; i < 6; i++)
            if (c.Texture[i] == oldtex) c.Texture[i] = (ushort)newtex;
        if (c.Children != null)
            for (int i = 0; i < 8; i++) ReplaceTexture(c.Children[i], oldtex, newtex);
    }

    /// <summary>
    /// octaedit.cpp:2565 <c>mpreplacetex</c> — replace a texture across a selection
    /// (or the whole world when <paramref name="insel"/> is false).
    /// </summary>
    public static void ReplaceTexture(Octa octa, Commands.Block3? sel, int oldtex, int newtex, bool insel)
    {
        if (insel && sel.HasValue)
        {
            ForEachSelCube(octa, sel.Value, sel.Value.Grid, (x, y, z, c) => ReplaceTexture(c, oldtex, newtex));
        }
        else
        {
            for (int i = 0; i < 8; i++) ReplaceTexture(octa.WorldRoot[i], oldtex, newtex);
        }
    }

    /// <summary>
    /// octaedit.cpp:2713 <c>setmat</c> — apply a material to a cube (recursing
    /// children), subject to an optional filter and geometry test.
    /// <paramref name="matmask"/> clears the bits the new material overwrites.
    /// </summary>
    public static void SetMaterial(Cube c, ushort mat, ushort matmask,
        ushort filtermat, ushort filtermask, int filtergeom)
    {
        if (c.Children != null)
        {
            for (int i = 0; i < 8; i++) SetMaterial(c.Children[i], mat, matmask, filtermat, filtermask, filtergeom);
            return;
        }

        if ((c.Material & filtermask) != filtermat) return;

        switch (filtergeom)
        {
            case EditMatFilter.Empty: if (c.IsEmpty()) break; return;
            case EditMatFilter.NotEmpty: if (!c.IsEmpty()) break; return;
            case EditMatFilter.Solid: if (c.IsEntirelySolid()) break; return;
            case EditMatFilter.NotSolid: if (!c.IsEntirelySolid()) break; return;
        }

        if (mat != Octa.MAT_AIR)
        {
            c.Material = (ushort)((c.Material & matmask) | mat);
        }
        else c.Material = Octa.MAT_AIR;
    }

    /// <summary>octaedit.cpp geometry filters for <see cref="SetMaterial"/> (EDITMATF_*).</summary>
    public static class EditMatFilter
    {
        public const int Empty = 1;
        public const int NotEmpty = 2;
        public const int Solid = 3;
        public const int NotSolid = 4;
    }

    /// <summary>
    /// octaedit.cpp:2735 <c>mpeditmat</c> — apply a material across a selection with
    /// the same mask/filter derivation the original uses. <paramref name="matid"/> &lt; 0
    /// removes the filtered material.
    /// </summary>
    public static void EditMaterial(Octa octa, Commands.Block3 sel, int matid, int filter)
    {
        ushort filtermat = 0, filtermask = 0, matmask;
        int filtergeom = 0;
        if (filter >= 0)
        {
            filtermat = (ushort)(filter & 0xFFFF);
            filtermask = (filtermat & (Octa.MATF_VOLUME | Octa.MATF_INDEX)) != 0
                ? (ushort)(Octa.MATF_VOLUME | Octa.MATF_INDEX)
                : (filtermat & Octa.MATF_CLIP) != 0 ? Octa.MATF_CLIP : filtermat;
            filtergeom = filter & ~0xFFFF;
        }
        if (matid < 0)
        {
            matid = 0;
            matmask = filtermask;
            if (Octa.IsClipped((ushort)(filtermat & Octa.MATF_VOLUME))) matmask &= unchecked((ushort)~Octa.MATF_CLIP);
            if (Octa.IsDeadly((ushort)(filtermat & Octa.MATF_VOLUME))) matmask &= unchecked((ushort)~Octa.MAT_DEATH);
        }
        else
        {
            matmask = (matid & (Octa.MATF_VOLUME | Octa.MATF_INDEX)) != 0
                ? (ushort)0
                : (matid & Octa.MATF_CLIP) != 0 ? unchecked((ushort)~Octa.MATF_CLIP) : unchecked((ushort)~(ushort)matid);
            if (Octa.IsClipped((ushort)(matid & Octa.MATF_VOLUME))) matid |= Octa.MAT_CLIP;
            if (Octa.IsDeadly((ushort)(matid & Octa.MATF_VOLUME))) matid |= Octa.MAT_DEATH;
        }
        ushort m = (ushort)matid;
        ForEachSelCube(octa, sel, sel.Grid, (x, y, z, c) => SetMaterial(c, m, matmask, filtermat, filtermask, filtergeom));
    }

    // ---- flip / rotate (octaedit.cpp) ----

    /// <summary>octaedit.cpp:2643 <c>dflip(face)</c>.</summary>
    public static uint DFlip(uint face) => face == Octa.F_EMPTY ? face : 0x88888888u - (((face & 0xF0F0F0F0u) >> 4) | ((face & 0x0F0F0F0Fu) << 4));

    /// <summary>octaedit.cpp:2644 <c>cflip(face)</c>.</summary>
    public static uint CFlip(uint face) => ((face & 0xFF00FF00u) >> 8) | ((face & 0x00FF00FFu) << 8);

    /// <summary>octaedit.cpp:2645 <c>rflip(face)</c>.</summary>
    public static uint RFlip(uint face) => ((face & 0xFFFF0000u) >> 16) | ((face & 0x0000FFFFu) << 16);

    /// <summary>octaedit.cpp:2646 <c>mflip(face)</c>.</summary>
    public static uint MFlip(uint face) => (face & 0xFF0000FFu) | ((face & 0x00FF0000u) >> 8) | ((face & 0x0000FF00u) << 8);

    /// <summary>octaedit.cpp:2648 <c>flipcube(c, d)</c> — mirror a cube across axis d, recursing children.</summary>
    public static void FlipCube(Cube c, int d)
    {
        (c.Texture[d * 2], c.Texture[d * 2 + 1]) = (c.Texture[d * 2 + 1], c.Texture[d * 2]);
        c.Faces[Octa.D[d]] = DFlip(c.Faces[Octa.D[d]]);
        c.Faces[Octa.C[d]] = CFlip(c.Faces[Octa.C[d]]);
        c.Faces[Octa.R[d]] = RFlip(c.Faces[Octa.R[d]]);
        if (c.Children != null)
        {
            int dim = 1 << d;
            for (int i = 0; i < 8; i++)
                if ((i & dim) != 0) (c.Children[i], c.Children[i - dim]) = (c.Children[i - dim], c.Children[i]);
            for (int i = 0; i < 8; i++) FlipCube(c.Children[i], d);
        }
    }

    /// <summary>octaedit.cpp:2663 <c>rotatecube(c, d)</c> — rotate a cube clockwise about axis d, recursing children.</summary>
    public static void RotateCube(Cube c, int d)
    {
        c.Faces[Octa.D[d]] = CFlip(MFlip(c.Faces[Octa.D[d]]));
        c.Faces[Octa.C[d]] = DFlip(MFlip(c.Faces[Octa.C[d]]));
        c.Faces[Octa.R[d]] = RFlip(MFlip(c.Faces[Octa.R[d]]));
        (c.Faces[Octa.R[d]], c.Faces[Octa.C[d]]) = (c.Faces[Octa.C[d]], c.Faces[Octa.R[d]]);

        (c.Texture[2 * Octa.R[d]], c.Texture[2 * Octa.C[d] + 1]) = (c.Texture[2 * Octa.C[d] + 1], c.Texture[2 * Octa.R[d]]);
        (c.Texture[2 * Octa.C[d]], c.Texture[2 * Octa.R[d] + 1]) = (c.Texture[2 * Octa.R[d] + 1], c.Texture[2 * Octa.C[d]]);
        (c.Texture[2 * Octa.C[d]], c.Texture[2 * Octa.C[d] + 1]) = (c.Texture[2 * Octa.C[d] + 1], c.Texture[2 * Octa.C[d]]);

        if (c.Children != null)
        {
            int row = 1 << Octa.R[d];
            int col = 1 << Octa.C[d];
            int ddim = 1 << d;
            for (int i = 0; i <= ddim; i += ddim)
            {
                // rotatequad(children[i+row], children[i], children[i+col], children[i+col+row])
                Cube t = c.Children[i + row];
                c.Children[i + row] = c.Children[i];
                c.Children[i] = c.Children[i + col];
                c.Children[i + col] = c.Children[i + col + row];
                c.Children[i + col + row] = t;
            }
            for (int i = 0; i < 8; i++) RotateCube(c.Children[i], d);
        }
    }

    /// <summary>octaedit.cpp:1015-ish <c>visibleorient(c, orient)</c> — the actual face a texture edit lands on.</summary>
    private static int VisibleOrient(Cube c, int orient)
    {
        for (int i = 0; i < 2; i++)
        {
            int a = FaceEdgesIdx[orient][i * 2 + 0];
            int b = FaceEdgesIdx[orient][i * 2 + 1];
            for (int j = 0; j < 2; j++)
            {
                if (CrushedEdge(EdgeByte(c, a), j) && CrushedEdge(EdgeByte(c, b), j) && c.TouchingFace(orient))
                    return ((a >> 2) << 1) + j;
            }
        }
        return orient;
    }

    /// <summary>Read edge byte <paramref name="k"/> (0..11); <c>cubeedge</c> inversion of <c>edges[k]</c>.</summary>
    private static byte EdgeByte(Cube c, int k) => c.CubeEdge(k >> 2, k & 1, (k >> 1) & 1);

    private static bool CrushedEdge(byte e, int dc) => dc != 0 ? e == 0 : e == 0x88;

    /// <summary>octa.cpp:723 <c>faceedgesidx[6][4]</c> — ordered edges surrounding each orient.</summary>
    private static readonly int[][] FaceEdgesIdx =
    {
        new[] { 4, 5, 8, 10 },
        new[] { 6, 7, 9, 11 },
        new[] { 8, 9, 0, 2 },
        new[] { 10, 11, 1, 3 },
        new[] { 0, 1, 4, 6 },
        new[] { 2, 3, 5, 7 },
    };

    // ---- (re)mip (octa.cpp) ----

    /// <summary>
    /// octa.cpp:316 <c>forcemip(c, fixtex)</c> — collapse a cube's edges to those
    /// of its nearest non-empty children. Optionally re-mips the parent textures.
    /// <paramref name="c"/> must have children.
    /// </summary>
    public static void ForceMip(Octa octa, Cube c, bool fixtex)
    {
        Cube[] ch = c.Children!;
        c.EmptyFaces();

        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                // The child near vertex j: n = i ^ (j==3 ? 4 : (j==4 ? 3 : j)).
                int n = i ^ (j == 3 ? 4 : j == 4 ? 3 : j);
                if (!ch[n].IsEmpty())
                {
                    Vector3I v = CubeGeometry.GetCubeVector(ch[n], i);
                    // setcubevector(c, i, ivec(n, v, 8).shr(1)) — child vertex i,
                    // octant n offset by one node size (8), halved to parent grid.
                    CubeGeometry.SetCubeVector(c, i, ParentVertex(n, v));
                    break;
                }
            }
        }

        if (fixtex)
        {
            for (int j = 0; j < 6; j++) c.Texture[j] = Octa.GetMippedTexture(c, j);
        }
    }

    /// <summary>octa.cpp:316 — <c>ivec(n, v, 8).shr(1)</c>: vertex v offset into octant n, halved to parent scale.</summary>
    private static Vector3I ParentVertex(int n, Vector3I v)
    {
        int x = (v.X + ((n & 1) >> 0) * 8) >> 1;
        int y = (v.Y + ((n & 2) >> 1) * 8) >> 1;
        int z = (v.Z + ((n & 4) >> 2) * 8) >> 1;
        return new Vector3I(x, y, z);
    }

    // ---- remip (octa.cpp) ----

    /// <summary>
    /// octa.cpp:1013 <c>visibleface(c, orient, co, size, mat, nmat, matmask)</c> —
    /// is this face visible (not occluded by its neighbour)? Integer-only; the
    /// merge decision of <see cref="Remip"/> depends on it.
    /// </summary>
    public static bool VisibleFace(Octa octa, Cube c, int orient, Vector3I co, int size,
        ushort mat, ushort nmat, ushort matmask)
    {
        if (mat != Octa.MAT_AIR)
        {
            if (OctaVisible.FaceEdges(c, orient) == Octa.F_SOLID && c.TouchingFace(orient)) return false;
        }
        else
        {
            if (OctaVisible.CollapsedFace(c, orient)) return false;
            if (!c.TouchingFace(orient)) return true;
        }

        Cube o = octa.NeighbourCube(c, orient, co, size, out Vector3I no, out int nsize);
        if (ReferenceEquals(o, c)) return false;

        int opp = Octa.Opposite(orient);
        if (nsize > size || (nsize == size && o.Children == null))
        {
            if (nmat != Octa.MAT_AIR && (o.Material & matmask) == nmat) return true;
            if (o.IsEntirelySolid()) return false;
            if (mat != Octa.MAT_AIR && ((o.Material & matmask) == mat ||
                (Octa.IsLiquid(mat) && (o.Material & Octa.MATF_VOLUME) == Octa.MAT_GLASS))) return false;
            if (o.IsEmpty() || OctaVisible.NotTouchingFace(o, opp)) return true;
            if (o.TouchingFace(opp) && OctaVisible.FaceEdges(o, opp) == Octa.F_SOLID) return false;

            var vo = new Vector3I(co.X & 0xFFF, co.Y & 0xFFF, co.Z & 0xFFF);
            no = new Vector3I(no.X & 0xFFF, no.Y & 0xFFF, no.Z & 0xFFF);
            var cf = new Vector2I[4];
            var of = new Vector2I[4];
            int numc = OctaVisible.GenFaceVecs(c, orient, vo, size, mat != Octa.MAT_AIR, cf);
            int numo = OctaVisible.GenFaceVecs(o, opp, no, nsize, false, of);
            return numo < 3 || !OctaVisible.InsideFace(cf, numc, of, numo);
        }

        {
            var vo = new Vector3I(co.X & 0xFFF, co.Y & 0xFFF, co.Z & 0xFFF);
            no = new Vector3I(no.X & 0xFFF, no.Y & 0xFFF, no.Z & 0xFFF);
            var cf = new Vector2I[4];
            int numc = OctaVisible.GenFaceVecs(c, orient, vo, size, mat != Octa.MAT_AIR, cf);
            return !OctaVisible.OccludesFace(o, opp, no, nsize, vo, size, mat, nmat, matmask, cf, numc);
        }
    }

    /// <summary>
    /// octa.cpp:463 <c>remip(c, co, size)</c> — merge a node's children back into
    /// the node where they are provably redundant (faces match a fresh subdivision,
    /// textures agree, and — with <paramref name="mipvis"/> — faces are consistent
    /// across their quadrants). Returns whether the node is "perfect" (fully
    /// merged); the top-level caller ignores the result.
    ///
    /// Pure integer arithmetic: deterministic given the octree and
    /// <paramref name="mipvis"/>. This is why `Remip` can replicate pass-through
    /// (docs/editor-and-commands.md §5.4).
    /// </summary>
    public static bool Remip(Octa octa, Cube c, Vector3I co, int size, bool mipvis)
    {
        Cube[] ch = c.Children!;
        if (ch == null)
        {
            if (size << 1 <= 0x1000) return true;
            SubdivideCube(octa, c);
            ch = c.Children!;
        }

        bool perfect = true;
        for (int i = 0; i < 8; i++)
        {
            var o = new Vector3I(co.X + ((i & 1) >> 0) * size, co.Y + ((i & 2) >> 1) * size, co.Z + ((i & 4) >> 2) * size);
            if (!Remip(octa, ch[i], o, size >> 1, mipvis)) perfect = false;
        }

        c.SolidFaces(); // so texmip is more consistent
        for (int j = 0; j < 6; j++) c.Texture[j] = Octa.GetMippedTexture(c, j); // parents get child texs regardless

        if (!perfect) return false;
        if (size << 1 > 0x1000) return false;

        ushort mat = Octa.MAT_AIR;
        for (int i = 0; i < 8; i++)
        {
            mat = ch[i].Material;
            if ((mat & Octa.MATF_CLIP) == Octa.MAT_NOCLIP || (mat & Octa.MAT_ALPHA) != 0)
            {
                if (i > 0) return false;
                while (++i < 8) if (ch[i].Material != mat) return false;
                break;
            }
            else if (!ch[i].IsEntirelySolid())
            {
                while (++i < 8)
                {
                    int omat = ch[i].Material;
                    if (ch[i].IsEntirelySolid()
                        ? (omat & Octa.MATF_CLIP) == Octa.MAT_NOCLIP || (omat & Octa.MAT_ALPHA) != 0
                        : mat != omat) return false;
                }
                break;
            }
        }

        // n = a fresh subdivision of c's re-mipped shape; children must match.
        var n = new Cube
        {
            Faces = c.Faces,
            Texture = c.Texture,
            Material = c.Material,
            Merged = c.Merged,
            Visible = c.Visible,
        };
        n.Children = c.Children; // forcemip needs the children to read vertices
        ForceMip(octa, n, fixtex: true);
        n.Children = null;
        SubdivideCube(octa, n);

        Cube[] nh = n.Children!;
        var vis = new int[6];
        for (int i = 0; i < 8; i++)
        {
            if (ch[i].Faces[0] != nh[i].Faces[0] ||
                ch[i].Faces[1] != nh[i].Faces[1] ||
                ch[i].Faces[2] != nh[i].Faces[2])
            {
                octa.FreeOcta(nh);
                return false;
            }

            if (ch[i].IsEmpty() && nh[i].IsEmpty()) continue;

            var o = new Vector3I(co.X + ((i & 1) >> 0) * size, co.Y + ((i & 2) >> 1) * size, co.Z + ((i & 4) >> 2) * size);
            for (int orient = 0; orient < 6; orient++)
            {
                if (VisibleFace(octa, ch[i], orient, o, size, Octa.MAT_AIR,
                        (ushort)((mat & Octa.MAT_ALPHA) ^ Octa.MAT_ALPHA), Octa.MAT_ALPHA))
                {
                    if (ch[i].Texture[orient] != n.Texture[orient]) { octa.FreeOcta(nh); return false; }
                    vis[orient] |= 1 << i;
                }
            }
        }

        if (mipvis)
        {
            for (int orient = 0; orient < 6; orient++)
            {
                int mask = 0;
                for (int x = 0; x < 2; x++)
                    for (int y = 0; y < 2; y++)
                        mask |= 1 << Octa.OctaIndex(orient >> 1, x, y, orient & 1);
                if ((vis[orient] & mask) != 0 && (vis[orient] & mask) != mask) { octa.FreeOcta(nh); return false; }
            }
        }

        octa.FreeOcta(nh);
        octa.DiscardChildren(c);
        for (int i = 0; i < 3; i++) c.Faces[i] = n.Faces[i];
        c.Material = mat;
        for (int i = 0; i < 6; i++) if (vis[i] != 0) c.Visible |= (byte)(1 << i);
        if (c.Visible != 0) c.Visible |= 0x40;
        // brightencube(c) is a lightmap concern; the octree state above is complete.
        return true;
    }

    /// <summary>
    /// octa.cpp:565 <c>mpremip</c> — remip the whole world. Runs bottom-up from each
    /// root octant. <paramref name="mipvis"/> is the user toggle that affects the
    /// collapse decision (must be replicated; see docs/editor-and-commands.md §5.4).
    /// </summary>
    public static void RemipAll(Octa octa, bool mipvis)
    {
        for (int i = 0; i < 8; i++)
        {
            var o = new Vector3I(((i & 1) >> 0) * (octa.WorldSize >> 1), ((i & 2) >> 1) * (octa.WorldSize >> 1), ((i & 4) >> 2) * (octa.WorldSize >> 1));
            Remip(octa, octa.WorldRoot[i], o, octa.WorldSize >> 2, mipvis);
        }
        // calcmerges() rebuilds derived render caches; not octree state.
    }
}
