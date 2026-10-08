using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Editor;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the editor's ray-cast seam (<see cref="OctaRay"/>) — the real
/// <c>raycube</c> port with <c>RAY_SKIPFIRST</c> and a grid size, mapping the hit
/// surface normal back to an axis-aligned orientation. The raw engine port is
/// asserted against tools/rayprobe in <see cref="OctaRaycastTest"/>; this test
/// locks the wrapper the cursor relies on.
/// </summary>
public class OctaRayTest : GoTestBase
{
    public OctaRayTest(Node testScene) : base(testScene) { }

    /// <summary>A worldsize-64 world with everything solid except a hollow cavity.</summary>
    private static Octa CarvedWorld()
    {
        var w = new Octa(6);
        var c = OctaEdit.LookupCube(w, new Vector3I(32, 32, 32), 8, out _, out _);
        w.DiscardChildren(c, true);
        c.EmptyFaces();
        return w;
    }

    [Test]
    public void SkipFirstStepsPastStartingSolid()
    {
        var w = new Octa(6); // fully solid
        // The eye is inside the solid cube at (0,0,0); RAY_SKIPFIRST skips it and
        // the ray hits the next solid cube at x=32 (the 0.1 DDA epsilon).
        float d = OctaRay.RayCube(w, new Vector3(10, 10, 10), new Vector3(1, 0, 0), 1000f, out int orient);
        TestAssert.True(Mathf.Abs(d - 22.1f) < 0.001f, $"hit the next solid cube at 22.1, got {d}");
        TestAssert.Equal(0, orient, "struck the -X face");
    }

    [Test]
    public void HitsWallFromAir()
    {
        var w = CarvedWorld();
        float d = OctaRay.RayCube(w, new Vector3(36, 36, 36), new Vector3(1, 0, 0), 1000f, 8, out int orient);
        TestAssert.True(Mathf.Abs(d - 4.1f) < 0.001f, $"hit the far wall at 4.1, got {d}");
        TestAssert.Equal(0, orient, "struck the -X face");
    }

    [Test]
    public void HitsTopWallFromAir()
    {
        var w = CarvedWorld();
        float d = OctaRay.RayCube(w, new Vector3(36, 36, 36), new Vector3(0, 0, 1), 1000f, 8, out int orient);
        TestAssert.True(Mathf.Abs(d - 4.1f) < 0.001f, $"hit the top wall at 4.1, got {d}");
        TestAssert.Equal(4, orient, "struck the -Z (bottom) face");
    }

    [Test]
    public void HitsNearWallFromAir()
    {
        var w = CarvedWorld();
        float d = OctaRay.RayCube(w, new Vector3(36, 36, 36), new Vector3(-1, 0, 0), 1000f, 8, out int orient);
        TestAssert.True(Mathf.Abs(d - 4.1f) < 0.001f, $"hit the near wall at 4.1, got {d}");
        TestAssert.Equal(1, orient, "struck the +X face");
    }

    [Test]
    public void CursorPlacesSelectionOnPointedCube()
    {
        // End-to-end: ray from inside the cavity toward the wall, then cursor update.
        var w = CarvedWorld();
        var cursor = new EditCursor();
        var eye = new Vector3(36, 36, 36);
        var dir = new Vector3(1, 0, 0);
        float dist = OctaRay.RayCube(w, eye, dir, 1000f, cursor.GridSize, out _);
        cursor.Update(w, eye, dir, dist);

        TestAssert.Equal(0, cursor.Orient, "cursor orient is the struck face");
        TestAssert.Equal(new Vector3I(40, 32, 32), cursor.Lu, "lookup cube is the pointed cube");
        TestAssert.Equal(new Vector3I(40, 32, 32), cursor.Selection.O, "selection anchored at the cursor");
        TestAssert.Equal(new Vector3I(1, 1, 1), cursor.Selection.S, "single-cell selection");
    }
}
