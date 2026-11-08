using System;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game.Ctf;
using SauerbratenRevival.World.Spatial;

namespace SauerbratenRevival.Tests;

public class SpatialZoneTest : GoTestBase
{
    public SpatialZoneTest(Node testScene) : base(testScene) { }

    private const float Epsilon = 1e-3f;

    // =========================================================================
    // 1. SPHERE TESTS
    // =========================================================================

    [Test]
    public void Sphere_ComputeBounds_CoversRadiusAndOffset()
    {
        Vector3 origin = new(100f, 200f, 300f);
        Vector3 offset = new(10f, -20f, 5f);
        float radius = 16f;
        var zone = SpatialZone.CreateSphere(origin, radius, SpatialCategory.FlagZone, zoneId: 1, team: 1);
        zone.CenterOffset = offset;

        zone.ComputeBounds(out Vector3 min, out Vector3 max, out float rejectRadius);

        Vector3 expectedCenter = origin + offset;
        TestAssert.Equal(expectedCenter - new Vector3(radius, radius, radius), min, "Sphere min bounds with offset");
        TestAssert.Equal(expectedCenter + new Vector3(radius, radius, radius), max, "Sphere max bounds with offset");

        float expectedReject = new Vector2(offset.X, offset.Y).Length() + radius;
        TestAssert.True(MathF.Abs(expectedReject - rejectRadius) < Epsilon, "Sphere 2D reject radius matches offset XY + radius");
    }

    [Test]
    public void Sphere_ContainsPoint_StrictInequalityAnd3DDiagonals()
    {
        Vector3 center = new(50f, -50f, 100f);
        float r = 12f;
        var zone = SpatialZone.CreateSphere(center, r, SpatialCategory.ItemZone);

        // Center is strictly inside
        TestAssert.True(zone.ContainsPoint(center), "Center is inside sphere");

        // Axis-aligned points (+X, -X, +Y, -Y, +Z, -Z)
        Vector3[] directions =
        {
            new(1f, 0f, 0f), new(-1f, 0f, 0f),
            new(0f, 1f, 0f), new(0f, -1f, 0f),
            new(0f, 0f, 1f), new(0f, 0f, -1f),
        };

        foreach (var dir in directions)
        {
            TestAssert.True(zone.ContainsPoint(center + dir * (r - 0.1f)), $"Point at r-0.1 along {dir} is inside");
            // Strict inequality dist < r (matching Cube 2 / Sauerbraten checkitems: dist < ItemRadius)
            TestAssert.False(zone.ContainsPoint(center + dir * r), $"Exact boundary at r along {dir} is strictly outside (< R)");
            TestAssert.False(zone.ContainsPoint(center + dir * (r + 0.1f)), $"Point at r+0.1 along {dir} is outside");
        }

        // 3D diagonal points (dist = sqrt(3) * s)
        float sInside = (r - 0.1f) / MathF.Sqrt(3f);
        float sOutside = (r + 0.1f) / MathF.Sqrt(3f);
        TestAssert.True(zone.ContainsPoint(center + new Vector3(sInside, sInside, sInside)), "3D diagonal point inside");
        TestAssert.False(zone.ContainsPoint(center + new Vector3(sOutside, sOutside, sOutside)), "3D diagonal point outside");
    }

    [Test]
    public void Sphere_OverlapsBounds_FacesEdgesAndCorners()
    {
        Vector3 center = new(100f, 100f, 100f);
        float r = 10f;
        var zone = SpatialZone.CreateSphere(center, r, SpatialCategory.FlagZone);

        // 1. AABB contains sphere
        TestAssert.True(zone.OverlapsBounds(new Vector3(80f, 80f, 80f), new Vector3(120f, 120f, 120f)), "AABB contains sphere");

        // 2. Sphere contains AABB
        TestAssert.True(zone.OverlapsBounds(new Vector3(98f, 98f, 98f), new Vector3(102f, 102f, 102f)), "Sphere contains AABB");

        // 3. Face overlap along +X only (Y and Z overlap center)
        TestAssert.True(zone.OverlapsBounds(new Vector3(105f, 95f, 95f), new Vector3(115f, 105f, 105f)), "Face overlap +X (min.X=105 <= 110)");
        TestAssert.False(zone.OverlapsBounds(new Vector3(111f, 95f, 95f), new Vector3(120f, 105f, 105f)), "Separated +X (min.X=111 > 110)");

        // 4. Edge overlap: Y separated, X separated, Z clamped to center
        // Closest corner of AABB is at (106, 106, 100). Distance = sqrt(6^2 + 6^2) = sqrt(72) = 8.485 < 10 -> inside!
        TestAssert.True(zone.OverlapsBounds(new Vector3(106f, 106f, 95f), new Vector3(120f, 120f, 105f)), "Edge overlap (dist = 8.49 < 10)");
        // Closest corner at (108, 108, 100). Distance = sqrt(8^2 + 8^2) = sqrt(128) = 11.31 > 10 -> outside!
        TestAssert.False(zone.OverlapsBounds(new Vector3(108f, 108f, 95f), new Vector3(120f, 120f, 105f)), "Edge separated (dist = 11.31 > 10)");

        // 5. Corner overlap: closest point is corner at (105, 105, 105). Distance = sqrt(25 + 25 + 25) = 8.66 < 10 -> inside!
        TestAssert.True(zone.OverlapsBounds(new Vector3(105f, 105f, 105f), new Vector3(120f, 120f, 120f)), "Corner overlap (dist = 8.66 < 10)");
        // Closest corner at (106, 106, 106). Distance = sqrt(36 + 36 + 36) = 10.39 > 10 -> outside!
        TestAssert.False(zone.OverlapsBounds(new Vector3(106f, 106f, 106f), new Vector3(120f, 120f, 120f)), "Corner separated (dist = 10.39 > 10)");
    }

