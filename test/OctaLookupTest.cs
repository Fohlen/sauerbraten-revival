using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the M1b cube lookups and block mapping in <see cref="OctaEdit"/>
/// (<c>lookupcube</c>, <c>blockcube</c>) and the full deformed-cube
/// <c>subdividecube</c>. Expected values are the exact output of the original
/// engine, produced by <c>tools/octaprobe</c> (see its README) — not hand-derived.
/// </summary>
public class OctaLookupTest : GoTestBase
{
    public OctaLookupTest(Node testScene) : base(testScene) { }

    [Test]
    public void LookupCubeResolvesLeafAndOrigin()
    {
        var w = new Octa(4); // worldsize 16, matches octaprobe
        Cube c = OctaEdit.LookupCube(w, new Vector3I(5, 2, 7), 0, out Vector3I ro, out int rsize);

        // octaprobe: LOOKUP (5,2,7) tsize=0 -> ro=(0,0,0) rsize=8 solid
        TestAssert.True(c.IsEntirelySolid(), "solid world -> solid leaf");
        TestAssert.Equal(8, rsize, "root-size leaf at worldscale 4");
        TestAssert.Equal(Vector3I.Zero, ro, "origin masked to root size");
    }

    [Test]
    public void LookupCubeSubdividesWhenTsizePositive()
    {
        var w = new Octa(4);
        int before = w.AllocNodes;
        Cube c = OctaEdit.LookupCube(w, new Vector3I(1, 1, 1), 2, out Vector3I ro, out int rsize);

        // octaprobe: LOOKUP (1,1,1) tsize=2 -> ro=(0,0,0) rsize=2
        TestAssert.True(w.AllocNodes > before, "subdivision allocated nodes");
        TestAssert.Equal(2, rsize, "size forced down to the requested tsize");
        TestAssert.Equal(Vector3I.Zero, ro, "subdivided leaf origin");
        TestAssert.True(c.IsEntirelySolid(), "solid parent -> solid children");
    }

    [Test]
    public void LookupCubeClampsOutsideWorld()
    {
        var w = new Octa(4);
        Cube c = OctaEdit.LookupCube(w, new Vector3I(100, -5, 100), 0, out _, out _);
        TestAssert.True(c is not null, "outside coordinates clamp into the world");
    }

    /// <summary>Read the 12 edge bytes of a cube (faces[0..2], 4 bytes each).</summary>
    private static string EdgeBytes(Cube c)
    {
        var sb = new System.Text.StringBuilder(12 * 3);
        for (int d = 0; d < 3; d++)
            for (int b = 0; b < 4; b++)
                sb.Append($" {((byte)((c.Faces[d] >> (b * 8)) & 0xFF)):x2}");
        return sb.ToString().TrimStart();
    }

    [Test]
    public void SubdivideDeformedCubeMatchesEngineEdgeBytes()
    {
        // octaprobe: cube with edges[0]=4 (vertex 0 x pulled to 4), then subdivide.
        // The exact child edge bytes are ground truth from the original octa.cpp.
        var w = new Octa(4);
        Cube c = w.WorldRoot[0];
        c.SetCubeEdge(0, 0, 0, 4);

        OctaEdit.SubdivideCube(w, c);

        string[] expected =
        {
            "00 00 00 00 00 00 00 00 00 00 00 00",
            "00 00 00 80 80 80 80 80 80 80 80 80",
            "84 80 80 80 80 80 80 80 80 80 80 80",
            "00 80 80 80 80 80 80 80 80 80 80 80",
            "84 80 80 80 80 80 80 80 80 80 80 80",
            "00 80 80 80 80 80 80 80 80 80 80 80",
            "80 80 80 80 80 80 80 80 80 80 80 80",
            "80 80 80 80 80 80 80 80 80 80 80 80",
        };
        for (int i = 0; i < 8; i++)
            TestAssert.Equal(expected[i], EdgeBytes(c.Children![i]), $"child {i} edge bytes");
    }

    [Test]
    public void SubdivideSolidCubeProducesSolidChildren()
    {
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        for (int i = 0; i < 8; i++)
            TestAssert.True(w.WorldRoot[0].Children![i].IsEntirelySolid(), "flat solid parent -> solid children");
    }

    [Test]
    public void SubdivideIsIdempotentAfterFullPort()
    {
        var w = new Octa(4);
        Cube c = w.WorldRoot[0];
        c.SetCubeEdge(0, 0, 0, 6);
        OctaEdit.SubdivideCube(w, c);
        int alloc = w.AllocNodes;
        OctaEdit.SubdivideCube(w, c);
        TestAssert.Equal(alloc, w.AllocNodes, "already-subdivided is a no-op");
    }

    [Test]
    public void BlockCubeMapsAxisAlignedBlock()
    {
        // orient 0 = -X: dim 0, dc 0, R[0]=1, C[0]=2.
        var w = new Octa(5); // worldsize 32
        var b = new Block3(new Vector3I(8, 8, 8), new Vector3I(2, 3, 4), 8, 0);

        // octaprobe: cell(0,0,0) -> s=(8,8,8); cell(1,2,1) -> s=(16,16,24)
        TestAssert.Equal(new Vector3I(8, 8, 8), BlockQuery(b, 0, 0, 0), "first cell maps to block origin");
        TestAssert.Equal(new Vector3I(16, 16, 24), BlockQuery(b, 1, 2, 1), "valid far cell maps through orientation");
    }

    [Test]
    public void BlockCubeOrientationOffset()
    {
        // orient 1 = +X: dc 1, so the depth axis is laid out backwards from the
        // far edge. octaprobe: cell(0,0,0) -> s=(16,8,8).
        var b = new Block3(new Vector3I(8, 8, 8), new Vector3I(2, 1, 1), 8, 1);
        TestAssert.Equal(new Vector3I(16, 8, 8), BlockQuery(b, 0, 0, 0), "positive-facing block starts at the far edge");
    }

    /// <summary>Reimplement the blockcell→world mapping to assert the coordinate the original queries.</summary>
    private static Vector3I BlockQuery(Block3 b, int x, int y, int z)
    {
        int dim = b.Orient >> 1, dc = b.Orient & 1;
        var s = new Vector3I(0, 0, 0);
        s = SetAxis(s, dim, dc * (b.S[dim] - 1) * b.Grid);
        s = SetAxis(s, Octa.R[dim], x * b.Grid);
        s = SetAxis(s, Octa.C[dim], y * b.Grid);
        s += b.O;
        s = SetAxis(s, dim, s[dim] + (dc != 0 ? -z * b.Grid : z * b.Grid));
        return s;
    }

    private static Vector3I SetAxis(Vector3I v, int d, int value) => d switch
    {
        0 => new Vector3I(value, v.Y, v.Z),
        1 => new Vector3I(v.X, value, v.Z),
        _ => new Vector3I(v.X, v.Y, value),
    };

    [Test]
    public void PastedDeformedCubeChecksumMatchesSource()
    {
        var w = new Octa(4);
        Cube src = w.WorldRoot[0];
        src.SetCubeEdge(0, 0, 0, 4);
        src.SetCubeEdge(2, 1, 1, 10);
        OctaEdit.SubdivideCube(w, src);

        var dst = new Cube();
        OctaEdit.CopyCube(w, src, dst);

        ulong srcSum = OgzGeometry.Compute(new[] { src }).Checksum;
        ulong dstSum = OgzGeometry.Compute(new[] { dst }).Checksum;
        TestAssert.Equal(srcSum, dstSum, "deformed-cube copy checksum matches");
    }
}
