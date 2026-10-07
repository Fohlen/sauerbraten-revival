using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Editor;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Exercises the prototype octree ray cast (<see cref="OctaRay"/>) used to place
/// the editor cursor. The full engine <c>raycube</c> port is deferred; this locks
/// the behaviour the cursor relies on.
/// </summary>
public class OctaRayTest : GoTestBase
{
    public OctaRayTest(Node testScene) : base(testScene) { }

    /// <summary>A worldsize-64 world with everything solid except a hollow cavity.</summary>
    private static Octa CarvedWorld()
    {
        var w = new Octa(6);
        // Empty a size-8 cube at (32,32,32) so there is a hole to aim into.
        var sel = new SauerbratenRevival.World.Commands.Block3(new Vector3I(32, 32, 32), new Vector3I(1, 1, 1), 8, 0);
        OctaEdit.DeleteCube(w, sel);
        return w;
    }

    [Test]
    public void HitsSolidWall()
    {
        var w = new Octa(6); // fully solid
        float d = OctaRay.RayCube(w, new Vector3(10, 10, 10), new Vector3(1, 0, 0), 1000f, out int orient);
        TestAssert.True(d == 0f, "starts inside solid -> distance 0");
        TestAssert.Equal(-1, orient, "inside-solid hit has no face");
    }

    [Test]
    public void HitsWallFromAir()
    {
        var w = CarvedWorld();
        // Start inside the cavity (32..40 is empty), aim +X into the solid wall at x=40.
        float d = OctaRay.RayCube(w, new Vector3(36, 36, 36), new Vector3(1, 0, 0), 1000f, out int orient);
        TestAssert.True(Mathf.Abs(d - 4f) < 0.001f, $"hit the far wall at 4 units, got {d}");
        TestAssert.Equal(0, orient, "struck the -X face");
    }

    [Test]
    public void HitsTopWallFromAir()
    {
        var w = CarvedWorld();
        float d = OctaRay.RayCube(w, new Vector3(36, 36, 36), new Vector3(0, 0, 1), 1000f, out int orient);
        TestAssert.True(Mathf.Abs(d - 4f) < 0.001f, $"hit the top wall at 4 units, got {d}");
        TestAssert.Equal(4, orient, "struck the -Z (bottom) face");
    }

    [Test]
    public void OpenAirReturnsMax()
    {
        var w = CarvedWorld();
        // Inside the cavity, aim -X from x=36: the wall is at x=32, so it should hit.
        float d = OctaRay.RayCube(w, new Vector3(36, 36, 36), new Vector3(-1, 0, 0), 1000f, out int orient);
        TestAssert.True(Mathf.Abs(d - 4f) < 0.001f, $"hit the near wall at 4 units, got {d}");
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
        float dist = OctaRay.RayCube(w, eye, dir, 1000f, out int orient);
        cursor.Update(w, eye, dir, dist);

        TestAssert.Equal(0, cursor.Orient, "cursor orient is the struck face");
        TestAssert.Equal(new Vector3I(40, 32, 32), cursor.Lu, "lookup cube is the pointed cube");
        TestAssert.Equal(new Vector3I(40, 32, 32), cursor.Selection.O, "selection anchored at the cursor");
        TestAssert.Equal(new Vector3I(1, 1, 1), cursor.Selection.S, "single-cell selection");
    }
}
