using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Meshing;

/// <summary>
/// Port of Sauerbraten's face merging algorithm from src/engine/octa.cpp.
/// Merges coplanar, adjacent faces sharing the same texture and material
/// into single convex polygons (up to MAXFACEVERTS = 15 vertices).
/// </summary>
public static class OctaMerges
{
    public const int MaxMerge = 6;
    public const int MinFace = 4;
    public const int MaxFaceVerts = Octa.MAXFACEVERTS;

    public struct FaceBounds
    {
        public ushort U1, U2, V1, V2;

        public FaceBounds(ushort u1, ushort u2, ushort v1, ushort v2)
        {
            U1 = u1;
            U2 = u2;
            V1 = v1;
            V2 = v2;
        }
    }

    public readonly struct PVert : IEquatable<PVert>
    {
        public readonly ushort X, Y;

        public PVert(ushort x, ushort y) { X = x; Y = y; }

        public bool Equals(PVert other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is PVert other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public static bool operator ==(PVert a, PVert b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(PVert a, PVert b) => a.X != b.X || a.Y != b.Y;
    }

    public readonly struct PEdge : IEquatable<PEdge>
    {
        public readonly PVert From, To;

        public PEdge(PVert from, PVert to) { From = from; To = to; }

        public bool Equals(PEdge other) => From == other.From && To == other.To;
        public override bool Equals(object? obj) => obj is PEdge other && Equals(other);
        public override int GetHashCode() => (int)((uint)From.X ^ ((uint)From.Y << 8) ^ ((uint)To.X << 16) ^ ((uint)To.Y << 24));
        public static bool operator ==(PEdge a, PEdge b) => a.From == b.From && a.To == b.To;
        public static bool operator !=(PEdge a, PEdge b) => a.From != b.From || a.To != b.To;
    }

    public sealed class Poly
    {
        public Cube C = null!;
        public int NumVerts;
        public bool Merged;
        public readonly PVert[] Verts = new PVert[MaxFaceVerts];
    }

    public sealed class PLink
    {
        public PEdge Edge;
        public readonly int[] Polys = new int[2] { -1, -1 };

        public PLink(PEdge edge) { Edge = edge; }
        public void Clear() { Polys[0] = Polys[1] = -1; }
    }

    public readonly struct CFKey : IEquatable<CFKey>, IComparable<CFKey>
    {
        public readonly byte Orient;
        public readonly ushort Material;
        public readonly ushort Tex;
        public readonly Vector3I N;
        public readonly int Offset;

        public CFKey(byte orient, ushort material, ushort tex, Vector3I n, int offset)
        {
            Orient = orient;
            Material = material;
            Tex = tex;
            N = n;
            Offset = offset;
        }

        public bool Equals(CFKey other) =>
            Orient == other.Orient && Material == other.Material && Tex == other.Tex &&
            N == other.N && Offset == other.Offset;

        public override bool Equals(object? obj) => obj is CFKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Orient, Material, Tex, N, Offset);

        public int CompareTo(CFKey other)
        {
            int c = Orient.CompareTo(other.Orient);
            if (c != 0) return c;
            c = Tex.CompareTo(other.Tex);
            if (c != 0) return c;
            c = Material.CompareTo(other.Material);
            if (c != 0) return c;
            c = Offset.CompareTo(other.Offset);
            if (c != 0) return c;
            c = N.X.CompareTo(other.N.X);
            if (c != 0) return c;
            c = N.Y.CompareTo(other.N.Y);
            return c != 0 ? c : N.Z.CompareTo(other.N.Z);
        }
    }

    private static int DimCoord(int orient) => orient & 1;
    private static int OctaCoord(int dim, int i) => (i >> dim) & 1;
    private static int Component(Vector3I v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    private static void SetComponent(ref Vector3I v, int axis, int val)
    {
        switch (axis)
        {
            case 0: v.X = val; break;
            case 1: v.Y = val; break;
            default: v.Z = val; break;
        }
    }

    /// <summary>octarender.cpp:551 reduceslope</summary>
    public static void ReduceSlope(ref Vector3I n)
    {
        int minDim = -1, minVal = 64;
        for (int i = 0; i < 3; i++)
        {
            int val = System.Math.Abs(Component(n, i));
            if (val != 0 && (minDim < 0 || val < minVal))
            {
                minDim = i;
                minVal = val;
            }
        }
        if (minDim >= 0 && minVal > 1)
        {
            int r = Octa.R[minDim], c = Octa.C[minDim];
            if ((Component(n, r) % minVal) == 0 && (Component(n, c) % minVal) == 0)
                n /= minVal;
        }
        while (((n.X | n.Y | n.Z) & 1) == 0 && n != Vector3I.Zero)
        {
            n.X >>= 1;
            n.Y >>= 1;
            n.Z >>= 1;
        }
    }

    /// <summary>octa.cpp:1340 mincubeface</summary>
    public static void MinCubeFace(Cube cu, int orient, Vector3I o, int size,
                                   in FaceBounds orig, ref FaceBounds cf, ushort nmat, ushort matmask)
    {
        int dim = orient >> 1;
        if (cu.Children != null)
        {
            size >>= 1;
            int coord = DimCoord(orient);
            for (int i = 0; i < 8; i++)
            {
                if (OctaCoord(dim, i) == coord)
                {
                    var childCo = new Vector3I(
                        o.X + ((i & 1) >> 0) * size,
                        o.Y + ((i & 2) >> 1) * size,
                        o.Z + ((i & 4) >> 2) * size);
                    MinCubeFace(cu.Children[i], orient, childCo, size, orig, ref cf, nmat, matmask);
                }
            }
            return;
        }

        int c = Octa.C[dim], r = Octa.R[dim];
        ushort uco = (ushort)((Component(o, c) & 0xFFF) << 3);
        ushort vco = (ushort)((Component(o, r) & 0xFFF) << 3);
        ushort uc1 = uco, vc1 = vco;
        ushort uc2 = (ushort)((size << 3) + uco), vc2 = (ushort)((size << 3) + vco);
        uc1 = System.Math.Max(uc1, orig.U1);
        uc2 = System.Math.Min(uc2, orig.U2);
        vc1 = System.Math.Max(vc1, orig.V1);
        vc2 = System.Math.Min(vc2, orig.V2);

        if (!cu.IsEmpty() && cu.TouchingFace(orient) && !(nmat != Octa.MAT_AIR && (cu.Material & matmask) == nmat))
        {
            byte r1 = OctaVisible.EdgeByte(cu, OctaVisible.FaceEdgesIdx[orient][0]);
            byte r2 = OctaVisible.EdgeByte(cu, OctaVisible.FaceEdgesIdx[orient][1]);
            byte c1 = OctaVisible.EdgeByte(cu, OctaVisible.FaceEdgesIdx[orient][2]);
            byte c2 = OctaVisible.EdgeByte(cu, OctaVisible.FaceEdgesIdx[orient][3]);

            ushort u1 = (ushort)(System.Math.Max(c1 & 0xF, c2 & 0xF) * size + uco);
            ushort u2 = (ushort)(System.Math.Min(c1 >> 4, c2 >> 4) * size + uco);
            ushort v1 = (ushort)(System.Math.Max(r1 & 0xF, r2 & 0xF) * size + vco);
            ushort v2 = (ushort)(System.Math.Min(r1 >> 4, r2 >> 4) * size + vco);

            u1 = System.Math.Max(u1, orig.U1);
            u2 = System.Math.Min(u2, orig.U2);
            v1 = System.Math.Max(v1, orig.V1);
            v2 = System.Math.Min(v2, orig.V2);

            if (v2 - v1 == vc2 - vc1)
            {
                if (u2 - u1 == uc2 - uc1) return;
                if (u1 == uc1) uc1 = u2;
                if (u2 == uc2) uc2 = u1;
            }
            else if (u2 - u1 == uc2 - uc1)
            {
                if (v1 == vc1) vc1 = v2;
                if (v2 == vc2) vc2 = v1;
            }
        }

        if (uc1 == uc2 || vc1 == vc2) return;
        cf.U1 = System.Math.Min(cf.U1, uc1);
        cf.U2 = System.Math.Max(cf.U2, uc2);
        cf.V1 = System.Math.Min(cf.V1, vc1);
        cf.V2 = System.Math.Max(cf.V2, vc2);
    }

    /// <summary>octa.cpp:1385 mincubeface</summary>
    public static bool MinCubeFace(Octa octa, Cube cu, int orient, Vector3I co, int size, ref FaceBounds orig)
    {
        Cube nc = octa.NeighbourCube(cu, orient, co, size, out Vector3I no, out int nsize);
        FaceBounds mincf = new(orig.U2, orig.U1, orig.V2, orig.V1);
        ushort nmat = (cu.Material & Octa.MAT_ALPHA) != 0 ? Octa.MAT_AIR : Octa.MAT_ALPHA;
        MinCubeFace(nc, orient ^ 1, no, nsize, orig, ref mincf, nmat, Octa.MAT_ALPHA);

        bool smaller = false;
        if (mincf.U1 > orig.U1) { orig.U1 = mincf.U1; smaller = true; }
        if (mincf.U2 < orig.U2) { orig.U2 = mincf.U2; smaller = true; }
        if (mincf.V1 > orig.V1) { orig.V1 = mincf.V1; smaller = true; }
        if (mincf.V2 < orig.V2) { orig.V2 = mincf.V2; smaller = true; }
        return smaller;
    }

    /// <summary>octa.cpp:1435 clippoly</summary>
    public static bool ClipPoly(Poly p, in FaceBounds b)
    {
        Span<PVert> verts1 = stackalloc PVert[MaxFaceVerts + 4];
        Span<PVert> verts2 = stackalloc PVert[MaxFaceVerts + 4];
        int numverts1 = 0, numverts2 = 0;
        int px = p.Verts[p.NumVerts - 1].X, py = p.Verts[p.NumVerts - 1].Y;

        for (int i = 0; i < p.NumVerts; i++)
        {
            int x = p.Verts[i].X, y = p.Verts[i].Y;
            if (x < b.U1)
            {
                if (px > b.U2) verts1[numverts1++] = new PVert(b.U2, (ushort)(y + ((y - py) * (b.U2 - x)) / (x - px)));
                if (px > b.U1) verts1[numverts1++] = new PVert(b.U1, (ushort)(y + ((y - py) * (b.U1 - x)) / (x - px)));
            }
            else if (x > b.U2)
            {
                if (px < b.U1) verts1[numverts1++] = new PVert(b.U1, (ushort)(y + ((y - py) * (b.U1 - x)) / (x - px)));
                if (px < b.U2) verts1[numverts1++] = new PVert(b.U2, (ushort)(y + ((y - py) * (b.U2 - x)) / (x - px)));
            }
            else
            {
                if (px < b.U1)
                {
                    if (x > b.U1) verts1[numverts1++] = new PVert(b.U1, (ushort)(y + ((y - py) * (b.U1 - x)) / (x - px)));
                }
                else if (px > b.U2 && x < b.U2)
                {
                    verts1[numverts1++] = new PVert(b.U2, (ushort)(y + ((y - py) * (b.U2 - x)) / (x - px)));
                }
                verts1[numverts1++] = new PVert((ushort)x, (ushort)y);
            }
            px = x;
            py = y;
        }

        if (numverts1 < 3) return false;
        px = verts1[numverts1 - 1].X;
        py = verts1[numverts1 - 1].Y;

        for (int i = 0; i < numverts1; i++)
        {
            int x = verts1[i].X, y = verts1[i].Y;
            if (y < b.V1)
            {
                if (py > b.V2) verts2[numverts2++] = new PVert((ushort)(x + ((x - px) * (b.V2 - y)) / (y - py)), b.V2);
                if (py > b.V1) verts2[numverts2++] = new PVert((ushort)(x + ((x - px) * (b.V1 - y)) / (y - py)), b.V1);
            }
            else if (y > b.V2)
            {
                if (py < b.V1) verts2[numverts2++] = new PVert((ushort)(x + ((x - px) * (b.V1 - y)) / (y - py)), b.V1);
                if (py < b.V2) verts2[numverts2++] = new PVert((ushort)(x + ((x - px) * (b.V2 - y)) / (y - py)), b.V2);
            }
            else
            {
                if (py < b.V1)
                {
                    if (y > b.V1) verts2[numverts2++] = new PVert((ushort)(x + ((x - px) * (b.V1 - y)) / (y - py)), b.V1);
                }
                else if (py > b.V2 && y < b.V2)
                {
                    verts2[numverts2++] = new PVert((ushort)(x + ((x - px) * (b.V2 - y)) / (y - py)), b.V2);
                }
                verts2[numverts2++] = new PVert((ushort)x, (ushort)y);
            }
            px = x;
            py = y;
        }

        if (numverts2 < 3 || numverts2 > MaxFaceVerts) return false;
        for (int i = 0; i < numverts2; i++) p.Verts[i] = verts2[i];
        p.NumVerts = numverts2;
        return true;
    }

    /// <summary>octa.cpp:1506 genpoly</summary>
    public static bool GenPoly(Octa octa, Cube cu, int orient, Vector3I o, int size, int vis,
                               out Vector3I n, out int offset, Poly p)
    {
        int dim = orient >> 1;
        int coord = DimCoord(orient);
        Span<Vector3I> v = stackalloc Vector3I[4];
        cu.GenFaceVerts(orient, v);

        if (cu.FlatAxisFace(orient))
        {
            n = Vector3I.Zero;
            SetComponent(ref n, dim, coord != 0 ? 1 : -1);
        }
        else
        {
            if (CubeGeometry.FaceConvexity(v) != 0)
            {
                n = Vector3I.Zero;
                offset = 0;
                return false;
            }
            n = CubeGeometry.Cross(v[1] - v[0], v[2] - v[0]);
            if (n == Vector3I.Zero) n = CubeGeometry.Cross(v[2] - v[0], v[3] - v[0]);
            ReduceSlope(ref n);
        }

        var po = new Vector3I((o.X & 0xFFF) << 3, (o.Y & 0xFFF) << 3, (o.Z & 0xFFF) << 3);
        for (int k = 0; k < 4; k++) v[k] = v[k] * size + po;
        offset = -CubeGeometry.Dot(n, v[3]);

        int r = Octa.R[dim], c = Octa.C[dim];
        int order = (vis & 4) != 0 ? 1 : 0;
        p.NumVerts = 0;

        if (coord != 0)
        {
            Vector3I v0 = v[order];
            p.Verts[p.NumVerts++] = new PVert((ushort)Component(v0, c), (ushort)Component(v0, r));
            if ((vis & 1) != 0)
            {
                Vector3I v1 = v[order + 1];
                p.Verts[p.NumVerts++] = new PVert((ushort)Component(v1, c), (ushort)Component(v1, r));
            }
            Vector3I v2 = v[order + 2];
            p.Verts[p.NumVerts++] = new PVert((ushort)Component(v2, c), (ushort)Component(v2, r));
            if ((vis & 2) != 0)
            {
                Vector3I v3 = v[(order + 3) & 3];
                p.Verts[p.NumVerts++] = new PVert((ushort)Component(v3, c), (ushort)Component(v3, r));
            }
        }
        else
        {
            if ((vis & 2) != 0)
            {
                Vector3I v3 = v[(order + 3) & 3];
                p.Verts[p.NumVerts++] = new PVert((ushort)Component(v3, c), (ushort)Component(v3, r));
            }
            Vector3I v2 = v[order + 2];
            p.Verts[p.NumVerts++] = new PVert((ushort)Component(v2, c), (ushort)Component(v2, r));
            if ((vis & 1) != 0)
            {
                Vector3I v1 = v[order + 1];
                p.Verts[p.NumVerts++] = new PVert((ushort)Component(v1, c), (ushort)Component(v1, r));
            }
            Vector3I v0 = v[order];
            p.Verts[p.NumVerts++] = new PVert((ushort)Component(v0, c), (ushort)Component(v0, r));
        }

        if (OctaVisible.FaceEdges(cu, orient) != Octa.F_SOLID)
        {
            int px = p.Verts[p.NumVerts - 2].X - p.Verts[p.NumVerts - 3].X;
            int py = p.Verts[p.NumVerts - 2].Y - p.Verts[p.NumVerts - 3].Y;
            int cx = p.Verts[p.NumVerts - 1].X - p.Verts[p.NumVerts - 2].X;
            int cy = p.Verts[p.NumVerts - 1].Y - p.Verts[p.NumVerts - 2].Y;
            int dir = px * cy - py * cx;
            if (dir > 0) return false;
            if (dir == 0)
            {
                if (p.NumVerts < 4) return false;
                p.Verts[p.NumVerts - 2] = p.Verts[p.NumVerts - 1];
                p.NumVerts--;
            }

            px = cx; py = cy;
            cx = p.Verts[0].X - p.Verts[p.NumVerts - 1].X;
            cy = p.Verts[0].Y - p.Verts[p.NumVerts - 1].Y;
            dir = px * cy - py * cx;
            if (dir > 0) return false;
            if (dir == 0)
            {
                if (p.NumVerts < 4) return false;
                p.NumVerts--;
            }

            px = cx; py = cy;
            cx = p.Verts[1].X - p.Verts[0].X;
            cy = p.Verts[1].Y - p.Verts[0].Y;
            dir = px * cy - py * cx;
            if (dir > 0) return false;
            if (dir == 0)
            {
                if (p.NumVerts < 4) return false;
                p.Verts[0] = p.Verts[p.NumVerts - 1];
                p.NumVerts--;
            }

            px = cx; py = cy;
            cx = p.Verts[2].X - p.Verts[1].X;
            cy = p.Verts[2].Y - p.Verts[1].Y;
            dir = px * cy - py * cx;
            if (dir > 0) return false;
            if (dir == 0)
            {
                if (p.NumVerts < 4) return false;
                p.Verts[1] = p.Verts[2];
                p.Verts[2] = p.Verts[3];
                p.NumVerts--;
            }
        }

        p.C = cu;
        p.Merged = false;

        if (MinFace != 0 && size >= (1 << MinFace) && cu.TouchingFace(orient))
        {
            FaceBounds b = new(p.Verts[0].X, p.Verts[0].X, p.Verts[0].Y, p.Verts[0].Y);
            for (int i = 1; i < p.NumVerts; i++)
            {
                PVert vi = p.Verts[i];
                b.U1 = System.Math.Min(b.U1, vi.X);
                b.U2 = System.Math.Max(b.U2, vi.X);
                b.V1 = System.Math.Min(b.V1, vi.Y);
                b.V2 = System.Math.Max(b.V2, vi.Y);
            }
            if (MinCubeFace(octa, cu, orient, o, size, ref b) && ClipPoly(p, b))
                p.Merged = true;
        }

        return true;
    }

    /// <summary>octa.cpp:1604 mergepolys (pairwise)</summary>
    public static bool MergePolys(int orient, Dictionary<PEdge, PLink> links, List<PLink> queue,
                                  int owner, Poly p, Poly q, in PEdge e)
    {
        int pe = -1, qe = -1;
        for (int i = 0; i < p.NumVerts; i++) if (p.Verts[i] == e.From) { pe = i; break; }
        for (int i = 0; i < q.NumVerts; i++) if (q.Verts[i] == e.To) { qe = i; break; }
        if (pe < 0 || qe < 0) return false;
        if (p.Verts[(pe + 1) % p.NumVerts] != e.To || q.Verts[(qe + 1) % q.NumVerts] != e.From) return false;

        Span<PVert> verts = stackalloc PVert[2 * MaxFaceVerts];
        int numverts = 0, index = pe + 2;
        for (int i = 0; i < p.NumVerts - 1; i++)
        {
            if (index >= p.NumVerts) index -= p.NumVerts;
            verts[numverts++] = p.Verts[index++];
        }

        index = qe + 2;
        int px = verts[numverts - 1].X - verts[numverts - 2].X;
        int py = verts[numverts - 1].Y - verts[numverts - 2].Y;
        for (int i = 0; i < q.NumVerts - 1; i++)
        {
            if (index >= q.NumVerts) index -= q.NumVerts;
            PVert src = q.Verts[index++];
            int cx = src.X - verts[numverts - 1].X;
            int cy = src.Y - verts[numverts - 1].Y;
            int dir = px * cy - py * cx;
            if (dir > 0) return false;
            if (dir == 0) numverts--;
            verts[numverts++] = src;
            px = cx;
            py = cy;
        }

        int endCx = verts[0].X - verts[numverts - 1].X;
        int endCy = verts[0].Y - verts[numverts - 1].Y;
        int endDir = px * endCy - py * endCx;
        if (endDir > 0) return false;
        if (endDir == 0) numverts--;

        if (numverts > MaxFaceVerts) return false;

        q.Merged = true;
        q.NumVerts = 0;

        p.Merged = true;
        p.NumVerts = numverts;
        for (int i = 0; i < numverts; i++) p.Verts[i] = verts[i];

        int prev = p.NumVerts - 1;
        for (int j = 0; j < p.NumVerts; j++)
        {
            PVert ef = p.Verts[prev], et = p.Verts[j];
            int order = ef.X > et.X || (ef.X == et.X && ef.Y > et.Y) ? 1 : 0;
            PEdge peEdge = order != 0 ? new PEdge(et, ef) : new PEdge(ef, et);

            if (!links.TryGetValue(peEdge, out var l))
            {
                l = new PLink(peEdge);
                links[peEdge] = l;
            }
            bool shouldQueue = l.Polys[order] < 0 && l.Polys[order ^ 1] >= 0;
            l.Polys[order] = owner;
            if (shouldQueue) queue.Add(l);
            prev = j;
        }

        return true;
    }

    /// <summary>octa.cpp:1653 addmerge</summary>
    public static void AddMerge(Cube cu, int orient, Vector3I co, Vector3I n, int offset, Poly p)
    {
        cu.Merged |= (byte)(1 << orient);
        if (p.NumVerts == 0)
        {
            if (cu.Ext != null) cu.Ext.Surfaces[orient] = new SurfaceInfo();
            return;
        }

        SurfaceInfo surf = new() { NumVerts = (byte)(Octa.LAYER_TOP | p.NumVerts) };
        Span<VertInfo> verts = stackalloc VertInfo[p.NumVerts];
        int dim = orient >> 1;
        int coord = DimCoord(orient);
        int c = Octa.C[dim], r = Octa.R[dim];

        for (int k = 0; k < p.NumVerts; k++)
        {
            PVert src = p.Verts[coord != 0 ? k : p.NumVerts - 1 - k];
            int nd = Component(n, dim);
            int vdim = nd != 0 ? -(offset + Component(n, c) * src.X + Component(n, r) * src.Y) / nd : 0;

            ushort vx = (ushort)(dim == 0 ? vdim : (c == 0 ? src.X : src.Y));
            ushort vy = (ushort)(dim == 1 ? vdim : (c == 1 ? src.X : src.Y));
            ushort vz = (ushort)(dim == 2 ? vdim : (c == 2 ? src.X : src.Y));

            verts[k] = new VertInfo { X = vx, Y = vy, Z = vz };
        }

        if (cu.Ext != null)
        {
            ref SurfaceInfo oldSurf = ref cu.Ext.Surfaces[orient];
            int oldNumVerts = oldSurf.NumVerts & Octa.MAXFACEVERTS;
            if (oldNumVerts == p.NumVerts && cu.Ext.Verts.Length >= oldSurf.Verts + oldNumVerts)
            {
                var v0 = verts[0];
                int oldOffset = oldSurf.Verts;
                for (int j = 0; j < oldNumVerts; j++)
                {
                    var ov = cu.Ext.Verts[oldOffset + j];
                    if (v0.X == ov.X && v0.Y == ov.Y && v0.Z == ov.Z)
                    {
                        bool match = true;
                        int cur = j;
                        for (int m = 1; m < oldNumVerts; m++)
                        {
                            if (++cur >= oldNumVerts) cur = 0;
                            var ovk = cu.Ext.Verts[oldOffset + cur];
                            if (verts[m].X != ovk.X || verts[m].Y != ovk.Y || verts[m].Z != ovk.Z)
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match) return;
                        break;
                    }
                }
            }
        }

        SetSurface(cu, orient, surf, verts);
    }

    /// <summary>octa.cpp:1696 clearmerge</summary>
    public static void ClearMerge(Cube c, int orient)
    {
        if ((c.Merged & (1 << orient)) != 0)
        {
            c.Merged = (byte)(c.Merged & ~(1 << orient));
            if (c.Ext != null)
            {
                c.Ext.Surfaces[orient] = new SurfaceInfo { NumVerts = Octa.LAYER_TOP };
            }
        }
    }

    /// <summary>octa.cpp:1705 addmerges</summary>
    public static void AddMerges(int orient, Vector3I co, Vector3I n, int offset, List<Poly> polys)
    {
        for (int i = 0; i < polys.Count; i++)
        {
            Poly p = polys[i];
            if (p.Merged) AddMerge(p.C, orient, co, n, offset, p);
            else ClearMerge(p.C, orient);
        }
    }

    /// <summary>octa.cpp:1715 mergepolys (list)</summary>
    public static void MergePolys(int orient, Vector3I co, Vector3I n, int offset, List<Poly> polys)
    {
        if (polys.Count <= 1)
        {
            AddMerges(orient, co, n, offset, polys);
            return;
        }

        var links = new Dictionary<PEdge, PLink>(polys.Count <= 32 ? 128 : 1024);
        var queue = new List<PLink>();

        for (int i = 0; i < polys.Count; i++)
        {
            Poly p = polys[i];
            int prev = p.NumVerts - 1;
            for (int j = 0; j < p.NumVerts; j++)
            {
                PVert ef = p.Verts[prev], et = p.Verts[j];
                int order = ef.X > et.X || (ef.X == et.X && ef.Y > et.Y) ? 1 : 0;
                PEdge e = order != 0 ? new PEdge(et, ef) : new PEdge(ef, et);

                if (!links.TryGetValue(e, out var l))
                {
                    l = new PLink(e);
                    links[e] = l;
                }
                l.Polys[order] = i;
                if (l.Polys[0] >= 0 && l.Polys[1] >= 0) queue.Add(l);
                prev = j;
            }
        }

        var nextQueue = new List<PLink>();
        while (queue.Count > 0)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                PLink l = queue[i];
                if (l.Polys[0] >= 0 && l.Polys[1] >= 0)
                    MergePolys(orient, links, nextQueue, l.Polys[0], polys[l.Polys[0]], polys[l.Polys[1]], l.Edge);
            }
            queue.Clear();
            queue.AddRange(nextQueue);
            nextQueue.Clear();
        }

        AddMerges(orient, co, n, offset, polys);
    }

