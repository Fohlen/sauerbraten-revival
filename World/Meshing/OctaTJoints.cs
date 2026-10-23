using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Meshing;

/// <summary>
/// Port of Sauerbraten's T-joint detection algorithm from src/engine/octarender.cpp.
/// Eliminates cracks/sparklies at the boundaries between different-sized or unaligned
/// adjacent cubes by finding T-junction vertices and linking them to cube edges.
/// </summary>
public static class OctaTJoints
{
    public const byte CE_START = 1 << 0;
    public const byte CE_END = 1 << 1;
    public const byte CE_FLIP = 1 << 2;
    public const byte CE_DUP = 1 << 3;

    public readonly struct EdgeGroup : IEquatable<EdgeGroup>
    {
        public readonly Vector3I Slope;
        public readonly Vector3I Origin;
        public readonly int Axis;

        public EdgeGroup(Vector3I origin, Vector3I slope, int axis)
        {
            Origin = origin;
            Slope = slope;
            Axis = axis;
        }

        public bool Equals(EdgeGroup other) =>
            Slope == other.Slope && Origin == other.Origin && Axis == other.Axis;

        public override bool Equals(object? obj) =>
            obj is EdgeGroup other && Equals(other);

        public override int GetHashCode() =>
            Slope.X ^ (Slope.Y << 2) ^ (Slope.Z << 4) ^ Origin.X ^ (Origin.Y << 6) ^ (Origin.Z << 8);

        public static bool operator ==(EdgeGroup a, EdgeGroup b) => a.Equals(b);
        public static bool operator !=(EdgeGroup a, EdgeGroup b) => !a.Equals(b);
    }

    public struct CubeEdge
    {
        public Cube C;
        public int Next;
        public int Offset;
        public ushort Size;
        public byte Index;
        public byte Flags;
    }

    /// <summary>
    /// Find all T-joints in the octree and link them into <see cref="CubeExt.TJoints"/>
    /// and <see cref="Octa.TJoints"/>.
    /// </summary>
    public static void FindTJoints(Octa octa)
    {
        octa.TJoints.Clear();
        ClearCubeTJoints(octa.WorldRoot);

        var cubeedges = new List<CubeEdge>();
        var edgegroups = new Dictionary<EdgeGroup, int>();

        GenCubeEdges(octa, octa.WorldRoot, Vector3I.Zero, octa.WorldSize >> 1, cubeedges, edgegroups);

        var sortedGroups = new List<EdgeGroup>(edgegroups.Keys);
        sortedGroups.Sort(CompareEdgeGroups);

        foreach (var group in sortedGroups)
        {
            FindTJointsForGroup(edgegroups[group], group, cubeedges, octa);
        }
    }

    public static int CompareEdgeGroups(EdgeGroup a, EdgeGroup b)
    {
        int c = a.Axis.CompareTo(b.Axis);
        if (c != 0) return c;
        c = a.Slope.X.CompareTo(b.Slope.X);
        if (c != 0) return c;
        c = a.Slope.Y.CompareTo(b.Slope.Y);
        if (c != 0) return c;
        c = a.Slope.Z.CompareTo(b.Slope.Z);
        if (c != 0) return c;
        c = a.Origin.X.CompareTo(b.Origin.X);
        if (c != 0) return c;
        c = a.Origin.Y.CompareTo(b.Origin.Y);
        return c != 0 ? c : a.Origin.Z.CompareTo(b.Origin.Z);
    }

    private static void ClearCubeTJoints(Cube[] cubes)
    {
        for (int i = 0; i < 8; i++)
        {
            var c = cubes[i];
            if (c.Ext != null) c.Ext.TJoints = -1;
            if (c.Children != null) ClearCubeTJoints(c.Children);
        }
    }

    private static void GenCubeEdges(Octa octa, Cube[] cubes, Vector3I co, int size,
                                    List<CubeEdge> cubeedges, Dictionary<EdgeGroup, int> edgegroups)
    {
        for (int i = 0; i < 8; i++)
        {
            var childCo = new Vector3I(
                co.X + ((i & 1) >> 0) * size,
                co.Y + ((i & 2) >> 1) * size,
                co.Z + ((i & 4) >> 2) * size);
            var c = cubes[i];
            if (c.Children != null)
            {
                GenCubeEdges(octa, c.Children, childCo, size >> 1, cubeedges, edgegroups);
            }
            else if (!c.IsEmpty())
            {
                GenCubeEdges(octa, c, childCo, size, cubeedges, edgegroups);
            }
        }
    }

