using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the real <c>physics.cpp:raycube</c> port (<see cref="OctaRaycast"/>)
/// against the original engine's output, captured by tools/rayprobe. The golden
/// values (r6889) cover the octree DDA, the quick-hit paths (solid, RAY_EDITMAT,
/// RAY_CLIPMAT), the deformed-cube clip-plane / AABB intersection, and
/// RAY_SKIPFIRST.
/// </summary>
public class OctaRaycastTest : GoTestBase
{
    public OctaRaycastTest(Node testScene) : base(testScene) { }

    /// <summary>Fully solid world at the given scale (worldsize = 2^scale).</summary>
    private static Octa SolidWorld(int scale = 6) => new(scale);

    /// <summary>A solid world with a size-8 empty cavity at (32,32,32).</summary>
    private static Octa CarvedWorld()
    {
        var w = new Octa(6);
        var c = OctaEdit.LookupCube(w, new Vector3I(32, 32, 32), 8, out _, out _);
        w.DiscardChildren(c, true);
        c.EmptyFaces();
        return w;
    }

    /// <summary>
    /// An empty world with a single deformed solid cube at the origin (edge 0 nibble
    /// 4), render-hull visibility (0x80) — the rayprobe "deformed" scenario.
    /// </summary>
    private static Octa DeformedWorld()
    {
        var w = new Octa(4);
        foreach (var cube in w.WorldRoot) cube.EmptyFaces();
        var c = w.WorldRoot[0];
        c.SolidFaces();
        c.SetCubeEdge(0, 0, 0, 0x04); // edges[0] = 4: pull vertex 0 x to 4
        c.Visible = 0x80;
        return w;
    }

    private static void AssertRay(Octa w, Vector3 o, Vector3 dir, float radius, int mode, int size,
                                  float dist, Vector3 hit)
    {
        float d = OctaRaycast.RayCube(w, o, dir, radius, mode, size, out Vector3 h);
        TestAssert.True(Mathf.Abs(d - dist) < 0.001f, $"dist: got {d}, want {dist}");
        TestAssert.True((h - hit).LengthSquared() < 0.00001f, $"hit: got {h}, want {hit}");
    }

    [Test]
    public void SolidAboveQuickHit()
    {
        var w = SolidWorld(4);
        AssertRay(w, new Vector3(4, 4, 20), new Vector3(0, 0, -1), 1000f, 0, 0, 4.1f, Vector3.Zero);
    }

    [Test]
    public void CarvedCavityDda()
    {
        var w = CarvedWorld();
        AssertRay(w, new Vector3(36, 36, 36), new Vector3(1, 0, 0), 1000f, 0, 0, 4.1f, new Vector3(-1, 0, 0));
        AssertRay(w, new Vector3(36, 36, 36), new Vector3(-1, 0, 0), 1000f, 0, 0, 4.1f, new Vector3(1, 0, 0));
        AssertRay(w, new Vector3(36, 36, 36), new Vector3(0, 0, 1), 1000f, 0, 0, 4.1f, new Vector3(0, 0, -1));
    }

    [Test]
    public void DeformedCubeClipPlanes()
    {
        var w = DeformedWorld();
        AssertRay(w, new Vector3(2, 2, 20), new Vector3(0, 0, -1), 1000f, 0, 0, 12.1f, new Vector3(0, 0, 1));
        AssertRay(w, new Vector3(-5, 4, 4), new Vector3(1, 0, 0), 1000f, 0, 0, 5.1f, new Vector3(-1, 0, 0));
        // Deformed +X face triangle plane, normal (1,-1,-1)/sqrt(3).
        AssertRay(w, new Vector3(12, 4, 4), new Vector3(-1, 0, 0), 1000f, 0, 0, 4.1f,
                  new Vector3(0.577350f, -0.577350f, -0.577350f));
    }

    [Test]
    public void SkipFirstSkipsStartingCube()
    {
        var w = SolidWorld(4);
        AssertRay(w, new Vector3(4, 4, 8), new Vector3(0, 0, -1), 1000f, OctaRaycast.RaySkipFirst, 0, 0.1f, new Vector3(0, 0, 1));
    }

    [Test]
    public void MaterialQuickHits()
    {
        var w = new Octa(4);
        foreach (var cube in w.WorldRoot) cube.EmptyFaces();

        w.WorldRoot[0].Material = Octa.MAT_WATER;
        AssertRay(w, new Vector3(-5, 4, 4), new Vector3(1, 0, 0), 1000f, OctaRaycast.RayEditMat, 0, 5.1f, Vector3.Zero);

        w.WorldRoot[0].Material = Octa.MAT_GLASS;
        AssertRay(w, new Vector3(-5, 4, 4), new Vector3(1, 0, 0), 1000f, OctaRaycast.RayClipMat, 0, 5.1f, Vector3.Zero);
    }

    [Test]
    public void EditorRayEnt()
    {
        var w = CarvedWorld();
        float d = OctaRaycast.RayEnt(w, new Vector3(36, 36, 36), new Vector3(1, 0, 0), 1e16f,
                                     OctaRaycast.RaySkipFirst, 8, out int orient, out int ent, out Vector3 hit);
        TestAssert.True(Mathf.Abs(d - 4.1f) < 0.001f, $"dist: got {d}");
        TestAssert.Equal(-1, orient, "no entity orient");
        TestAssert.Equal(-1, ent, "no entity");
        TestAssert.True((hit - new Vector3(-1, 0, 0)).LengthSquared() < 0.00001f, $"hit: got {hit}");
    }
}