    // =========================================================================
    // 2. CYLINDER TESTS
    // =========================================================================

    [Test]
    public void Cylinder_ComputeBounds_CoversRadiusHeightAndOffset()
    {
        Vector3 origin = new(200f, 300f, 400f);
        Vector3 offset = new(-15f, 25f, -10f);
        float r = 64f;
        float hz = 24f;
        var zone = SpatialZone.CreateCylinder(origin, r, hz, SpatialCategory.BaseZone);
        zone.CenterOffset = offset;

        zone.ComputeBounds(out Vector3 min, out Vector3 max, out float rejectRadius);

        Vector3 expectedCenter = origin + offset;
        TestAssert.Equal(new Vector3(expectedCenter.X - r, expectedCenter.Y - r, expectedCenter.Z - hz), min, "Cylinder min bounds");
        TestAssert.Equal(new Vector3(expectedCenter.X + r, expectedCenter.Y + r, expectedCenter.Z + hz), max, "Cylinder max bounds");

        float expectedReject = new Vector2(offset.X, offset.Y).Length() + r;
        TestAssert.True(MathF.Abs(expectedReject - rejectRadius) < Epsilon, "Cylinder reject radius is 2D XY length + radius");
    }

    [Test]
    public void Cylinder_ContainsPoint_ParityWithFlagManagerInsideBase()
    {
        Vector3 baseLoc = new(500f, 500f, 512f);
        var baseZone = SpatialZone.CreateCylinder(
            baseLoc,
            FlagManager.BaseRadius,
            FlagManager.BaseHeight,
            SpatialCategory.BaseZone,
            zoneId: 1,
            team: 1);

        var flagState = new FlagState { Id = 1, Team = 1, SpawnLoc = baseLoc };

        // Test grid of points around base covering exact boundaries, caps, and outside
        Vector3[] testPoints =
        {
            baseLoc,
            new(500f, 500f, 512f + 24f),     // exact top cap
            new(500f, 500f, 512f - 24f),     // exact bottom cap
            new(500f, 500f, 512f + 24.01f),  // just above top cap
            new(500f, 500f, 512f - 24.01f),  // just below bottom cap
            new(564f, 500f, 512f),           // exact radius boundary (dx=64)
            new(564.01f, 500f, 512f),        // just outside radius
            new(540f, 530f, 512f + 20f),     // diagonal inside (dx=40, dy=30, dist=50 <= 64)
            new(550f, 550f, 512f),           // diagonal outside (50^2 + 50^2 = 5000 > 4096)
            new(500f + 64f / MathF.Sqrt(2f), 500f + 64f / MathF.Sqrt(2f), 512f + 24f), // exact corner boundary
            new(500f + 64f / MathF.Sqrt(2f) + 0.1f, 500f + 64f / MathF.Sqrt(2f) + 0.1f, 512f), // diagonal outside
        };

        foreach (var pt in testPoints)
        {
            bool legacy = FlagManager.InsideBase(flagState, pt);
            bool spatial = baseZone.ContainsPoint(pt);
            TestAssert.Equal(legacy, spatial, $"Parity at point {pt}");
        }
    }