    private static void GenCubeEdges(Octa octa, Cube c, Vector3I co, int size,
                                    List<CubeEdge> cubeedges, Dictionary<EdgeGroup, int> edgegroups)
    {
        Span<Vector3I> pos = stackalloc Vector3I[Octa.MAXFACEVERTS];
        Span<Vector3I> v4 = stackalloc Vector3I[4];
        for (int i = 0; i < 6; i++)
        {
            int vis = OctaVisible.VisibleTris(octa, c, i, co, size);
            if (vis == 0) continue;

            int numverts = 0;
            if (c.Ext != null && (c.Ext.Surfaces[i].NumVerts & Octa.MAXFACEVERTS) != 0)
            {
                var surf = c.Ext.Surfaces[i];
                numverts = surf.NumVerts & Octa.MAXFACEVERTS;
                var vo = new Vector3I((co.X & ~0xFFF) << 3, (co.Y & ~0xFFF) << 3, (co.Z & ~0xFFF) << 3);
                for (int j = 0; j < numverts; j++)
                {
                    var v = c.Ext.Verts[surf.Verts + j];
                    pos[j] = new Vector3I(v.X + vo.X, v.Y + vo.Y, v.Z + vo.Z);
                }
            }
            else if ((c.Merged & (1 << i)) != 0)
            {
                continue;
            }
            else
            {
                c.GenFaceVerts(i, v4);
                int convex = c.FlatAxisFace(i) ? 0 : CubeGeometry.FaceConvexity(v4);
                int order = (vis & 4) != 0 || convex < 0 ? 1 : 0;
                var vo = new Vector3I(co.X << 3, co.Y << 3, co.Z << 3);
                pos[numverts++] = v4[(order + 0) & 3] * size + vo;
                if ((vis & 1) != 0) pos[numverts++] = v4[(order + 1) & 3] * size + vo;
                pos[numverts++] = v4[(order + 2) & 3] * size + vo;
                if ((vis & 2) != 0) pos[numverts++] = v4[(order + 3) & 3] * size + vo;
            }

            for (int j = 0; j < numverts; j++)
            {
                int e1 = j;
                int e2 = j + 1 < numverts ? j + 1 : 0;
                Vector3I d = pos[e2] - pos[e1];
                if (d == Vector3I.Zero) continue;

                int absX = Math.Abs(d.X), absY = Math.Abs(d.Y), absZ = Math.Abs(d.Z);
                int axis = absX > absY ? (absX > absZ ? 0 : 2) : (absY > absZ ? 1 : 2);
                if (d[axis] < 0)
                {
                    d = -d;
                    (e1, e2) = (e2, e1);
                }
                OctaMerges.ReduceSlope(ref d);

                int t1 = pos[e1][axis] / d[axis];
                int t2 = pos[e2][axis] / d[axis];

                var g = new EdgeGroup(pos[e1] - d * t1, d, axis);
                var ce = new CubeEdge
                {
                    C = c,
                    Offset = t1,
                    Size = (ushort)(t2 - t1),
                    Index = (byte)(i * (Octa.MAXFACEVERTS + 1) + j),
                    Flags = (byte)(CE_START | CE_END | (e1 != j ? CE_FLIP : 0)),
                    Next = -1,
                };

                bool insert = true;
                if (edgegroups.TryGetValue(g, out int head))
                {
                    int prev = -1, cur = head;
                    while (cur >= 0)
                    {
                        var p = cubeedges[cur];
                        if (ce.Offset <= p.Offset + p.Size)
                        {
                            if (ce.Offset < p.Offset) break;
                            if ((p.Flags & CE_DUP) != 0
                                ? ce.Offset + ce.Size <= p.Offset + p.Size
                                : ce.Offset == p.Offset && ce.Size == p.Size)
                            {
                                p.Flags |= CE_DUP;
                                cubeedges[cur] = p;
                                insert = false;
                                break;
                            }
                            if (ce.Offset == p.Offset + p.Size)
                                ce.Flags &= unchecked((byte)~CE_START);
                        }
                        prev = cur;
                        cur = p.Next;
                    }

                    if (insert)
                    {
                        ce.Next = cur;
                        int cur2 = cur;
                        while (cur2 >= 0)
                        {
                            var p = cubeedges[cur2];
                            if (ce.Offset + ce.Size == p.Offset)
                            {
                                ce.Flags &= unchecked((byte)~CE_END);
                                break;
                            }
                            cur2 = p.Next;
                        }
                        int newIdx = cubeedges.Count;
                        if (prev >= 0)
                        {
                            var p = cubeedges[prev];
                            p.Next = newIdx;
                            cubeedges[prev] = p;
                        }
                        else
                        {
                            edgegroups[g] = newIdx;
                        }
                    }
                }
                else
                {
                    edgegroups[g] = cubeedges.Count;
                }

                if (insert)
                    cubeedges.Add(ce);
            }
        }
    }

