using System;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game.Ctf;
using SauerbratenRevival.World.Spatial;

namespace SauerbratenRevival.Tests;

public class SpatialZoneTest : GoTestBase
{
    public SpatialZoneTest(Node testScene) : base(testScene) { }

    [Test]
    public void SphereZoneBoundsAndContainment()
    {
        Vector3 center = new(100f, 200f, 300f);
        float radius = 16f;
        var zone = SpatialZone.CreateSphere(center, radius, SpatialCategory.FlagZone, zoneId: 1, team: 1);

        zone.ComputeBounds(out Vector3 min, out Vector3 max, out float rejectRadius);
        TestAssert.Equal(new Vector3(84f, 184f, 284f), min, "Sphere min bounds");
        TestAssert.Equal(new Vector3(116f, 216f, 316f), max, "Sphere max bounds");
        TestAssert.Equal(16f, rejectRadius, "Sphere reject radius");

        // Strict inequality: dist < radius
        TestAssert.True(zone.ContainsPoint(center), "Center is inside");
        TestAssert.True(zone.ContainsPoint(center + new Vector3(15.9f, 0f, 0f)), "15.9u is inside");
        TestAssert.False(zone.ContainsPoint(center + new Vector3(16f, 0f, 0f)), "16.0u boundary is outside");
        TestAssert.False(zone.ContainsPoint(center + new Vector3(20f, 0f, 0f)), "20.0u is outside");
    }

    [Test]
    public void CylinderZoneMatchesFlagManagerInsideBaseParity()
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

        // Test various points against both FlagManager.InsideBase and SpatialZone.ContainsPoint
        Vector3[] testPoints =
        {
            baseLoc,
            new(500f, 500f, 512f + 24f),     // exact top cap
            new(500f, 500f, 512f - 24f),     // exact bottom cap
            new(500f, 500f, 512f + 24.1f),   // just above
            new(500f, 500f, 512f - 24.1f),   // just below
            new(564f, 500f, 512f),           // exact radius boundary (dx=64)
            new(564.1f, 500f, 512f),         // just outside radius
            new(540f, 530f, 512f + 20f),     // diagonal inside (dx=40, dy=30, dist=50 <= 64)
            new(550f, 550f, 512f),           // diagonal outside (50^2 + 50^2 = 5000 > 4096)
        };

        foreach (var pt in testPoints)
        {
            bool legacy = FlagManager.InsideBase(flagState, pt);
            bool spatial = baseZone.ContainsPoint(pt);
            TestAssert.Equal(legacy, spatial, $"Parity at point {pt}");
        }
    }

    [Test]
    public void AabbZoneBoundsAndContainment()
    {
        Vector3 min = new(10f, 20f, 30f);
        Vector3 max = new(30f, 40f, 50f);
        var zone = SpatialZone.CreateAabb(min, max, SpatialCategory.TriggerZone, zoneId: 42);

        zone.ComputeBounds(out Vector3 bMin, out Vector3 bMax, out _);
        TestAssert.Equal(min, bMin, "AABB min matches");
        TestAssert.Equal(max, bMax, "AABB max matches");

        TestAssert.True(zone.ContainsPoint(new Vector3(20f, 30f, 40f)), "Center is inside");
        TestAssert.True(zone.ContainsPoint(min), "Min corner is inside");
        TestAssert.True(zone.ContainsPoint(max), "Max corner is inside");
        TestAssert.False(zone.ContainsPoint(new Vector3(9.9f, 30f, 40f)), "Outside X min");
        TestAssert.False(zone.ContainsPoint(new Vector3(30.1f, 30f, 40f)), "Outside X max");
    }

    [Test]
    public void ObbZoneRotatesContainmentWithYaw()
    {
        // Box centered at (100, 100, 50) with half-extents (20, 10, 5), rotated 90 degrees around Z
        Vector3 origin = new(100f, 100f, 50f);
        Vector3 halfExtents = new(20f, 10f, 5f);
        var zone = SpatialZone.CreateObb(origin, Vector3.Zero, halfExtents, yawDegrees: 90f, SpatialCategory.TriggerZone);

        // At 90 degrees yaw:
        // Local X is along world Y (half-extent 20)
        // Local Y is along world -X (half-extent 10)
        TestAssert.True(zone.ContainsPoint(origin), "Center is inside");
        TestAssert.True(zone.ContainsPoint(origin + new Vector3(0f, 19f, 0f)), "World Y=19 corresponds to local X=19 (halfExtents.X=20) -> inside");
        TestAssert.False(zone.ContainsPoint(origin + new Vector3(0f, 21f, 0f)), "World Y=21 corresponds to local X=21 > 20 -> outside");
        TestAssert.True(zone.ContainsPoint(origin + new Vector3(9f, 0f, 0f)), "World X=9 corresponds to local Y=-9 <= 10 -> inside");
        TestAssert.False(zone.ContainsPoint(origin + new Vector3(11f, 0f, 0f)), "World X=11 corresponds to local Y=-11 > 10 -> outside");
    }

    [Test]
    public void TriggerModeOverlapsEntrySelection()
    {
        var zone = SpatialZone.CreateSphere(new Vector3(100f, 100f, 100f), radius: 10f, SpatialCategory.FlagZone);

        // Create an entry where:
        // - FeetPos is inside the zone (e.g. FeetPos = (100, 100, 105), dist = 5 < 10)
        // - Origin (eye pos) is outside the zone (e.g. Origin = (100, 100, 119), dist = 19 > 10)
        // - Body bounds [Min, Max] covers Z [105..120]
        var entry = new SpatialEntry
        {
            Origin = new Vector3(100f, 100f, 119f),
            Min = new Vector3(96f, 96f, 105f),
            Max = new Vector3(104f, 104f, 120f),
        };

        // 1. FeetPos trigger mode
        zone.TriggerMode = SpatialZoneTriggerMode.FeetPos;
        TestAssert.True(zone.OverlapsEntry(in entry), "FeetPos is inside sphere");

        // 2. Origin trigger mode
        zone.TriggerMode = SpatialZoneTriggerMode.Origin;
        TestAssert.False(zone.OverlapsEntry(in entry), "Origin (eye) is outside sphere");

        // 3. BodyBounds trigger mode
        zone.TriggerMode = SpatialZoneTriggerMode.BodyBounds;
        TestAssert.True(zone.OverlapsEntry(in entry), "Body bounds overlaps sphere");
    }

    [Test]
    public void ZoneOverlapEventsDispatchCorrectly()
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
}