    [Test]
    public void Cylinder_ContainsPoint_CapsEdgesAndCorners()
    {
        Vector3 center = new(100f, 100f, 50f);
        float r = 20f;
        float hz = 10f;
        var zone = SpatialZone.CreateCylinder(center, r, hz, SpatialCategory.BaseZone);

        // Axial tests (inclusive Z bounds: |dz| <= hz)
        TestAssert.True(zone.ContainsPoint(center + new Vector3(0f, 0f, hz)), "Top cap (dz = hz) is inclusive");
        TestAssert.True(zone.ContainsPoint(center - new Vector3(0f, 0f, hz)), "Bottom cap (dz = -hz) is inclusive");
        TestAssert.False(zone.ContainsPoint(center + new Vector3(0f, 0f, hz + 0.01f)), "Above top cap is outside");
        TestAssert.False(zone.ContainsPoint(center - new Vector3(0f, 0f, hz + 0.01f)), "Below bottom cap is outside");

        // Radial tests (inclusive XY bounds: dx^2 + dy^2 <= r^2)
        TestAssert.True(zone.ContainsPoint(center + new Vector3(r, 0f, 0f)), "Radial boundary (+X) is inclusive");
        TestAssert.True(zone.ContainsPoint(center + new Vector3(-r, 0f, 0f)), "Radial boundary (-X) is inclusive");
        TestAssert.True(zone.ContainsPoint(center + new Vector3(0f, r, 0f)), "Radial boundary (+Y) is inclusive");
        TestAssert.True(zone.ContainsPoint(center + new Vector3(0f, -r, 0f)), "Radial boundary (-Y) is inclusive");
        TestAssert.False(zone.ContainsPoint(center + new Vector3(r + 0.05f, 0f, 0f)), "Outside radial boundary (+X)");

        // Corner tests (at r and hz simultaneously)
        TestAssert.True(zone.ContainsPoint(center + new Vector3(r, 0f, hz)), "Corner at (+r, 0, +hz) is inside");
        TestAssert.True(zone.ContainsPoint(center + new Vector3(-r, 0f, -hz)), "Corner at (-r, 0, -hz) is inside");
        TestAssert.False(zone.ContainsPoint(center + new Vector3(r + 0.05f, 0f, hz)), "Outside corner (+r+0.05, 0, +hz)");
        TestAssert.False(zone.ContainsPoint(center + new Vector3(r, 0f, hz + 0.05f)), "Outside corner (+r, 0, +hz+0.05)");
    }

    [Test]
    public void Cylinder_OverlapsBounds_ZSeparationAndRadialClamping()
    {
        Vector3 center = new(100f, 100f, 50f);
        float r = 20f;
        float hz = 10f; // Z is [40..60]
        var zone = SpatialZone.CreateCylinder(center, r, hz, SpatialCategory.BaseZone);

        // 1. Z completely separated (above or below)
        TestAssert.False(zone.OverlapsBounds(new Vector3(90f, 90f, 61f), new Vector3(110f, 110f, 70f)), "AABB above cylinder Z");
        TestAssert.False(zone.OverlapsBounds(new Vector3(90f, 90f, 30f), new Vector3(110f, 110f, 39f)), "AABB below cylinder Z");

        // 2. Z overlapping, but XY completely outside
        TestAssert.False(zone.OverlapsBounds(new Vector3(125f, 90f, 45f), new Vector3(140f, 110f, 55f)), "AABB separated in +X (min.X=125 > 120)");

        // 3. Z overlapping and XY overlapping side
        TestAssert.True(zone.OverlapsBounds(new Vector3(115f, 90f, 45f), new Vector3(130f, 110f, 55f)), "AABB overlapping side (min.X=115 <= 120)");

        // 4. Z overlapping top cap (e.g. Z is [55..65])
        TestAssert.True(zone.OverlapsBounds(new Vector3(95f, 95f, 55f), new Vector3(105f, 105f, 65f)), "AABB overlapping top cap");

        // 5. Diagonal corner clamp in XY
        // Closest corner in XY is at (115, 115). Distance from (100, 100) is sqrt(15^2 + 15^2) = sqrt(450) = 21.21 > 20 -> outside!
        TestAssert.False(zone.OverlapsBounds(new Vector3(115f, 115f, 45f), new Vector3(130f, 130f, 55f)), "XY diagonal outside (dist=21.21 > 20)");
        // Closest corner in XY at (114, 114). Distance is sqrt(14^2 + 14^2) = sqrt(392) = 19.80 <= 20 -> inside!
        TestAssert.True(zone.OverlapsBounds(new Vector3(114f, 114f, 45f), new Vector3(130f, 130f, 55f)), "XY diagonal inside (dist=19.80 <= 20)");
    }

    // =========================================================================
    // 3. AABB TESTS
    // =========================================================================

