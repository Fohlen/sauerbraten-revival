using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers selection flip/rotate (octaedit.cpp mpflip/mprotate). The engine port is
/// harder to differential-test (it needs sel/lookupcube globals), so these lock
/// algebraic invariants plus a concrete case.
/// </summary>
public class SelectionTransformTest : GoTestBase
{
    public SelectionTransformTest(Node testScene) : base(testScene) { }

    private static Selection Sel(int orient, Vector3I o, Vector3I s, int grid) =>
        new(o, s, grid, orient);

    [Test]
    public void FlipTwiceIsIdentity()
    {
        var w = new Octa(6);
        // Paint distinct materials along X so a flip is observable.
        for (int x = 0; x < 4; x++)
        {
            var c = OctaEdit.BlockCube(w, x, 0, 0, new Block3(new Vector3I(0, 0, 0), new Vector3I(4, 1, 1), 8, 1), 8);
            c.Material = (ushort)(x + 1);
        }
        var sel = Sel(1, new Vector3I(0, 0, 0), new Vector3I(4, 1, 1), 8); // orient +X, dim 0

        ulong before = Hash(w, 4);
        OctaEdit.FlipSelection(w, sel);
        ulong flipped = Hash(w, 4);
        TestAssert.True(before != flipped, "flip changed the selection");
        OctaEdit.FlipSelection(w, sel);
        TestAssert.Equal(before, Hash(w, 4), "flip twice restores");
    }

    [Test]
    public void RotateFourTimesIsIdentity()
    {
        var w = new Octa(6);
        var sel = Sel(4, new Vector3I(0, 0, 0), new Vector3I(3, 3, 1), 8); // orient Bottom, dim 2
        ulong before = Hash(w, 9);

        for (int i = 0; i < 4; i++) OctaEdit.RotateSelection(w, sel, cw: 1);
        TestAssert.Equal(before, Hash(w, 9), "rotate 4x restores");
    }

    [Test]
    public void FlipMirrorsPositions()
    {
        var w = new Octa(6);
        // Materials along world X (read via LookupCube; BlockCube maps through orient).
        for (int x = 0; x < 4; x++)
            OctaEdit.LookupCube(w, new Vector3I(x * 8 + 1, 1, 1), 8, out _, out _).Material = (ushort)(x + 1);

        var sel = Sel(1, new Vector3I(0, 0, 0), new Vector3I(4, 1, 1), 8);
        OctaEdit.FlipSelection(w, sel);

        TestAssert.Equal(4, MatAt(w, 0), "x=0 got x=3's material");
        TestAssert.Equal(1, MatAt(w, 3), "x=3 got x=0's material");
    }

    private static ushort MatAt(Octa w, int x)
        => OctaEdit.LookupCube(w, new Vector3I(x * 8 + 1, 1, 1), 0, out _, out _).Material;

    private static ulong Hash(Octa w, int n)
    {
        ulong h = 14695981039346656037UL;
        var block = new Block3(new Vector3I(0, 0, 0), new Vector3I(n, n, 1), 8, 1);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var c = OctaEdit.BlockCube(w, x, y, 0, block, 8);
                h ^= c.Material; h *= 1099511628211UL;
                h ^= c.Faces[0]; h *= 1099511628211UL;
                for (int f = 0; f < 6; f++) { h ^= c.Texture[f]; h *= 1099511628211UL; }
            }
        return h;
    }
}
