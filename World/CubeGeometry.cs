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
    private static readonly int[][][] FaceCorner =
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
}
