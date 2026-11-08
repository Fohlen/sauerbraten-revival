using System;
using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Models;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Spatial;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Unit tests for <see cref="WorldSpatialIndex"/> (ADR 0012 MR 2 Foundation):
/// <list type="bullet">
///   <item><description>Static registration, dynamic removal, and activation toggling</description></item>
///   <item><description>Generational handle validation preventing stale reference reuse</description></item>
///   <item><description>O(1) in-place dynamic entity updates vs cell-boundary crossings</description></item>
///   <item><description>Multi-cell traversal deduplication across grid boundaries</description></item>
///   <item><description>Explicit query overflow handling and truncation status</description></item>
///   <item><description>Map model collision parity (<c>OctaPhysics.MmCollide</c>)</description></item>
/// </list>
/// </summary>
public class WorldSpatialIndexTest : GoTestBase
{
    public WorldSpatialIndexTest(Node testScene) : base(testScene) { }

    private static PhysEnt NewPlayer(Vector3 eyePos) => new()
    {
        O = eyePos,
        NewPos = eyePos,
        Vel = Vector3.Zero,
        Type = Phys.EntPlayer,
        State = PhysEnt.CsAlive,
        PhysState = Phys.PhysFloor,
        Radius = 4.1f,
        XRadius = 4.1f,
        YRadius = 4.1f,
        EyeHeight = 14.0f,
        AboveEye = 1.0f,
        Yaw = 0f,
    };

    [Test]
    public void MapModelCollisionSupersededBySpatialIndexSupportsDynamicRemovalAndToggle()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        var crateInfo = new ModelInfo
        {
            Name = "objects/crate",
            Collide = true,
            EllipseCollide = false,
            CollideCenter = new Vector3(0f, 0f, 8f),
            CollideRadius = new Vector3(8f, 8f, 8f),
            RejectRadius = 16f,
        };

        Vector3 cratePos = new(256f, 256f, 512f);
        SpatialHandle handle = phys.AddMapModel(cratePos, 0f, crateInfo);
        TestAssert.True(handle.IsValid, "mapmodel registered in WorldSpatialIndex");
        TestAssert.Equal(1, phys.Spatial.StaticCount, "1 static entry in spatial index");

        // Player moving into crate from +X
        var player = NewPlayer(new Vector3(256f + 8f + 4.0f, 256f, 526f));
        TestAssert.True(phys.Collide(player, new Vector3(-1f, 0f, 0f)), "crate blocks player when active");

        // Disable collision in O(1) via SetActive without removing from grid
        phys.Spatial.SetActive(handle, false);
        TestAssert.False(phys.Collide(player, new Vector3(-1f, 0f, 0f)), "disabled mapmodel allows passing through");

        // Re-enable collision
        phys.Spatial.SetActive(handle, true);
        TestAssert.True(phys.Collide(player, new Vector3(-1f, 0f, 0f)), "re-enabled mapmodel blocks player again");