    [Test]
    public void Aabb_ComputeBounds_ExactAndOffset()
    {
        Vector3 min = new(10f, 20f, 30f);
        Vector3 max = new(50f, 60f, 70f);
        var zone = SpatialZone.CreateAabb(min, max, SpatialCategory.TriggerZone, zoneId: 7);

        zone.ComputeBounds(out Vector3 bMin, out Vector3 bMax, out float rejectRadius);
        TestAssert.Equal(min, bMin, "AABB min bounds");
        TestAssert.Equal(max, bMax, "AABB max bounds");

        Vector3 extents = (max - min) * 0.5f;
        float expectedReject = new Vector2(extents.X, extents.Y).Length();
        TestAssert.True(MathF.Abs(expectedReject - rejectRadius) < Epsilon, "AABB reject radius is 2D half-extents length");

        // With CenterOffset
        zone.CenterOffset = new Vector3(5f, -5f, 10f);
        zone.ComputeBounds(out bMin, out bMax, out rejectRadius);
        TestAssert.Equal(min + zone.CenterOffset, bMin, "AABB offset min");
        TestAssert.Equal(max + zone.CenterOffset, bMax, "AABB offset max");
    }

    [Test]
    public void Aabb_ContainsPoint_FacesEdgesAndCorners()
    {
        Vector3 center = new(100f, 100f, 100f);
        Vector3 extents = new(10f, 20f, 30f);
        var zone = new SpatialZone
        {
            Origin = center,
            Extents = extents,
            Shape = SpatialShapeKind.Aabb,
        };

        // Center is inside
        TestAssert.True(zone.ContainsPoint(center), "Center inside AABB");

        // All 6 faces (inclusive boundaries)
        TestAssert.True(zone.ContainsPoint(center + new Vector3(extents.X, 0f, 0f)), "Face +X inside");
        TestAssert.True(zone.ContainsPoint(center - new Vector3(extents.X, 0f, 0f)), "Face -X inside");
        TestAssert.True(zone.ContainsPoint(center + new Vector3(0f, extents.Y, 0f)), "Face +Y inside");
        TestAssert.True(zone.ContainsPoint(center - new Vector3(0f, extents.Y, 0f)), "Face -Y inside");
        TestAssert.True(zone.ContainsPoint(center + new Vector3(0f, 0f, extents.Z)), "Face +Z inside");
        TestAssert.True(zone.ContainsPoint(center - new Vector3(0f, 0f, extents.Z)), "Face -Z inside");

        // Points just outside faces
        TestAssert.False(zone.ContainsPoint(center + new Vector3(extents.X + 0.1f, 0f, 0f)), "Outside face +X");
        TestAssert.False(zone.ContainsPoint(center - new Vector3(extents.X + 0.1f, 0f, 0f)), "Outside face -X");
        TestAssert.False(zone.ContainsPoint(center + new Vector3(0f, extents.Y + 0.1f, 0f)), "Outside face +Y");
        TestAssert.False(zone.ContainsPoint(center - new Vector3(0f, extents.Y + 0.1f, 0f)), "Outside face -Y");
        TestAssert.False(zone.ContainsPoint(center + new Vector3(0f, 0f, extents.Z + 0.1f)), "Outside face +Z");
        TestAssert.False(zone.ContainsPoint(center - new Vector3(0f, 0f, extents.Z + 0.1f)), "Outside face -Z");

        // All 8 corners are inclusive
        for (int sx = -1; sx <= 1; sx += 2)
        {
            for (int sy = -1; sy <= 1; sy += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 corner = center + new Vector3(sx * extents.X, sy * extents.Y, sz * extents.Z);
                    TestAssert.True(zone.ContainsPoint(corner), $"Corner ({sx},{sy},{sz}) is inside");

                    Vector3 outsideCorner = center + new Vector3(sx * (extents.X + 0.1f), sy * extents.Y, sz * extents.Z);
                    TestAssert.False(zone.ContainsPoint(outsideCorner), $"Outside corner ({sx},{sy},{sz})");
                }
            }
        }
    }

    [Test]
    public void Aabb_OverlapsBounds_SeparatingAxesAndContainment()
    {
        Vector3 min = new(10f, 10f, 10f);
        Vector3 max = new(30f, 30f, 30f);
        var zone = SpatialZone.CreateAabb(min, max, SpatialCategory.TriggerZone);

        // Fully contained
        TestAssert.True(zone.OverlapsBounds(new Vector3(15f, 15f, 15f), new Vector3(25f, 25f, 25f)), "Inside bounds overlaps");

        // Containing zone
        TestAssert.True(zone.OverlapsBounds(new Vector3(0f, 0f, 0f), new Vector3(40f, 40f, 40f)), "Enclosing bounds overlaps");

        // Touching faces (inclusive)
        TestAssert.True(zone.OverlapsBounds(new Vector3(30f, 10f, 10f), new Vector3(40f, 30f, 30f)), "Touching +X face overlaps");
        TestAssert.True(zone.OverlapsBounds(new Vector3(0f, 10f, 10f), new Vector3(10f, 30f, 30f)), "Touching -X face overlaps");

        // Separating axes (separated on X, Y, or Z only)
        TestAssert.False(zone.OverlapsBounds(new Vector3(30.1f, 10f, 10f), new Vector3(40f, 30f, 30f)), "Separated on +X");
        TestAssert.False(zone.OverlapsBounds(new Vector3(0f, 10f, 10f), new Vector3(9.9f, 30f, 30f)), "Separated on -X");
        TestAssert.False(zone.OverlapsBounds(new Vector3(10f, 30.1f, 10f), new Vector3(30f, 40f, 30f)), "Separated on +Y");
        TestAssert.False(zone.OverlapsBounds(new Vector3(10f, 0f, 10f), new Vector3(30f, 9.9f, 30f)), "Separated on -Y");
        TestAssert.False(zone.OverlapsBounds(new Vector3(10f, 10f, 30.1f), new Vector3(30f, 30f, 40f)), "Separated on +Z");
        TestAssert.False(zone.OverlapsBounds(new Vector3(10f, 10f, 0f), new Vector3(30f, 30f, 9.9f)), "Separated on -Z");
    }

    // =========================================================================
    // 4. OBB TESTS (ORIENTED BOUNDING BOX)
    // =========================================================================

    [Test]
    public void Obb_ComputeBounds_RotatedCornersEnclosedTightly()
    {
        Vector3 origin = new(100f, 200f, 50f);
        Vector3 offset = new(10f, -5f, 2f);
        Vector3 extents = new(20f, 10f, 8f);

        // Test across many angles (0, 30, 45, 60, 90, 135, 180, 270, -45, 360)
        float[] testAngles = { 0f, 30f, 45f, 60f, 90f, 135f, 180f, 270f, -45f, 360f };

        foreach (float yaw in testAngles)
        {
            var zone = SpatialZone.CreateObb(origin, offset, extents, yaw, SpatialCategory.TriggerZone);
            zone.ComputeBounds(out Vector3 min, out Vector3 max, out float rejectRadius);

            float rad = yaw * (MathF.PI / 180f);
            Vector3 rotCenter = origin + RotateAroundZ(offset, rad);

            float observedMinX = float.MaxValue, observedMaxX = float.MinValue;
            float observedMinY = float.MaxValue, observedMaxY = float.MinValue;
            float observedMinZ = float.MaxValue, observedMaxZ = float.MinValue;

            // Generate all 8 rotated corners of the OBB in world coordinates
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 localCorner = new(sx * extents.X, sy * extents.Y, sz * extents.Z);
                        Vector3 worldCorner = rotCenter + RotateAroundZ(localCorner, rad);

                        observedMinX = MathF.Min(observedMinX, worldCorner.X);
                        observedMaxX = MathF.Max(observedMaxX, worldCorner.X);
                        observedMinY = MathF.Min(observedMinY, worldCorner.Y);
                        observedMaxY = MathF.Max(observedMaxY, worldCorner.Y);
                        observedMinZ = MathF.Min(observedMinZ, worldCorner.Z);
                        observedMaxZ = MathF.Max(observedMaxZ, worldCorner.Z);

                        // Every corner MUST lie inside [min, max]
                        TestAssert.True(worldCorner.X >= min.X - Epsilon && worldCorner.X <= max.X + Epsilon,
                            $"Corner X ({worldCorner.X}) inside bounds [{min.X}, {max.X}] at yaw={yaw}");
                        TestAssert.True(worldCorner.Y >= min.Y - Epsilon && worldCorner.Y <= max.Y + Epsilon,
                            $"Corner Y ({worldCorner.Y}) inside bounds [{min.Y}, {max.Y}] at yaw={yaw}");
                        TestAssert.True(worldCorner.Z >= min.Z - Epsilon && worldCorner.Z <= max.Z + Epsilon,
                            $"Corner Z ({worldCorner.Z}) inside bounds [{min.Z}, {max.Z}] at yaw={yaw}");

                        // Reject radius MUST cover every corner in XY relative to Origin
                        float distXY = new Vector2(worldCorner.X - origin.X, worldCorner.Y - origin.Y).Length();
                        TestAssert.True(distXY <= rejectRadius + Epsilon,
                            $"RejectRadius ({rejectRadius}) covers corner XY ({distXY}) at yaw={yaw}");
                    }
                }
            }

            // Assert that the computed bounds are tight to within numerical tolerance
            TestAssert.True(MathF.Abs(min.X - observedMinX) < Epsilon, $"MinX tight at yaw={yaw}");
            TestAssert.True(MathF.Abs(max.X - observedMaxX) < Epsilon, $"MaxX tight at yaw={yaw}");
            TestAssert.True(MathF.Abs(min.Y - observedMinY) < Epsilon, $"MinY tight at yaw={yaw}");
            TestAssert.True(MathF.Abs(max.Y - observedMaxY) < Epsilon, $"MaxY tight at yaw={yaw}");
            TestAssert.True(MathF.Abs(min.Z - observedMinZ) < Epsilon, $"MinZ tight at yaw={yaw}");
            TestAssert.True(MathF.Abs(max.Z - observedMaxZ) < Epsilon, $"MaxZ tight at yaw={yaw}");
        }
    }

    [Test]
    public void Obb_ContainsPoint_RotatedLocalFrameTransform()
    {
        Vector3 origin = new(200f, 200f, 100f);
        Vector3 offset = new(10f, 5f, 0f);
        Vector3 extents = new(30f, 15f, 10f);
        float yaw = 45f;

        var zone = SpatialZone.CreateObb(origin, offset, extents, yaw, SpatialCategory.TriggerZone);

        float rad = yaw * (MathF.PI / 180f);

        // Test points generated directly from local unrotated coordinates
        // World point = Origin + RotateAroundZ(offset + localPt, rad)
        Vector3 LocalToWorld(Vector3 local) => origin + RotateAroundZ(offset + local, rad);

        // 1. Center of box
        TestAssert.True(zone.ContainsPoint(LocalToWorld(Vector3.Zero)), "Local center is inside OBB");

        // 2. Interior points along each axis
        TestAssert.True(zone.ContainsPoint(LocalToWorld(new Vector3(25f, 0f, 0f))), "Local X=25 (< 30) is inside");
        TestAssert.True(zone.ContainsPoint(LocalToWorld(new Vector3(-25f, 0f, 0f))), "Local X=-25 is inside");
        TestAssert.True(zone.ContainsPoint(LocalToWorld(new Vector3(0f, 12f, 0f))), "Local Y=12 (< 15) is inside");
        TestAssert.True(zone.ContainsPoint(LocalToWorld(new Vector3(0f, 0f, 8f))), "Local Z=8 (< 10) is inside");

        // 3. Exact boundary points (inclusive <=)
        TestAssert.True(zone.ContainsPoint(LocalToWorld(new Vector3(30f, 0f, 0f))), "Local X=30 boundary is inside");
        TestAssert.True(zone.ContainsPoint(LocalToWorld(new Vector3(0f, 15f, 0f))), "Local Y=15 boundary is inside");
        TestAssert.True(zone.ContainsPoint(LocalToWorld(new Vector3(0f, 0f, 10f))), "Local Z=10 boundary is inside");
        TestAssert.True(zone.ContainsPoint(LocalToWorld(new Vector3(30f, 15f, 10f))), "Corner (30,15,10) is inside");

        // 4. Points outside boundaries
        TestAssert.False(zone.ContainsPoint(LocalToWorld(new Vector3(30.2f, 0f, 0f))), "Local X=30.2 is outside");
        TestAssert.False(zone.ContainsPoint(LocalToWorld(new Vector3(-30.2f, 0f, 0f))), "Local X=-30.2 is outside");
        TestAssert.False(zone.ContainsPoint(LocalToWorld(new Vector3(0f, 15.2f, 0f))), "Local Y=15.2 is outside");
        TestAssert.False(zone.ContainsPoint(LocalToWorld(new Vector3(0f, 0f, 10.2f))), "Local Z=10.2 is outside");
    }

    [Test]
    public void Obb_OverlapsBounds_RotatedBoundingBox()
    {
        Vector3 origin = new(100f, 100f, 50f);
        Vector3 extents = new(20f, 10f, 5f);
        var zone = SpatialZone.CreateObb(origin, Vector3.Zero, extents, yawDegrees: 90f, SpatialCategory.TriggerZone);

        // At 90 degrees yaw:
        // Local X is along world Y (covers [80..120])
        // Local Y is along world -X (covers [90..110])
        // Local Z is [45..55]

        // 1. Fully containing AABB
        TestAssert.True(zone.OverlapsBounds(new Vector3(80f, 70f, 40f), new Vector3(120f, 130f, 60f)), "Enclosing AABB overlaps OBB");

        // 2. AABB overlapping world Y edge [115..125]
        TestAssert.True(zone.OverlapsBounds(new Vector3(95f, 115f, 48f), new Vector3(105f, 125f, 52f)), "Overlapping +Y world edge");

        // 3. AABB separated in world Y [121..130]
        TestAssert.False(zone.OverlapsBounds(new Vector3(95f, 121f, 48f), new Vector3(105f, 130f, 52f)), "Separated in +Y world edge");

        // 4. AABB separated in world X [111..120]
        TestAssert.False(zone.OverlapsBounds(new Vector3(111f, 95f, 48f), new Vector3(120f, 105f, 52f)), "Separated in +X world edge");
    }

    // =========================================================================
    // 5. TRIGGER MODE & SPATIAL ENTRY TESTS
    // =========================================================================

    [Test]
    public void TriggerMode_FeetPos_Origin_BodyBounds_ExhaustiveMatrix()
    {
        // Zone centered at Z=100 with radius 10 (covers Z [90..110])
        var zone = SpatialZone.CreateSphere(new Vector3(100f, 100f, 100f), radius: 10f, SpatialCategory.FlagZone);

        // Scenario 1: Entity standing on floor at Z=95.
        // FeetPos is (100, 100, 95) -> dist = 5 < 10 -> inside!
        // Origin (eye) is at Z=109 -> dist = 9 < 10 -> inside!
        // BodyBounds covers Z [95..110] -> inside!
        var entNormal = new SpatialEntry
        {
            Origin = new Vector3(100f, 100f, 109f),
            Min = new Vector3(96f, 96f, 95f),
            Max = new Vector3(104f, 104f, 110f),
        };
        zone.TriggerMode = SpatialZoneTriggerMode.FeetPos;
        TestAssert.True(zone.OverlapsEntry(in entNormal), "entNormal: FeetPos inside");
        zone.TriggerMode = SpatialZoneTriggerMode.Origin;
        TestAssert.True(zone.OverlapsEntry(in entNormal), "entNormal: Origin inside");
        zone.TriggerMode = SpatialZoneTriggerMode.BodyBounds;
        TestAssert.True(zone.OverlapsEntry(in entNormal), "entNormal: BodyBounds inside");
        zone.TriggerMode = (SpatialZoneTriggerMode)99;
        TestAssert.True(zone.OverlapsEntry(in entNormal), "Fallback trigger mode uses FeetPos");

        // Scenario 2: Entity standing above zone.
        // FeetPos is at Z=105 -> dist = 5 < 10 -> inside!
        // Origin (eye) is at Z=119 -> dist = 19 > 10 -> OUTSIDE!
        // BodyBounds covers Z [105..120] -> overlaps [90..110] -> inside!
        var entHighEye = new SpatialEntry
        {
            Origin = new Vector3(100f, 100f, 119f),
            Min = new Vector3(96f, 96f, 105f),
            Max = new Vector3(104f, 104f, 120f),
        };
        zone.TriggerMode = SpatialZoneTriggerMode.FeetPos;
        TestAssert.True(zone.OverlapsEntry(in entHighEye), "entHighEye: FeetPos inside");
        zone.TriggerMode = SpatialZoneTriggerMode.Origin;
        TestAssert.False(zone.OverlapsEntry(in entHighEye), "entHighEye: Origin outside");
        zone.TriggerMode = SpatialZoneTriggerMode.BodyBounds;
        TestAssert.True(zone.OverlapsEntry(in entHighEye), "entHighEye: BodyBounds inside");

        // Scenario 3: Entity alongside the zone in XY.
        // Zone is at (100, 100, 100) with radius 10 (XY covers [90..110]).
        // Entity origin is at (112, 100, 100):
        // FeetPos is (112, 100, 95) -> dist from center = sqrt(12^2 + 5^2) = 13 > 10 -> OUTSIDE!
        // Origin is at (112, 100, 100) -> dist = 12 > 10 -> OUTSIDE!
        // BodyBounds: X is [108..116] (Min.X=108 < 110 penetrates sphere in XY!), Y is [96..104], Z is [95..105].
        // Closest point on AABB to (100, 100, 100) is (108, 100, 100), dist = 8 < 10 -> BODY BOUNDS OVERLAPS!
        var entSidePenetration = new SpatialEntry
        {
            Origin = new Vector3(112f, 100f, 100f),
            Min = new Vector3(108f, 96f, 95f),
            Max = new Vector3(116f, 104f, 105f),
        };
        zone.TriggerMode = SpatialZoneTriggerMode.FeetPos;
        TestAssert.False(zone.OverlapsEntry(in entSidePenetration), "entSidePenetration: FeetPos outside");
        zone.TriggerMode = SpatialZoneTriggerMode.Origin;
        TestAssert.False(zone.OverlapsEntry(in entSidePenetration), "entSidePenetration: Origin outside");
        zone.TriggerMode = SpatialZoneTriggerMode.BodyBounds;
        TestAssert.True(zone.OverlapsEntry(in entSidePenetration), "entSidePenetration: BodyBounds inside");

        // Scenario 4: Entity completely below zone.
        // FeetPos at Z=70, Origin at Z=84, Max.Z at Z=85 < 90 -> ALL OUTSIDE!
        var entBelow = new SpatialEntry
        {
            Origin = new Vector3(100f, 100f, 84f),
            Min = new Vector3(96f, 96f, 70f),
            Max = new Vector3(104f, 104f, 85f),
        };
        zone.TriggerMode = SpatialZoneTriggerMode.FeetPos;
        TestAssert.False(zone.OverlapsEntry(in entBelow), "entBelow: FeetPos outside");
        zone.TriggerMode = SpatialZoneTriggerMode.Origin;
        TestAssert.False(zone.OverlapsEntry(in entBelow), "entBelow: Origin outside");
        zone.TriggerMode = SpatialZoneTriggerMode.BodyBounds;
        TestAssert.False(zone.OverlapsEntry(in entBelow), "entBelow: BodyBounds outside");
    }

    // =========================================================================
    // 6. DEGENERATE & EDGE CASE TESTS
    // =========================================================================

    [Test]
    public void DegenerateAndEdgeCases_ZeroExtentsNegativeExtentsLargeCoordinates()
    {
        // 1. Zero radius sphere
        var zeroSphere = SpatialZone.CreateSphere(new Vector3(100f, 100f, 100f), radius: 0f, SpatialCategory.TriggerZone);
        zeroSphere.ComputeBounds(out Vector3 sMin, out Vector3 sMax, out float sReject);
        TestAssert.Equal(new Vector3(100f, 100f, 100f), sMin, "Zero sphere min is center");
        TestAssert.Equal(new Vector3(100f, 100f, 100f), sMax, "Zero sphere max is center");
        TestAssert.Equal(0f, sReject, "Zero sphere reject is 0");
        TestAssert.False(zeroSphere.ContainsPoint(new Vector3(100f, 100f, 100f)), "Zero sphere strictly contains no points (< 0)");

        // 2. Negative extents AABB is normalized via Extents.Abs()
        var aabbNeg = new SpatialZone
        {
            Origin = new Vector3(50f, 50f, 50f),
            Extents = new Vector3(-10f, -20f, -30f),
            Shape = SpatialShapeKind.Aabb,
        };
        aabbNeg.ComputeBounds(out Vector3 aMin, out Vector3 aMax, out _);
        TestAssert.Equal(new Vector3(40f, 30f, 20f), aMin, "Negative AABB normalized min");
        TestAssert.Equal(new Vector3(60f, 70f, 80f), aMax, "Negative AABB normalized max");
        TestAssert.True(aabbNeg.ContainsPoint(new Vector3(50f, 50f, 50f)), "Negative AABB center inside");

        // 3. Large world coordinates
        Vector3 largePos = new(100000f, -200000f, 500000f);
        var largeZone = SpatialZone.CreateCylinder(largePos, 50f, 25f, SpatialCategory.BaseZone);
        TestAssert.True(largeZone.ContainsPoint(largePos), "Large coords center inside");
        TestAssert.True(largeZone.ContainsPoint(largePos + new Vector3(50f, 0f, 25f)), "Large coords boundary inside");
        TestAssert.False(largeZone.ContainsPoint(largePos + new Vector3(51f, 0f, 0f)), "Large coords outside");
    }

    // =========================================================================
    // 7. EVENT DISPATCH TESTS
    // =========================================================================

    [Test]
    public void EventDispatch_EnteredStayedExited()
    {
        var zone = SpatialZone.CreateSphere(Vector3.Zero, 10f, SpatialCategory.FlagZone);
        int enteredCount = 0;
        int stayedCount = 0;
        int exitedCount = 0;

        zone.Entered += _ => enteredCount++;
        zone.Stayed += _ => stayedCount++;
        zone.Exited += _ => exitedCount++;

        zone.RaiseOverlapEvent(new ZoneOverlapEvent { Kind = ZoneOverlapKind.Entered, Zone = zone });
        TestAssert.Equal(1, enteredCount, "Entered event fired");

        zone.RaiseOverlapEvent(new ZoneOverlapEvent { Kind = ZoneOverlapKind.Stayed, Zone = zone });
        TestAssert.Equal(1, stayedCount, "Stayed event fired");

        zone.RaiseOverlapEvent(new ZoneOverlapEvent { Kind = ZoneOverlapKind.Exited, Zone = zone });
        TestAssert.Equal(1, exitedCount, "Exited event fired");
    }

    private static Vector3 RotateAroundZ(Vector3 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector3(c * v.X - s * v.Y, s * v.X + c * v.Y, v.Z);
    }
}