    /// <summary>
    /// Writes a surface and its vertices into a cube's CubeExt, repacking Verts cleanly.
    /// </summary>
    public static void SetSurface(Cube c, int orient, SurfaceInfo surf, ReadOnlySpan<VertInfo> newVerts)
    {
        c.Ext ??= new CubeExt();
        var ext = c.Ext;

        int totalVerts = 0;
        for (int i = 0; i < 6; i++)
        {
            if (i == orient)
                totalVerts += newVerts.Length;
            else
                totalVerts += ext.Surfaces[i].TotalVerts();
        }

        var verts = new VertInfo[totalVerts];
        int offset = 0;
        for (int i = 0; i < 6; i++)
        {
            if (i == orient)
            {
                surf.Verts = (byte)offset;
                for (int k = 0; k < newVerts.Length; k++)
                    verts[offset++] = newVerts[k];
                ext.Surfaces[i] = surf;
            }
            else
            {
                int count = ext.Surfaces[i].TotalVerts();
                if (count > 0 && ext.Verts.Length >= ext.Surfaces[i].Verts + count)
                {
                    int oldOffset = ext.Surfaces[i].Verts;
                    ext.Surfaces[i].Verts = (byte)offset;
                    for (int k = 0; k < count; k++)
                        verts[offset++] = ext.Verts[oldOffset + k];
                }
            }
        }
        ext.Verts = verts;
        ext.MaxVerts = (byte)totalVerts;
    }

