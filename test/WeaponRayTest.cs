using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class WeaponRayTest : GoTestBase
{
    public WeaponRayTest(Node testScene) : base(testScene) { }

    [Test]
    public void LineCylinderIntersectCenterHits()
    {
        // Upright cylinder at origin, radius 2.0, Z from 0 to 10
        Vector3 start = new(0f, 0f, 0f);
        Vector3 end = new(0f, 0f, 10f);
        float radius = 2.0f;

        // Ray from (-10, 0, 5) to (10, 0, 5) -> length 20, center at x=0
        // Cylinder radius is 2, so entry is at x = -2, which is normalized dist = 8 / 20 = 0.4
        Vector3 from = new(-10f, 0f, 5f);
        Vector3 to = new(10f, 0f, 5f);

        bool hit = WeaponRay.LineCylinderIntersect(from, to, start, end, radius, out float dist);
        TestAssert.True(hit, "center ray hits cylinder");
        TestAssert.Near(0.4f, dist, 0.0001f, "entry normalized distance");
    }

    [Test]
    public void LineCylinderIntersectMissReturnsFalse()
    {
        Vector3 start = new(0f, 0f, 0f);
        Vector3 end = new(0f, 0f, 10f);
        float radius = 2.0f;

        // Ray passing alongside at Y = 5.0 (outside radius 2.0)
        Vector3 from = new(-10f, 5f, 5f);
        Vector3 to = new(10f, 5f, 5f);

        bool hit = WeaponRay.LineCylinderIntersect(from, to, start, end, radius, out _);
        TestAssert.False(hit, "parallel ray outside radius misses");
    }

    [Test]
    public void LineCylinderIntersectTopCapHits()
    {
        Vector3 start = new(0f, 0f, 0f);
        Vector3 end = new(0f, 0f, 10f);
        float radius = 2.0f;

        // Ray coming straight down from (0, 0, 15) to (0, 0, 5)
        // Hits top cap at z = 10, total dz = -10, top cap is at 5/10 = 0.5
        Vector3 from = new(0f, 0f, 15f);
        Vector3 to = new(0f, 0f, 5f);

        bool hit = WeaponRay.LineCylinderIntersect(from, to, start, end, radius, out float dist);
        TestAssert.True(hit, "ray through top cap hits");
        TestAssert.Near(0.5f, dist, 0.0001f, "top cap normalized distance");
    }

    [Test]
    public void LineCylinderIntersectInsideStartsAtZero()
    {
        Vector3 start = new(0f, 0f, 0f);
        Vector3 end = new(0f, 0f, 10f);
        float radius = 2.0f;

        // Ray starting inside at (0, 0, 5)
        Vector3 from = new(0f, 0f, 5f);
        Vector3 to = new(10f, 0f, 5f);

        bool hit = WeaponRay.LineCylinderIntersect(from, to, start, end, radius, out float dist);
        TestAssert.True(hit, "ray starting inside cylinder hits");
        TestAssert.Near(0.0f, dist, 0.0001f, "start inside has dist 0");
    }

    [Test]
    public void IntersectEntityMatchesPhysEntBounds()
    {
        var ent = new PhysEnt
        {
            O = new Vector3(50f, 50f, 20f),
            Radius = 4.1f,
            EyeHeight = 14f,
            AboveEye = 1f
        };

        // Ray aimed straight through eye position O (50, 50, 20)
        Vector3 from = new(0f, 50f, 20f);
        Vector3 to = new(100f, 50f, 20f);

        bool hit = WeaponRay.IntersectEntity(ent, from, to, out float dist);
        TestAssert.True(hit, "ray hits entity");

        // Distance from 0 to 50 is 50. Radius is 4.1. Entry at x = 45.9.
        // Normalized dist along 100 length = 45.9 / 100 = 0.459
        TestAssert.Near(0.459f, dist, 0.001f, "normalized distance to cylinder surface");

        // Ray aimed above head (z = 20 + 1 + 2 = 23)
        Vector3 highFrom = new(0f, 50f, 23f);
        Vector3 highTo = new(100f, 50f, 23f);
        TestAssert.False(WeaponRay.IntersectEntity(ent, highFrom, highTo, out _), "ray above aboveeye misses");

        // Ray aimed below feet (z = 20 - 14 - 2 = 4)
        Vector3 lowFrom = new(0f, 50f, 4f);
        Vector3 lowTo = new(100f, 50f, 4f);
        TestAssert.False(WeaponRay.IntersectEntity(ent, lowFrom, lowTo, out _), "ray below eyeheight misses");
    }

    [Test]
    public void CalcRayOffsetMatchesGoldenValues()
    {
        // Reference values computed from C++ Sauerbraten MT19937 seed 12345
        // from (0, 0, 10), to (100, 0, 10), spread 400
        var rng = new WeaponRay.Mt19937(12345);
        Vector3 from = new(0f, 0f, 10f);
        Vector3 to = new(100f, 0f, 10f);
        int spread = 400;

        Vector3 r0 = WeaponRay.CalcRayOffset(from, to, spread, rng);
        TestAssert.Near(114.032509f, r0.X, 0.001f, "ray 0 X");
        TestAssert.Near(10.949588f, r0.Y, 0.001f, "ray 0 Y");
        TestAssert.Near(12.592795f, r0.Z, 0.001f, "ray 0 Z");

        Vector3 r1 = WeaponRay.CalcRayOffset(from, to, spread, rng);
        TestAssert.Near(96.450020f, r1.X, 0.001f, "ray 1 X");
        TestAssert.Near(5.971573f, r1.Y, 0.001f, "ray 1 Y");
        TestAssert.Near(2.879884f, r1.Z, 0.001f, "ray 1 Z");

        Vector3 r2 = WeaponRay.CalcRayOffset(from, to, spread, rng);
        TestAssert.Near(113.573296f, r2.X, 0.001f, "ray 2 X");
        TestAssert.Near(-0.085419f, r2.Y, 0.001f, "ray 2 Y");
        TestAssert.Near(14.840672f, r2.Z, 0.001f, "ray 2 Z");
    }
}
