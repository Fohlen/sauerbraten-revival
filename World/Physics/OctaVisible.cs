using Godot;

namespace SauerbratenRevival.World.Physics;

// octa.cpp face geometry: genfacevecs()/insideface()/occludesface()/visibletris().
// These work in the engine's 2D per-face coordinates (1/8 world units, axes
// C[dim] × R[dim]) and decide which face triangles survive occlusion — the exact
// input genclipplanes() needs for collision hulls.
public static class OctaVisible
{
    // octa.cpp:723 faceedgesidx[6][4].
    private static readonly int[][] FaceEdgesIdx =
    {
        new[] { 4, 5, 8, 10 }, new[] { 6, 7, 9, 11 }, new[] { 8, 9, 0, 2 },
        new[] { 10, 11, 1, 3 }, new[] { 0, 1, 4, 6 }, new[] { 2, 3, 5, 7 },
    };

    private static readonly int[,] NotTouchMasks =
    {
        { 3, 3, 3, 3, 3, 3, 3, 2, 3, 3, 3, 3, 3, 1, 3, 0 },
        { 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 3, 3, 2, 0 },
    };

    private static readonly int[][][][] TriVerts =
    {
        new[] { new[] { new[] { 1, 2, 3 }, new[] { 0, 1, 3 } }, new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } } },
        new[] { new[] { new[] { 0, 1, 2 }, new[] { 3, 0, 2 } }, new[] { new[] { 1, 2, 3 }, new[] { 1, 3, 0 } } },
    };

    private static int Dim(int orient) => orient >> 1;
    private static bool DimCoord(int orient) => (orient & 1) != 0;
    private static int Opposite(int orient) => orient ^ 1;
    public static int C(int dim) => Octa.C[dim];
    public static int R(int dim) => Octa.R[dim];

    private static int Comp(Vector3I v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
    private static int Comp(Vector2I v, int a) => a == 0 ? v.X : v.Y;

    /// <summary>octa.cpp:815 <c>faceedges(c, orient)</c> as a uint.</summary>
    public static uint FaceEdges(Cube c, int orient)
    {
        uint r = 0;
        for (int k = 0; k < 4; k++)
        {
            int i = FaceEdgesIdx[orient][k];
            r |= (uint)((c.Faces[i / 4] >> ((i % 4) * 8)) & 0xFF) << (k * 8);
        }
        return r;
    }

    /// <summary>octa.cpp:758 <c>notouchingface(c, orient)</c>.</summary>
    public static bool NotTouchingFace(Cube c, int orient)
    {
        uint face = c.Faces[Dim(orient)];
        return DimCoord(orient) ? (face & 0x80808080) == 0 : ((0x88888888 - face) & 0x08080808) == 0;
    }

    /// <summary>octa.cpp:972 <c>collapsedface(c, orient)</c>.</summary>
    public static bool CollapsedFace(Cube c, int orient)
    {
        int e0 = (byte)((c.Faces[FaceEdgesIdx[orient][0] / 4] >> ((FaceEdgesIdx[orient][0] % 4) * 8)) & 0xFF);
        int e1 = (byte)((c.Faces[FaceEdgesIdx[orient][1] / 4] >> ((FaceEdgesIdx[orient][1] % 4) * 8)) & 0xFF);
        int e2 = (byte)((c.Faces[FaceEdgesIdx[orient][2] / 4] >> ((FaceEdgesIdx[orient][2] % 4) * 8)) & 0xFF);
        int e3 = (byte)((c.Faces[FaceEdgesIdx[orient][3] / 4] >> ((FaceEdgesIdx[orient][3] % 4) * 8)) & 0xFF);
        int face = Dim(orient) * 4;
        int f0 = (byte)((c.Faces[(face + 0) / 4] >> (((face + 0) % 4) * 8)) & 0xFF);
        int f1 = (byte)((c.Faces[(face + 1) / 4] >> (((face + 1) % 4) * 8)) & 0xFF);
        int f2 = (byte)((c.Faces[(face + 2) / 4] >> (((face + 2) % 4) * 8)) & 0xFF);
        int f3 = (byte)((c.Faces[(face + 3) / 4] >> (((face + 3) % 4) * 8)) & 0xFF);
        if (DimCoord(orient)) { f0 >>= 4; f1 >>= 4; f2 >>= 4; f3 >>= 4; }
        else { f0 &= 0xF; f1 &= 0xF; f2 &= 0xF; f3 &= 0xF; }
        var v0 = new Vector3I(e0 & 0xF, e2 & 0xF, f0);
        var v1 = new Vector3I(e0 >> 4, e3 & 0xF, f1);
        var v2 = new Vector3I(e1 >> 4, e3 >> 4, f3);
        var v3 = new Vector3I(e1 & 0xF, e2 >> 4, f2);
        return CubeGeometry.Cross(v1 - v0, v2 - v0) == Vector3I.Zero &&
               CubeGeometry.Cross(v2, v3 - v0) == Vector3I.Zero;
    }

    /// <summary>octa.cpp:827 <c>genfacevecs</c> — face polygon in 2D face coords.</summary>
    public static int GenFaceVecs(Cube c, int orient, Vector3I pos, int size, bool solid, Vector2I[] fvecs, Vector3I[]? v = null)
    {
        int dim = Dim(orient), cd = C(dim), rd = R(dim);
        if (solid)
        {
            for (int n = 0; n < 4; n++)
            {
                int src = DimCoord(orient) ? n : (3 - n);
                var corner = CubeGeometry.FaceCorner[orient][src];
                fvecs[n] = new Vector2I(corner[cd] * 8 * size + Comp(pos, cd) * 8,
                                        corner[rd] * 8 * size + Comp(pos, rd) * 8);
            }
            return 4;
        }

        var buf = v ?? new Vector3I[4];
        if (v == null) c.GenFaceVerts(orient, buf);
        int i = 0;
        var prev = new Vector2I(int.MaxValue, int.MaxValue);
        int coord = DimCoord(orient) ? 8 : 0;
        for (int n = 0; n < 4; n++)
        {
            var e = buf[n];
            if (Comp(e, dim) != coord) continue;
            var f = new Vector2I(Comp(e, cd) * size + Comp(pos, cd) * 8, Comp(e, rd) * size + Comp(pos, rd) * 8);
            if (f != prev) { prev = f; fvecs[i++] = f; }
        }
        if (i > 0 && fvecs[0] == prev) i--;
        return i;
    }

    private static int ClipFaceVecY(Vector2I o, Vector2I dir, int cx, int cy, int size, out Vector2I r)
    {
        r = default;
        if (dir.X >= 0) { if (cx <= o.X || cx >= o.X + dir.X) return 0; }
        else if (cx <= o.X + dir.X || cx >= o.X) return 0;
        int t = (o.Y - cy) + (cx - o.X) * dir.Y / dir.X;
        if (t <= 0 || t >= size) return 0;
        r = new Vector2I(cx, cy + t);
        return 1;
    }

    private static int ClipFaceVecX(Vector2I o, Vector2I dir, int cx, int cy, int size, out Vector2I r)
    {
        r = default;
        if (dir.Y >= 0) { if (cy <= o.Y || cy >= o.Y + dir.Y) return 0; }
        else if (cy <= o.Y + dir.Y || cy >= o.Y) return 0;
        int t = (o.X - cx) + (cy - o.Y) * dir.X / dir.Y;
        if (t <= 0 || t >= size) return 0;
        r = new Vector2I(cx + t, cy);
        return 1;
    }

    private static int ClipFaceVec(Vector2I o, Vector2I dir, int cx, int cy, int size, Vector2I[] rvecs, int off)
    {
        int r = 0;
        if (o.X >= cx && o.X <= cx + size && o.Y >= cy && o.Y <= cy + size &&
            ((o.X != cx && o.X != cx + size) || (o.Y != cy && o.Y != cy + size)))
            rvecs[off + r++] = o;
        if (ClipFaceVecX(o, dir, cx, cy, size, out var a) != 0) rvecs[off + r++] = a;
        if (ClipFaceVecX(o, dir, cx, cy + size, size, out var b) != 0) rvecs[off + r++] = b;
        if (ClipFaceVecY(o, dir, cx, cy, size, out var c) != 0) rvecs[off + r++] = c;
        if (ClipFaceVecY(o, dir, cx + size, cy, size, out var d) != 0) rvecs[off + r++] = d;
        return r;
    }

    /// <summary>octa.cpp:944 <c>clipfacevecs</c>.</summary>
    public static int ClipFaceVecs(Vector2I[] o, int numo, int cx, int cy, int size, Vector2I[] rvecs)
    {
        cx <<= 3; cy <<= 3; size <<= 3;
        int r = 0;
        var prev = o[numo - 1];
        for (int i = 0; i < numo; i++)
        {
            var cur = o[i];
            r += ClipFaceVec(prev, new Vector2I(cur.X - prev.X, cur.Y - prev.Y), cx, cy, size, rvecs, r);
            prev = cur;
        }
        var corner = new[] { new Vector2I(cx, cy), new Vector2I(cx + size, cy), new Vector2I(cx + size, cy + size), new Vector2I(cx, cy + size) };
        for (int i = 0; i < 4; i++) if (InsideFace(new[] { corner[i] }, 1, o, numo)) rvecs[r++] = corner[i];
        return r;
    }

    /// <summary>octa.cpp:928 <c>insideface</c>.</summary>
    public static bool InsideFace(Vector2I[] p, int nump, Vector2I[] o, int numo)
    {
        int bounds = 0;
        var prev = o[numo - 1];
        for (int i = 0; i < numo; i++)
        {
            var cur = o[i];
            var dir = new Vector2I(cur.X - prev.X, cur.Y - prev.Y);
            int offset = dir.X * prev.Y - dir.Y * prev.X;
            for (int j = 0; j < nump; j++)
                if (dir.X * p[j].Y - dir.Y * p[j].X > offset) return false;
            bounds++;
            prev = cur;
        }
        return bounds >= 3;
    }

    /// <summary>octa.cpp:981 <c>occludesface</c>.</summary>
    public static bool OccludesFace(Cube c, int orient, Vector3I o, int size, Vector3I vo, int vsize,
                                    ushort vmat, ushort nmat, ushort matmask, Vector2I[] vf, int numv)
    {
        int dim = Dim(orient);
        if (c.Children == null)
        {
            var nf = new Vector2I[8];
            if (nmat != Octa.MAT_AIR && (c.Material & matmask) == nmat)
                return ClipFaceVecs(vf, numv, Comp(o, C(dim)), Comp(o, R(dim)), size, nf) < 3;
            if (c.IsEntirelySolid()) return true;
            if (vmat != Octa.MAT_AIR && ((c.Material & matmask) == vmat ||
                (Phys.IsLiquid(vmat) && Phys.IsClipped(c.Material)))) return true;
            if (c.TouchingFace(orient) && FaceEdges(c, orient) == Octa.F_SOLID) return true;
            var cf = new Vector2I[8];
            int numc = ClipFaceVecs(vf, numv, Comp(o, C(dim)), Comp(o, R(dim)), size, cf);
            if (numc < 3) return true;
            if (c.IsEmpty() || NotTouchingFace(c, orient)) return false;
            var of = new Vector2I[4];
            int numo = GenFaceVecs(c, orient, o, size, false, of);
            return numo >= 3 && InsideFace(cf, numc, of, numo);
        }

        size >>= 1;
        int coord = DimCoord(orient) ? 1 : 0;
        for (int i = 0; i < 8; i++)
        {
            if (((i >> dim) & 1) != coord) continue;
            var childCo = new Vector3I(o.X + ((i & 1) >> 0) * size, o.Y + ((i & 2) >> 1) * size, o.Z + ((i & 4) >> 2) * size);
            if (!OccludesFace(c.Children[i], orient, childCo, size, vo, vsize, vmat, nmat, matmask, vf, numv)) return false;
        }
        return true;
    }

    /// <summary>octa.cpp:1094 <c>visibletris</c> (COLLIDE_ELLIPSE uses MAT_NOCLIP/MATF_CLIP).</summary>
    public static int VisibleTris(Octa octa, Cube c, int orient, Vector3I co, int size,
                                  ushort nmat = Phys.MatAlpha, ushort matmask = Phys.MatAlpha, bool showoutside = false)
    {
        int vis = 3, touching = 0xF;
        var v = new Vector3I[4];
        c.GenFaceVerts(orient, v);
        Vector3I e1 = v[1], e2 = v[2], e3 = v[0];
        Vector3I n = CubeGeometry.Cross(e1 - v[0], e2 - v[0]);
        int convex = (e3 - v[3]).X * n.X + (e3 - v[3]).Y * n.Y + (e3 - v[3]).Z * n.Z;
        if (convex == 0)
        {
            if (CubeGeometry.Cross(e3, e2) == Vector3I.Zero || v[1] == v[3])
            {
                if (n == Vector3I.Zero) return 0;
                vis = 1; touching = 0xF & ~(1 << 3);
            }
            else if (n == Vector3I.Zero) { vis = 2; touching = 0xF & ~(1 << 1); }
        }

        int dim = Dim(orient), coord = DimCoord(orient) ? 1 : 0;
        if (v[0][dim] != coord * 8) touching &= ~(1 << 0);
        if (v[1][dim] != coord * 8) touching &= ~(1 << 1);
        if (v[2][dim] != coord * 8) touching &= ~(1 << 2);
        if (v[3][dim] != coord * 8) touching &= ~(1 << 3);
        int order = convex < 0 ? 1 : 0;
        int notouch = NotTouchMasks[order, touching];
        if ((vis & notouch) == vis) return vis;

        var o = octa.NeighbourCube(c, orient, co, size, out var no, out int nsize);
        if (o == c) return showoutside ? vis : 0;

        if ((c.Material & matmask) == nmat) nmat = Octa.MAT_AIR;
        var vo = new Vector3I(co.X & 0xFFF, co.Y & 0xFFF, co.Z & 0xFFF);
        no = new Vector3I(no.X & 0xFFF, no.Y & 0xFFF, no.Z & 0xFFF);
        var cf = new Vector2I[4];
        var of = new Vector2I[4];
        int opp = Opposite(orient), numo = 0, numc;
        if (nsize > size || (nsize == size && o.Children == null))
        {
            if (o.IsEmpty() || NotTouchingFace(o, opp)) return vis;
            if (nmat != Octa.MAT_AIR && (o.Material & matmask) == nmat) return vis;
            if (o.IsEntirelySolid() || (o.TouchingFace(opp) && FaceEdges(o, opp) == Octa.F_SOLID)) return vis & notouch;
            numc = GenFaceVecs(c, orient, vo, size, false, cf, v);
            numo = GenFaceVecs(o, opp, no, nsize, false, of);
            if (numo < 3) return vis;
            if (InsideFace(cf, numc, of, numo)) return vis & notouch;
        }
        else
        {
            numc = GenFaceVecs(c, orient, vo, size, false, cf, v);
            if (OccludesFace(o, opp, no, nsize, vo, size, Octa.MAT_AIR, nmat, matmask, cf, numc)) return vis & notouch;
        }
        if (vis != 3 || notouch != 0) return vis;

        do
        {
            for (int i = 0; i < 2; i++)
            {
                var tv = TriVerts[order][coord][i];
                var tf = new[] { cf[tv[0]], cf[tv[1]], cf[tv[2]] };
                if (numo > 0) { if (!InsideFace(tf, 3, of, numo)) continue; }
                else if (!OccludesFace(o, opp, no, nsize, vo, size, Octa.MAT_AIR, nmat, matmask, tf, 3)) continue;
                return vis & ~(1 << i);
            }
            vis |= 4;
        } while (++order <= 1);
        return 3;
    }

    private static byte EdgeByte(Cube c, int i) => (byte)((c.Faces[i / 4] >> ((i % 4) * 8)) & 0xFF);

    /// <summary>octa.cpp:734 <c>collideface</c>.</summary>
    public static bool CollideFace(Cube c, int orient)
    {
        if (c.FlatAxisFace(orient))
        {
            byte r1 = EdgeByte(c, FaceEdgesIdx[orient][0]), r2 = EdgeByte(c, FaceEdgesIdx[orient][1]);
            if ((byte)((r1 >> 4) | (r2 & 0xF0)) == (byte)((r1 & 0x0F) | (r2 << 4))) return false;
            byte c1 = EdgeByte(c, FaceEdgesIdx[orient][2]), c2 = EdgeByte(c, FaceEdgesIdx[orient][3]);
            if ((byte)((c1 >> 4) | (c2 & 0xF0)) == (byte)((c1 & 0x0F) | (c2 << 4))) return false;
        }
        return true;
    }

    /// <summary>octa.cpp:1059 <c>classifyface</c> — bit1 = face is visible, bit2 = collidable.</summary>
    public static int ClassifyFace(Octa octa, Cube c, int orient, Vector3I co, int size)
    {
        if (CollapsedFace(c, orient)) return 0;
        int vismask = (c.Material & Phys.MatFClip) == Phys.MatNoClip ? 1 : 3;
        if (!c.TouchingFace(orient)) return vismask;
        var o = octa.NeighbourCube(c, orient, co, size, out var no, out int nsize);
        if (o == c) return 0;
        int vis = 0, opp = Opposite(orient);
        if (nsize > size || (nsize == size && o.Children == null))
        {
            if (((~c.Material & o.Material) & Phys.MatAlpha) != 0) vis |= 1;
            if ((o.Material & Phys.MatFClip) == Phys.MatNoClip) vis |= vismask & 2;
            if (vis == vismask || o.IsEntirelySolid()) return vis;
            if (o.IsEmpty() || NotTouchingFace(o, opp)) return vismask;
            if (o.TouchingFace(opp) && FaceEdges(o, opp) == Octa.F_SOLID) return vis;
            var vo = new Vector3I(co.X & 0xFFF, co.Y & 0xFFF, co.Z & 0xFFF);
            no = new Vector3I(no.X & 0xFFF, no.Y & 0xFFF, no.Z & 0xFFF);
            var cf = new Vector2I[4]; var of = new Vector2I[4];
            int numc = GenFaceVecs(c, orient, vo, size, false, cf);
            int numo = GenFaceVecs(o, opp, no, nsize, false, of);
            return numo < 3 || !InsideFace(cf, numc, of, numo) ? vismask : vis;
        }
        var vo2 = new Vector3I(co.X & 0xFFF, co.Y & 0xFFF, co.Z & 0xFFF);
        no = new Vector3I(no.X & 0xFFF, no.Y & 0xFFF, no.Z & 0xFFF);
        var cf2 = new Vector2I[4];
        int numc2 = GenFaceVecs(c, orient, vo2, size, false, cf2);
        if (!OccludesFace(o, opp, no, nsize, vo2, size, Octa.MAT_AIR,
                (ushort)((c.Material & Phys.MatAlpha) ^ Phys.MatAlpha), Phys.MatAlpha, cf2, numc2)) vis |= 1;
        if ((vismask & 2) != 0 && !OccludesFace(o, opp, no, nsize, vo2, size, Octa.MAT_AIR, Phys.MatNoClip, Phys.MatFClip, cf2, numc2)) vis |= 2;
        return vis;
    }

    /// <summary>setcubevisibility() collision mask (collidemask) used by genclipplanes.</summary>
    public static int CollisionVisibleMask(Octa octa, Cube c, Vector3I co, int size)
    {
        int mask = 0;
        for (int i = 0; i < 6; i++)
            if ((ClassifyFace(octa, c, i, co, size) & 2) != 0 && CollideFace(c, i)) mask |= 1 << i;
        return mask;
    }
}