    public static void FindTJointsForGroup(int cur, in EdgeGroup g, List<CubeEdge> cubeedges, Octa octa)
    {
        int active = -1;
        while (cur >= 0)
        {
            var e = cubeedges[cur];
            int prevactive = -1, curactive = active;
            while (curactive >= 0)
            {
                var a = cubeedges[curactive];
                if (a.Offset + a.Size <= e.Offset)
                {
                    if (prevactive >= 0)
                    {
                        var pa = cubeedges[prevactive];
                        pa.Next = a.Next;
                        cubeedges[prevactive] = pa;
                    }
                    else active = a.Next;
                }
                else
                {
                    prevactive = curactive;
                    if ((a.Flags & CE_DUP) == 0)
                    {
                        if ((e.Flags & CE_START) != 0 && e.Offset > a.Offset && e.Offset < a.Offset + a.Size)
                            AddTJoint(octa, g, a, e.Offset);
                        if ((e.Flags & CE_END) != 0 && e.Offset + e.Size > a.Offset && e.Offset + e.Size < a.Offset + a.Size)
                            AddTJoint(octa, g, a, e.Offset + e.Size);
                    }
                    if ((e.Flags & CE_DUP) == 0)
                    {
                        if ((a.Flags & CE_START) != 0 && a.Offset > e.Offset && a.Offset < e.Offset + e.Size)
                            AddTJoint(octa, g, e, a.Offset);
                        if ((a.Flags & CE_END) != 0 && a.Offset + a.Size > e.Offset && a.Offset + a.Size < e.Offset + e.Size)
                            AddTJoint(octa, g, e, a.Offset + a.Size);
                    }
                }
                curactive = a.Next;
            }
            int next = e.Next;
            e.Next = active;
            cubeedges[cur] = e;
            active = cur;
            cur = next;
        }
    }

    public static void AddTJoint(Octa octa, in EdgeGroup g, in CubeEdge e, int offset)
    {
        int vcoord = (g.Slope[g.Axis] * offset + g.Origin[g.Axis]) & 0x7FFF;
        var c = e.C;
        c.Ext ??= new CubeExt();

        int tjIndex = octa.TJoints.Count;
        var tj = new TJoint
        {
            Offset = vcoord / g.Slope[g.Axis],
            Edge = e.Index,
            Next = -1,
        };
        octa.TJoints.Add(tj);

        int prev = -1, cur = c.Ext.TJoints;
        while (cur >= 0)
        {
            var o = octa.TJoints[cur];
            bool flip = (e.Flags & CE_FLIP) != 0;
            if (tj.Edge < o.Edge || (tj.Edge == o.Edge && (flip ? tj.Offset > o.Offset : tj.Offset < o.Offset)))
                break;
            prev = cur;
            cur = o.Next;
        }

        tj.Next = cur;
        octa.TJoints[tjIndex] = tj;
        if (prev < 0) c.Ext.TJoints = tjIndex;
        else
        {
            var p = octa.TJoints[prev];
            p.Next = tjIndex;
            octa.TJoints[prev] = p;
        }
    }
}
