using System;
using Godot;

namespace SauerbratenRevival.World;

/// <summary>
/// Geometry helpers shared by the OGZ reader and the mesher — a port of the
/// face-vertex math in src/engine/octa.cpp / octa.h.
/// </summary>
public static class CubeGeometry
{
    // genfaceverts (octa.cpp): the corner bits for each orientation. Values are
    // 0/1 (the 0/8 scaling is applied by the caller). Indexed [orient][vert][axis].
    public static readonly int[][][] FaceCorner =
    {
        new[] { new[] { 0, 1, 1 }, new[] { 0, 1, 0 }, new[] { 0, 0, 0 }, new[] { 0, 0, 1 } }, // -X
        new[] { new[] { 1, 1, 1 }, new[] { 1, 0, 1 }, new[] { 1, 0, 0 }, new[] { 1, 1, 0 } }, // +X
        new[] { new[] { 1, 0, 1 }, new[] { 0, 0, 1 }, new[] { 0, 0, 0 }, new[] { 1, 0, 0 } }, // -Y
        new[] { new[] { 0, 1, 0 }, new[] { 0, 1, 1 }, new[] { 1, 1, 1 }, new[] { 1, 1, 0 } }, // +Y
        new[] { new[] { 0, 0, 0 }, new[] { 0, 1, 0 }, new[] { 1, 1, 0 }, new[] { 1, 0, 0 } }, // -Z
        new[] { new[] { 0, 0, 1 }, new[] { 1, 0, 1 }, new[] { 1, 1, 1 }, new[] { 0, 1, 1 } }, // +Z
    };

    /// <summary>octa.cpp:673 <c>genfaceverts</c> for a runtime cube.</summary>
    public static void GenFaceVerts(Cube c, int orient, Span<Vector3I> v)
    {
        Span<byte> edges = stackalloc byte[12];
        for (int d = 0; d < 3; d++)
            for (int i = 0; i < 4; i++)
                edges[d * 4 + i] = (byte)(c.Faces[d] >> (i * 8));
        GenFaceVerts(edges, orient, v);
    }

    /// <summary>octa.cpp:673 <c>genfaceverts</c> over raw edge bytes (used by the OGZ reader).</summary>
    public static void GenFaceVerts(ReadOnlySpan<byte> edges, int orient, Span<Vector3I> v)
    {
        for (int n = 0; n < 4; n++)
        {
            int x = FaceCorner[orient][n][0], y = FaceCorner[orient][n][1], z = FaceCorner[orient][n][2];
            // cubeedge(c, d, a, b) indexes edges[(d<<2)+(b<<1)+a]; edgeget takes a nibble.
            v[n] = new Vector3I(
                EdgeGet(edges[z * 2 + y], x),
                EdgeGet(edges[4 + x * 2 + z], y),
                EdgeGet(edges[8 + y * 2 + x], z));
        }
    }

    /// <summary>octa.h: <c>edgeget(edge, coord)</c>.</summary>
    public static int EdgeGet(byte edge, int coord) => coord != 0 ? (edge >> 4) & 0xF : edge & 0xF;

    /// <summary>The four corners (0/8 units) of an axis-aligned face (for material surfaces).</summary>
    public static void SolidFaceVerts(int orient, Span<Vector3I> v)
    {
        for (int n = 0; n < 4; n++)
        {
            int x = FaceCorner[orient][n][0], y = FaceCorner[orient][n][1], z = FaceCorner[orient][n][2];
            v[n] = new Vector3I(x * 8, y * 8, z * 8);
        }
    }

    /// <summary>octa.cpp:764 <c>faceconvexity(v)</c>: 1 convex, -1 concave, 0 flat.</summary>
    public static int FaceConvexity(ReadOnlySpan<Vector3I> v)
    {
        Vector3I a = v[1] - v[0], b = v[2] - v[0], d = v[0] - v[3];
        Vector3I n = Cross(a, b);
        return d.X * n.X + d.Y * n.Y + d.Z * n.Z;
    }

    /// <summary>Integer cross product (also used by the OGZ reader's surface-vertex math).</summary>
    public static Vector3I Cross(Vector3I a, Vector3I b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    /// <summary>Integer dot product.</summary>
    public static int Dot(Vector3I a, Vector3I b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>octa.cpp:125 <c>getcubevector(c, d, x, y, z, p)</c> — read the vertex at corner (x,y,z) of dimension d.</summary>
    public static Vector3I GetCubeVector(Cube c, int d, int x, int y, int z)
    {
        int[] v = { 0, 0, 0 };
        v[d] = 0; v[Octa.R[d]] = x; v[Octa.C[d]] = y; v[Octa.D[d]] = z;
        return new Vector3I(
            Cube.EdgeGet(CubeEdge(c, 0, v[Octa.R[0]], v[Octa.C[0]]), v[Octa.D[0]]),
            Cube.EdgeGet(CubeEdge(c, 1, v[Octa.R[1]], v[Octa.C[1]]), v[Octa.D[1]]),
            Cube.EdgeGet(CubeEdge(c, 2, v[Octa.R[2]], v[Octa.C[2]]), v[Octa.D[2]]));
    }

    /// <summary>octa.h: <c>cubeedge(c, d, x, y)</c> — edge nibble pair at (x,y) on face word d.</summary>
    public static byte CubeEdge(Cube c, int d, int x, int y) => c.CubeEdge(d, x, y);

    /// <summary>octa.cpp:133 <c>getcubevector(c, i, p)</c> — vertex i (0..7) of the cube.</summary>
    public static Vector3I GetCubeVector(Cube c, int i) => new(
        Cube.EdgeGet(c.CubeEdge(0, (i >> Octa.R[0]) & 1, (i >> Octa.C[0]) & 1), (i >> Octa.D[0]) & 1),
        Cube.EdgeGet(c.CubeEdge(1, (i >> Octa.R[1]) & 1, (i >> Octa.C[1]) & 1), (i >> Octa.D[1]) & 1),
        Cube.EdgeGet(c.CubeEdge(2, (i >> Octa.R[2]) & 1, (i >> Octa.C[2]) & 1), (i >> Octa.D[2]) & 1));

    /// <summary>octa.cpp:141 <c>setcubevector(c, i, p)</c> — write vertex i (0..7) of the cube.</summary>
    public static void SetCubeVector(Cube c, int i, Vector3I p)
    {
        c.SetCubeEdge(0, (i >> Octa.R[0]) & 1, (i >> Octa.C[0]) & 1, (byte)p.X);
        c.SetCubeEdge(1, (i >> Octa.R[1]) & 1, (i >> Octa.C[1]) & 1, (byte)p.Y);
        c.SetCubeEdge(2, (i >> Octa.R[2]) & 1, (i >> Octa.C[2]) & 1, (byte)p.Z);
    }
}