        // Remove mapmodel completely (e.g. coop-edit RemoveEntity command)
        bool removed = phys.RemoveMapModel(phys.MapModels[0]);
        TestAssert.True(removed, "RemoveMapModel succeeded");
        TestAssert.Equal(0, phys.Spatial.StaticCount, "static count is 0 after removal");
        TestAssert.False(phys.Collide(player, new Vector3(-1f, 0f, 0f)), "removed mapmodel no longer collides");
    }

    [Test]
    public void GenerationalHandlePreventsStaleHandleReuse()
    {
        var index = new WorldSpatialIndex(worldSize: 1024);

        // Register first entry at slot 0
        var h1 = index.RegisterStatic(
            SpatialCategory.MapModel,
            SpatialShapeKind.Obb,
            origin: new Vector3(100f, 100f, 100f),
            min: new Vector3(90f, 90f, 90f),
            max: new Vector3(110f, 110f, 110f),
            userId: 101);

        TestAssert.True(h1.IsValid, "h1 is valid");
        TestAssert.True(index.IsValid(h1), "index reports h1 is valid");
        TestAssert.Equal(101, index.GetEntry(h1).UserId, "h1 payload matches");

        // Remove h1 -> slot 0 is freed and its generation is incremented
        bool removed = index.Remove(h1);
        TestAssert.True(removed, "h1 removed successfully");
        TestAssert.False(index.IsValid(h1), "h1 is no longer valid after removal");

        // Calling operations with stale h1 should safely fail or no-op
        TestAssert.False(index.Remove(h1), "re-removing stale h1 returns false");
        index.SetActive(h1, true); // Should no-op safely

        // Register second entry -> reuses slot 0 with incremented generation
        var h2 = index.RegisterStatic(
            SpatialCategory.MapModel,
            SpatialShapeKind.Obb,
            origin: new Vector3(200f, 200f, 200f),
            min: new Vector3(190f, 190f, 190f),
            max: new Vector3(210f, 210f, 210f),
            userId: 202);

        TestAssert.Equal(h1.Id, h2.Id, "h2 recycled the slot from h1");
        TestAssert.True(h2.Generation > h1.Generation, "h2 has a newer generation than h1");
        TestAssert.True(index.IsValid(h2), "h2 is valid");
        TestAssert.False(index.IsValid(h1), "stale h1 remains invalid despite slot reuse");

        // Mutating h1 must NOT modify h2
        index.UpdateBounds(h1, new Vector3(0, 0, 0), new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
        TestAssert.Equal(new Vector3(200f, 200f, 200f), index.GetEntry(h2).Origin, "h2 origin unmodified by stale h1 update");

        index.SetActive(h1, false);
        TestAssert.True(index.GetEntry(h2).Active, "h2 active state unmodified by stale h1 set-active");
    }

    [Test]
    public void DynamicEntityUpdatesInPlaceWithinCellAndRelinksOnCellBoundary()
    {
        var index = new WorldSpatialIndex(worldSize: 1024, cellShift: 6); // 64-unit cells
        var player = NewPlayer(new Vector3(150f, 150f, 526f)); // cell (2, 2) covers [128..192)

        SpatialHandle handle = index.RegisterPhysEnt(player, SpatialCategory.Player, userId: 7, team: 1);
        TestAssert.Equal(1, index.DynamicCount, "1 dynamic entity registered");
        TestAssert.Equal(0L, index.InPlaceUpdates, "0 initial in-place updates");
        TestAssert.Equal(0L, index.BoundaryCrossings, "0 initial boundary crossings");

        // Simulate 20 physics substeps moving 0.5 units per step inside cell (2, 2): 150 -> 160
        for (int i = 0; i < 20; i++)
        {
            player.O += new Vector3(0.5f, 0.25f, 0f);
            index.SyncDynamicEntities();
        }

        TestAssert.Equal(20L, index.InPlaceUpdates, "all 20 sub-cell steps took the O(1) in-place fast path");
        TestAssert.Equal(0L, index.BoundaryCrossings, "no cell boundary crossings occurred inside [128..192)");

        // Now move player across the 192.0 cell boundary into cell (3, 2)
        player.O = new Vector3(205f, 155f, 526f);
        index.SyncDynamicEntities();
        TestAssert.Equal(1L, index.BoundaryCrossings, "crossing into cell (3, 2) recorded 1 boundary crossing");

        // Verify query at old cell (150, 150) returns 0 and new cell (205, 155) returns the player
        Span<int> hits = stackalloc int[8];
        var oldResult = index.QueryRadius(new Vector3(150f, 150f, 526f), 8f, SpatialCategory.Player, hits);
        TestAssert.Equal(0, oldResult.Count, "old cell no longer contains player");

        var newResult = index.QueryRadius(new Vector3(205f, 155f, 526f), 8f, SpatialCategory.Player, hits);
        TestAssert.Equal(1, newResult.Count, "new cell contains moved player");
        TestAssert.Equal(handle.Id, hits[0], "matched player handle ID");
    }

    [Test]
    public void MultiCellDeduplicationEnsuresSingleResult()
    {
        var index = new WorldSpatialIndex(worldSize: 1024, cellShift: 6); // 64-unit cells

        // Register an object that straddles the boundary between cell (1, 1) and (2, 2): [60..70]
        var h = index.RegisterStatic(
            SpatialCategory.MapModel,
            SpatialShapeKind.Obb,
            origin: new Vector3(64f, 64f, 100f),
            min: new Vector3(50f, 50f, 90f),
            max: new Vector3(78f, 78f, 110f));

        // Query an AABB that covers cells (0, 0) through (2, 2)
        Span<int> hits = stackalloc int[16];
        var result = index.QueryAabb(new Vector3(40f, 40f, 80f), new Vector3(90f, 90f, 120f), SpatialCategory.MapModel, hits);

        TestAssert.Equal(1, result.Count, "entry spanning 4 cells is returned exactly once in query results");
        TestAssert.Equal(1, result.TotalMatches, "total matches is 1");
        TestAssert.False(result.IsTruncated, "result is not truncated");
        TestAssert.Equal(h.Id, hits[0], "hit matches handle ID");

        // Also test the List<int> overload
        var listHits = new List<int>();
        int listCount = index.QueryAabb(new Vector3(40f, 40f, 80f), new Vector3(90f, 90f, 120f), SpatialCategory.MapModel, listHits);
        TestAssert.Equal(1, listCount, "List<int> overload also returns exactly 1 item");
        TestAssert.Equal(h.Id, listHits[0], "List<int> hit matches handle ID");
    }

    [Test]
    public void QueryOverflowHandlingAndTruncationStatus()
    {
        var index = new WorldSpatialIndex(worldSize: 1024);

        // Register 10 items in close proximity
        for (int i = 0; i < 10; i++)
        {
            Vector3 pos = new(300f + i * 2f, 300f, 500f);
            index.RegisterStatic(
                SpatialCategory.ItemZone,
                SpatialShapeKind.Aabb,
                origin: pos,
                min: pos - new Vector3(1f, 1f, 1f),
                max: pos + new Vector3(1f, 1f, 1f),
                userId: i);
        }

        // Query with a buffer of length 4 (smaller than the 10 matching entries)
        Span<int> smallBuffer = stackalloc int[4];
        var result = index.QueryAabb(
            new Vector3(290f, 290f, 490f),
            new Vector3(330f, 310f, 510f),
            SpatialCategory.ItemZone,
            smallBuffer);

        TestAssert.Equal(4, result.Count, "wrote exactly buffer length (4) entries");
        TestAssert.Equal(10, result.TotalMatches, "total matches reported is 10");
        TestAssert.True(result.IsTruncated, "IsTruncated is true when TotalMatches > Count");

        // Callers can use TotalMatches to allocate or rent a large enough buffer and retry
        Span<int> fullBuffer = stackalloc int[result.TotalMatches];
        var retryResult = index.QueryAabb(
            new Vector3(290f, 290f, 490f),
            new Vector3(330f, 310f, 510f),
            SpatialCategory.ItemZone,
            fullBuffer);

        TestAssert.Equal(10, retryResult.Count, "all 10 entries retrieved after sizing buffer");
        TestAssert.Equal(10, retryResult.TotalMatches, "total matches is 10");
        TestAssert.False(retryResult.IsTruncated, "IsTruncated is false when capacity is sufficient");

        // Test QueryRadius overflow semantics
        var radiusResult = index.QueryRadius(new Vector3(310f, 300f, 500f), 20f, SpatialCategory.ItemZone, smallBuffer);
        TestAssert.Equal(4, radiusResult.Count, "radius query capped at buffer length");
        TestAssert.Equal(10, radiusResult.TotalMatches, "radius query counted all 10 items in radius");
        TestAssert.True(radiusResult.IsTruncated, "radius query marked truncated");

        // List<int> overloads never truncate
        var listResults = new List<int>();
        int listCount = index.QueryRadius(new Vector3(310f, 300f, 500f), 20f, SpatialCategory.ItemZone, listResults);
        TestAssert.Equal(10, listCount, "List<int> radius query returned all 10 entries without truncation");
    }

    [Test]
    public void ProjectileSegmentAndExplosionRadiusQueriesPruneDistantEntities()
    {
        var index = new WorldSpatialIndex(worldSize: 2048);

        var pNear = NewPlayer(new Vector3(200f, 200f, 526f));
        var pMid = NewPlayer(new Vector3(300f, 202f, 526f));
        var pFar = NewPlayer(new Vector3(1500f, 1500f, 526f));

        SpatialHandle hNear = index.RegisterPhysEnt(pNear, SpatialCategory.Player, userId: 1);
        SpatialHandle hMid = index.RegisterPhysEnt(pMid, SpatialCategory.Player, userId: 2);
        SpatialHandle hFar = index.RegisterPhysEnt(pFar, SpatialCategory.Player, userId: 3);

        Span<int> hits = stackalloc int[16];

        // 1. Rocket flying along X from (180, 200, 520) to (320, 200, 520)
        var segResult = index.QuerySegment(
            new Vector3(180f, 200f, 520f),
            new Vector3(320f, 200f, 520f),
            radius: 2f,
            SpatialCategory.Actors,
            hits);

        TestAssert.Equal(2, segResult.Count, "rocket segment query finds the 2 players along flight path and prunes distant player");
        TestAssert.False(segResult.IsTruncated, "segment query was not truncated");

        // 2. Grenade / Rocket explosion at (210, 200, 520) with ExpRad = 40
        var blastResult = index.QueryRadius(
            new Vector3(210f, 200f, 520f),
            radius: 40f,
            SpatialCategory.Actors,
            hits);

        TestAssert.Equal(1, blastResult.Count, "explosion blast radius query finds only the player within 40 units");
        TestAssert.Equal(hNear.Id, hits[0], "explosion hit is pNear");
        TestAssert.True(hMid.IsValid && hFar.IsValid, "other handles remain valid");
    }
}