    /// <summary>octa.cpp:1843 invalidatemerges</summary>
    public static void InvalidateMerges(Cube c)
    {
        if (c.Merged != 0) c.Merged = 0;
        if (c.Ext != null && c.Ext.TJoints >= 0) c.Ext.TJoints = -1;
        if (c.Children != null)
        {
            for (int i = 0; i < 8; i++)
                InvalidateMerges(c.Children[i]);
        }
    }

    /// <summary>octa.cpp:1875 calcmerges</summary>
    public static void CalcMerges(Octa octa)
    {
        var cpolys = new Dictionary<CFKey, List<Poly>>();
        GenMerges(octa.WorldRoot, Vector3I.Zero, octa.WorldSize >> 1, octa, cpolys, isWorldRoot: true);
    }

    /// <summary>octa.cpp:1765 genmerges</summary>
    private static void GenMerges(Cube[] cubes, Vector3I o, int size, Octa octa,
                                  Dictionary<CFKey, List<Poly>> cpolys, bool isWorldRoot = false)
    {
        for (int i = 0; i < 8; i++)
        {
            var co = new Vector3I(
                o.X + ((i & 1) >> 0) * size,
                o.Y + ((i & 2) >> 1) * size,
                o.Z + ((i & 4) >> 2) * size);
            Cube c = cubes[i];

            if (c.Children != null)
            {
                GenMerges(c.Children, co, size >> 1, octa, cpolys);
            }
            else if (!c.IsEmpty())
            {
                for (int j = 0; j < 6; j++)
                {
                    int vis = OctaVisible.VisibleTris(octa, c, j, co, size);
                    if (vis != 0)
                    {
                        var p = new Poly();
                        if (size < (1 << MaxMerge) && !isWorldRoot)
                        {
                            if (GenPoly(octa, c, j, co, size, vis, out Vector3I n, out int offset, p))
                            {
                                var k = new CFKey((byte)j, (ushort)(c.Material & Octa.MAT_ALPHA), c.Texture[j], n, offset);
                                if (!cpolys.TryGetValue(k, out var list))
                                {
                                    list = new List<Poly>();
                                    cpolys[k] = list;
                                }
                                list.Add(p);
                                continue;
                            }
                        }
                        else if (MinFace != 0 && size >= (1 << MinFace) && c.TouchingFace(j))
                        {
                            if (GenPoly(octa, c, j, co, size, vis, out Vector3I n, out int offset, p) && p.Merged)
                            {
                                AddMerge(c, j, co, n, offset, p);
                                continue;
                            }
                        }
                        ClearMerge(c, j);
                    }
                }
            }

            if ((size == (1 << MaxMerge) || isWorldRoot) && cpolys.Count > 0)
            {
                var keys = new List<CFKey>(cpolys.Keys);
                keys.Sort(); // deterministic ordering
                foreach (var key in keys)
                {
                    MergePolys(key.Orient, co, key.N, key.Offset, cpolys[key]);
                }
                cpolys.Clear();
            }
        }
    }
}
