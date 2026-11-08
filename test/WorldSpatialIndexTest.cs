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
    public void MapModelsReadOnlyOwnershipEnforcesRemovalThroughPhysicsMethods()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        var lampInfo = new ModelInfo
        {
            Name = "objects/lantern01",
            Collide = true,
            EllipseCollide = false,
            CollideCenter = new Vector3(0f, 0f, 12f),
            CollideRadius = new Vector3(2f, 2f, 13f),
            RejectRadius = 25f,
        };

        var h0 = phys.AddMapModel(new Vector3(200f, 200f, 512f), 0f, lampInfo);
        var h1 = phys.AddMapModel(new Vector3(400f, 400f, 512f), 0f, lampInfo);

        TestAssert.Equal(2, phys.MapModels.Count, "2 map models registered");
        TestAssert.Equal(2, phys.Spatial.StaticCount, "2 static entries in spatial index");

        // Verify MapModels is IReadOnlyList and cannot be mutated as a mutable List
        TestAssert.True(phys.MapModels is IReadOnlyList<MapModelCollider>, "exposed as IReadOnlyList");

        // Remove by index using RemoveMapModelAt
        bool removed = phys.RemoveMapModelAt(0);
        TestAssert.True(removed, "RemoveMapModelAt succeeded");
        TestAssert.Equal(1, phys.MapModels.Count, "1 map model remaining");
        TestAssert.Equal(1, phys.Spatial.StaticCount, "1 static entry remaining in spatial index");
        TestAssert.False(phys.Spatial.IsValid(h0), "removed model's handle is invalidated");
        TestAssert.True(phys.Spatial.IsValid(h1), "surviving model's handle is still valid");

        // Clear all map models
        phys.ClearMapModels();
        TestAssert.Equal(0, phys.MapModels.Count, "0 models after ClearMapModels");
        TestAssert.Equal(0, phys.Spatial.StaticCount, "0 static entries in spatial index after ClearMapModels");
        TestAssert.False(phys.Spatial.IsValid(h1), "cleared model's handle is invalidated");
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

    [Test]
    public void PropertiesAndCapacityAllocationGrowDynamically()
    {
        var index = new WorldSpatialIndex(worldSize: 1024, cellShift: 6, initialCapacity: 16);

        TestAssert.Equal(64, index.CellSize, "CellSize matches 1 << CellShift");
        TestAssert.Equal(6, index.CellShift, "CellShift is 6");
        TestAssert.Equal(16, index.GridDim, "GridDim is 16 for 1024 / 64");
        TestAssert.Equal(1024, index.WorldSize, "WorldSize is 1024");
        TestAssert.Equal(0, index.Count, "Initial Count is 0");

        // Register 20 entries to force capacity resize past 16
        SpatialHandle lastHandle = SpatialHandle.Invalid;
        for (int i = 0; i < 20; i++)
        {
            Vector3 pos = new(100f + i * 2f, 100f, 100f);
            lastHandle = index.RegisterStatic(
                SpatialCategory.Geometry,
                SpatialShapeKind.Aabb,
                origin: pos,
                min: pos - Vector3.One,
                max: pos + Vector3.One,
                userId: i);
        }

        TestAssert.Equal(20, index.StaticCount, "20 static entries registered");
        TestAssert.Equal(20, index.Count, "Total Count is 20");
        TestAssert.True(lastHandle.IsValid, "last handle is valid");

        // GetEntryById and GetEntry
        ref readonly var entry = ref index.GetEntry(lastHandle);
        TestAssert.Equal(19, entry.UserId, "payload matches via GetEntry");

        ref readonly var rawEntry = ref index.GetEntryById(lastHandle.Id);
        TestAssert.Equal(19, rawEntry.UserId, "payload matches via GetEntryById");

        // Stale / invalid handle validations
        TestAssert.False(index.IsValid(SpatialHandle.Invalid), "Invalid handle is not valid");
        TestAssert.False(index.IsValid(new SpatialHandle(-10, 1)), "Negative ID is not valid");
        TestAssert.False(index.IsValid(new SpatialHandle(999, 1)), "Out of bounds ID is not valid");

        bool threw = false;
        try
        {
            index.GetEntry(SpatialHandle.Invalid);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        TestAssert.True(threw, "GetEntry with invalid handle throws ArgumentException");
    }

    [Test]
    public void EnsureWorldSizeResizesGridAndRelinksAllocatedEntries()
    {
        var index = new WorldSpatialIndex(worldSize: 1024, cellShift: 6);

        var hStatic1 = index.RegisterStatic(SpatialCategory.Geometry, SpatialShapeKind.Aabb, new Vector3(100f, 100f, 100f), new Vector3(90f, 90f, 90f), new Vector3(110f, 110f, 110f), userId: 1);
        var hStatic2 = index.RegisterStatic(SpatialCategory.Geometry, SpatialShapeKind.Aabb, new Vector3(200f, 200f, 100f), new Vector3(190f, 190f, 90f), new Vector3(210f, 210f, 110f), userId: 2);
        var hDynamic = index.RegisterDynamic(SpatialCategory.Projectile, SpatialShapeKind.Sphere, new Vector3(300f, 300f, 100f), new Vector3(290f, 290f, 90f), new Vector3(310f, 310f, 110f), userId: 3);

        // Remove hStatic1 so there is an unallocated hole in _entries
        index.Remove(hStatic1);

        // Resizing to same size should be a no-op
        index.EnsureWorldSize(1024);
        TestAssert.Equal(1024, index.WorldSize, "WorldSize unchanged");

        // Resize to 2048
        index.EnsureWorldSize(2048);
        TestAssert.Equal(2048, index.WorldSize, "WorldSize updated to 2048");
        TestAssert.Equal(32, index.GridDim, "GridDim updated to 32");

        // Verify remaining entries can still be queried in their new cells
        Span<int> hits = stackalloc int[4];
        var q1 = index.QueryAabb(new Vector3(180f, 180f, 80f), new Vector3(220f, 220f, 120f), SpatialCategory.Geometry, hits);
        TestAssert.Equal(1, q1.Count, "found surviving static entry after world resize");
        TestAssert.Equal(hStatic2.Id, hits[0], "matched hStatic2 ID");

        var q2 = index.QueryAabb(new Vector3(280f, 280f, 80f), new Vector3(320f, 320f, 120f), SpatialCategory.Projectile, hits);
        TestAssert.Equal(1, q2.Count, "found dynamic entry after world resize");
        TestAssert.Equal(hDynamic.Id, hits[0], "matched hDynamic ID");
    }

    [Test]
    public void ClearResetsAllStateAndInvalidatesHandles()
    {
        var index = new WorldSpatialIndex(worldSize: 1024);

        var player = NewPlayer(new Vector3(150f, 150f, 526f));
        var hStatic = index.RegisterStatic(SpatialCategory.MapModel, SpatialShapeKind.Obb, new Vector3(100f, 100f, 100f), new Vector3(90f, 90f, 90f), new Vector3(110f, 110f, 110f));
        var hDyn = index.RegisterPhysEnt(player, SpatialCategory.Player);

        TestAssert.Equal(1, index.StaticCount, "1 static");
        TestAssert.Equal(1, index.DynamicCount, "1 dynamic");
        TestAssert.Equal(2, index.Count, "2 total");

        index.Clear();

        TestAssert.Equal(0, index.StaticCount, "0 static after Clear");
        TestAssert.Equal(0, index.DynamicCount, "0 dynamic after Clear");
        TestAssert.Equal(0, index.Count, "0 total after Clear");
        TestAssert.Equal(0L, index.InPlaceUpdates, "InPlaceUpdates reset");
        TestAssert.Equal(0L, index.BoundaryCrossings, "BoundaryCrossings reset");

        TestAssert.False(index.IsValid(hStatic), "static handle invalidated after Clear");
        TestAssert.False(index.IsValid(hDyn), "dynamic handle invalidated after Clear");

        // Empty queries return 0
        Span<int> hits = stackalloc int[4];
        var q = index.QueryAabb(Vector3.Zero, new Vector3(1000f, 1000f, 1000f), SpatialCategory.All, hits);
        TestAssert.Equal(0, q.Count, "0 results after Clear");
    }

    [Test]
    public void RegisterDynamicMetadataAndPhysEntLifecycle()
    {
        var index = new WorldSpatialIndex(worldSize: 1024);

        // RegisterDynamic without PhysEnt
        var hDyn = index.RegisterDynamic(
            SpatialCategory.Projectile,
            SpatialShapeKind.Sphere,
            origin: new Vector3(200f, 200f, 200f),
            min: new Vector3(195f, 195f, 195f),
            max: new Vector3(205f, 205f, 205f),
            userId: 50,
            team: 1);

        TestAssert.Equal(1, index.DynamicCount, "1 dynamic count");
        TestAssert.Equal(50, index.GetEntry(hDyn).UserId, "initial userId is 50");
        TestAssert.Equal(1, index.GetEntry(hDyn).Team, "initial team is 1");

        // SetMetadata
        index.SetMetadata(hDyn, userId: 51, team: 2);
        TestAssert.Equal(51, index.GetEntry(hDyn).UserId, "updated userId is 51");
        TestAssert.Equal(2, index.GetEntry(hDyn).Team, "updated team is 2");

        // SetMetadata on invalid handle should safely no-op
        index.SetMetadata(SpatialHandle.Invalid, userId: 99);

        // UpdatePosition
        index.UpdatePosition(hDyn, new Vector3(220f, 200f, 200f), yaw: 45f);
        TestAssert.Equal(new Vector3(220f, 200f, 200f), index.GetEntry(hDyn).Origin, "position translated");
        TestAssert.Equal(new Vector3(215f, 195f, 195f), index.GetEntry(hDyn).Min, "min translated");
        TestAssert.Equal(45f, index.GetEntry(hDyn).Yaw, "yaw updated");

        // UpdatePosition on invalid handle
        index.UpdatePosition(SpatialHandle.Invalid, Vector3.Zero);

        // UpdatePhysEnt on invalid handle and on dynamic entry without PhysEnt
        index.UpdatePhysEnt(SpatialHandle.Invalid);
        index.UpdatePhysEnt(hDyn); // has no TrackedPhysEnt, should no-op safely

        // UpdatePhysEnt with entOverride
        var player = NewPlayer(new Vector3(220f, 200f, 200f));
        index.UpdatePhysEnt(hDyn, player);
        TestAssert.Equal(player.Radius, index.GetEntry(hDyn).RejectRadius, "reject radius updated via override");

        // Dead player registration (active should be false)
        var deadPlayer = NewPlayer(new Vector3(500f, 500f, 500f));
        deadPlayer.State = PhysEnt.CsDead;
        var hDead = index.RegisterPhysEnt(deadPlayer);
        TestAssert.False(index.GetEntry(hDead).Active, "dead player is inactive by default");

        // StepBroadphase alias
        index.StepBroadphase(evaluateZones: true);

        // Remove dynamic entry without TrackedPhysEnt
        bool removed = index.Remove(hDyn);
        TestAssert.True(removed, "untracked dynamic entry removed successfully");
        TestAssert.Equal(1, index.DynamicCount, "1 dynamic entry remains (dead player)");
    }

    [Test]
    public void InPlaceStaticZBoundsExpansionAndCellMetadataRecomputation()
    {
        var index = new WorldSpatialIndex(worldSize: 1024, cellShift: 6);

        // Register two static entries in the same cell (1, 1) -> [64..128)
        var h1 = index.RegisterStatic(
            SpatialCategory.MapModel,
            SpatialShapeKind.Obb,
            origin: new Vector3(80f, 80f, 100f),
            min: new Vector3(75f, 75f, 90f),
            max: new Vector3(85f, 85f, 110f));

        var h2 = index.RegisterStatic(
            SpatialCategory.Geometry,
            SpatialShapeKind.Aabb,
            origin: new Vector3(90f, 90f, 150f),
            min: new Vector3(85f, 85f, 140f),
            max: new Vector3(95f, 95f, 160f));

        // UpdateBounds on h1 staying in cell (1, 1) expanding Z bounds
        long prevInPlace = index.InPlaceUpdates;
        index.UpdateBounds(h1, new Vector3(80f, 80f, 100f), new Vector3(75f, 75f, 70f), new Vector3(85f, 85f, 120f));
        TestAssert.Equal(prevInPlace + 1, index.InPlaceUpdates, "static in-place update counted");

        // Remove h1 -> triggers RecomputeStaticCellMetadata with h2 surviving
        index.Remove(h1);
        TestAssert.Equal(1, index.StaticCount, "1 static entry remaining");

        // Remove h2 -> triggers RecomputeStaticCellMetadata resetting to empty
        index.Remove(h2);
        TestAssert.Equal(0, index.StaticCount, "0 static entries remaining");

        // Same for dynamic entries: 2 dynamic in same cell, remove one, then remove other
        var p1 = NewPlayer(new Vector3(80f, 80f, 100f));
        var p2 = NewPlayer(new Vector3(90f, 90f, 100f));
        var hd1 = index.RegisterPhysEnt(p1, SpatialCategory.Player);
        var hd2 = index.RegisterPhysEnt(p2, SpatialCategory.Monster);

        index.Remove(hd1);
        TestAssert.Equal(1, index.DynamicCount, "1 dynamic remaining after removing hd1");
        index.Remove(hd2);
        TestAssert.Equal(0, index.DynamicCount, "0 dynamic remaining after removing hd2");
    }

    [Test]
    public void QueryFiltersPruningAndLargeListCapacityRent()
    {
        var index = new WorldSpatialIndex(worldSize: 1024, cellShift: 6);

        // 1. Empty / zero / negative queries
        Span<int> hits = stackalloc int[4];
        var emptyRes = index.QueryAabb(Vector3.Zero, Vector3.One, SpatialCategory.All, hits);
        TestAssert.Equal(0, emptyRes.Count, "QueryAabb on empty index returns 0");

        var noneRes = index.QueryAabb(Vector3.Zero, Vector3.One, SpatialCategory.None, hits);
        TestAssert.Equal(0, noneRes.Count, "QueryAabb with None mask returns 0");

        var negRes = index.QueryRadius(Vector3.Zero, -5f, SpatialCategory.All, hits);
        TestAssert.Equal(0, negRes.Count, "QueryRadius with negative radius returns 0");

        var listHits = new List<int>();
        int listNeg = index.QueryRadius(Vector3.Zero, -5f, SpatialCategory.All, listHits);
        TestAssert.Equal(0, listNeg, "List QueryRadius with negative radius returns 0");

        int listNone = index.QueryRadius(Vector3.Zero, 10f, SpatialCategory.None, listHits);
        TestAssert.Equal(0, listNone, "List QueryRadius with None mask returns 0");

        // 2. Register an item and test spherical rejection (inside AABB corner but outside radius)
        Vector3 boxCenter = new(100f, 100f, 100f);
        index.RegisterStatic(
            SpatialCategory.Geometry,
            SpatialShapeKind.Aabb,
            origin: boxCenter,
            min: new Vector3(108f, 108f, 108f),
            max: new Vector3(110f, 110f, 110f));

        // Center at (100, 100, 100) with radius 10. The box corner (108, 108, 108) is at dist = sqrt(64+64+64) = 13.85 > 10.
        // It overlaps the AABB [90..110] but is outside sphere radius 10!
        var sphereRejectRes = index.QueryRadius(boxCenter, 10f, SpatialCategory.Geometry, hits);
        TestAssert.Equal(0, sphereRejectRes.Count, "box in corner of AABB rejected by sphere radius check");

        // 3. Register > 130 items to test the ArrayPool rented buffer path in List overloads (> 128)
        for (int i = 0; i < 140; i++)
        {
            Vector3 pos = new(200f + (i % 10) * 2f, 200f + (i / 10) * 2f, 200f);
            index.RegisterStatic(
                SpatialCategory.ItemZone,
                SpatialShapeKind.Aabb,
                origin: pos,
                min: pos - new Vector3(0.5f, 0.5f, 0.5f),
                max: pos + new Vector3(0.5f, 0.5f, 0.5f),
                userId: i);
        }

        int listAabbCount = index.QueryAabb(new Vector3(190f, 190f, 190f), new Vector3(240f, 240f, 210f), SpatialCategory.ItemZone, listHits);
        TestAssert.Equal(140, listAabbCount, "QueryAabb List overload successfully rented buffer for 140 entries");

        int listRadiusCount = index.QueryRadius(new Vector3(210f, 215f, 200f), 50f, SpatialCategory.ItemZone, listHits);
        TestAssert.Equal(140, listRadiusCount, "QueryRadius List overload successfully rented buffer for 140 entries");

        // 4. QuerySegment with List and negative direction ray
        int segCount = index.QuerySegment(new Vector3(240f, 240f, 200f), new Vector3(190f, 190f, 200f), 10f, SpatialCategory.ItemZone, listHits);
        TestAssert.Equal(140, segCount, "QuerySegment List overload handled reverse segment");

        // 5. NextQueryStamp rollover simulation
        var queryStampField = typeof(WorldSpatialIndex).GetField("_queryStamp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        queryStampField!.SetValue(index, uint.MaxValue);
        var rolledOverRes = index.QueryAabb(new Vector3(190f, 190f, 190f), new Vector3(240f, 240f, 210f), SpatialCategory.ItemZone, hits);
        TestAssert.True(rolledOverRes.Count > 0, "Query succeeded after query stamp rollover");
    }

    [Test]
    public void CollideMapModelsComprehensiveBranchesAndRejections()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        // 1. CollideMapModels with StaticCount == 0
        var p = NewPlayer(new Vector3(256f, 256f, 512f));
        TestAssert.False(phys.Spatial.CollideMapModels(phys, p, new Vector3(1f, 0f, 0f)), "StaticCount == 0 returns false");

        // 2. MapModel with EllipseCollide = true and default RejectRadius (computed from center and radius)
        var barrelInfo = new ModelInfo
        {
            Name = "objects/barrel",
            Collide = true,
            EllipseCollide = true,
            CollideCenter = new Vector3(0f, 0f, 6f),
            CollideRadius = new Vector3(4f, 4f, 6f),
            RejectRadius = 0f, // test fallback computation
        };

        Vector3 barrelPos = new(256f, 256f, 512f);
        var hBarrel = phys.AddMapModel(barrelPos, yaw: 0f, barrelInfo);
        TestAssert.True(phys.Spatial.GetEntry(hBarrel).RejectRadius > 0f, "fallback reject radius was computed");
        TestAssert.Equal(SpatialShapeKind.Ellipse, phys.Spatial.GetEntry(hBarrel).Shape, "EllipseCollide sets Ellipse shape");

        // 3. Player with CollideObb (not an ellipse collide type) -> returns false
        p.CollideType = Phys.CollideObb;
        TestAssert.False(phys.Spatial.CollideMapModels(phys, p, new Vector3(1f, 0f, 0f)), "CollideObb falls through switch and returns false");

        // 4. Player with CollideEllipse colliding with Ellipse barrel
        p.CollideType = Phys.CollideEllipse;
        p.O = new Vector3(256f + 4f + 4.0f, 256f, 526f); // touching
        TestAssert.True(phys.Spatial.CollideMapModels(phys, p, new Vector3(-1f, 0f, 0f)), "CollideEllipse collides with Ellipse barrel");

        // 5. Player far away (outside Reject2D in +X, -X, +Y, -Y)
        p.O = new Vector3(256f + 50f, 256f, 526f);
        TestAssert.False(phys.Spatial.CollideMapModels(phys, p, new Vector3(1f, 0f, 0f)), "far player outside Reject2D +X does not collide");

        p.O = new Vector3(256f - 50f, 256f, 526f);
        TestAssert.False(phys.Spatial.CollideMapModels(phys, p, new Vector3(-1f, 0f, 0f)), "far player outside Reject2D -X does not collide");

        p.O = new Vector3(256f, 256f + 50f, 526f);
        TestAssert.False(phys.Spatial.CollideMapModels(phys, p, new Vector3(0f, 1f, 0f)), "far player outside Reject2D +Y does not collide");

        p.O = new Vector3(256f, 256f - 50f, 526f);
        TestAssert.False(phys.Spatial.CollideMapModels(phys, p, new Vector3(0f, -1f, 0f)), "far player outside Reject2D -Y does not collide");

        // 6. Player vertical reject: above barrel and below barrel
        p.O = new Vector3(256f, 256f, 600f); // far above
        TestAssert.False(phys.Spatial.CollideMapModels(phys, p, new Vector3(0f, 0f, -1f)), "player above barrel does not collide");

        p.O = new Vector3(256f, 256f, 400f); // far below
        TestAssert.False(phys.Spatial.CollideMapModels(phys, p, new Vector3(0f, 0f, 1f)), "player below barrel does not collide");

        // 7. Multi-cell straddling: model at cell boundary
        Vector3 boundaryPos = new(64f, 64f, 512f);
        phys.AddMapModel(boundaryPos, yaw: 0f, barrelInfo);
        p.O = new Vector3(64f + 4f + 4.0f, 64f, 526f);
        TestAssert.True(phys.Spatial.CollideMapModels(phys, p, new Vector3(-1f, 0f, 0f)), "straddling boundary map model collides and deduplicates");
    }

    [Test]
    public void FullBranchCoverageAcrossQueriesCollisionAndLifecycle()
    {
        var index = new WorldSpatialIndex(worldSize: 1024, cellShift: 6);

        // 1. Generation wrap-around in Remove and Clear
        var hGen = index.RegisterStatic(
            SpatialCategory.Geometry,
            SpatialShapeKind.Aabb,
            new Vector3(100f, 100f, 100f),
            new Vector3(90f, 90f, 90f),
            new Vector3(110f, 110f, 110f));

        var entriesField = typeof(WorldSpatialIndex).GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var entries = (SpatialEntry[])entriesField!.GetValue(index)!;
        entries[hGen.Id].Generation = uint.MaxValue;

        // Calling Remove with Generation == uint.MaxValue causes generation++ to wrap to 0, which gets set to 1
        index.Remove(new SpatialHandle(hGen.Id, uint.MaxValue));
        TestAssert.Equal(1u, entries[hGen.Id].Generation, "Generation wrapped from uint.MaxValue to 1 on Remove");

        // Now test Generation wrap-around in Clear()
        var hClear = index.RegisterStatic(
            SpatialCategory.Geometry,
            SpatialShapeKind.Aabb,
            new Vector3(100f, 100f, 100f),
            new Vector3(90f, 90f, 90f),
            new Vector3(110f, 110f, 110f));
        entries[hClear.Id].Generation = uint.MaxValue;
        index.Clear();
        TestAssert.Equal(1u, entries[hClear.Id].Generation, "Generation wrapped from uint.MaxValue to 1 on Clear");

        // 2. ExpandStaticZBounds with Max.Z expanding past cell.StaticMaxZ
        var hStaticZ = index.RegisterStatic(
            SpatialCategory.MapModel,
            SpatialShapeKind.Obb,
            new Vector3(100f, 100f, 100f),
            new Vector3(90f, 90f, 90f),
            new Vector3(110f, 110f, 110f));

        // Update bounds within same cell expanding both Min.Z (< 90) and Max.Z (> 110)
        index.UpdateBounds(
            hStaticZ,
            new Vector3(100f, 100f, 100f),
            new Vector3(90f, 90f, 50f),
            new Vector3(110f, 110f, 200f));
        TestAssert.Equal(50f, index.GetEntry(hStaticZ).Min.Z, "Min.Z updated");
        TestAssert.Equal(200f, index.GetEntry(hStaticZ).Max.Z, "Max.Z updated");

        // 3. SyncDynamicEntities defensive check: entry in _trackedDynamicIds with TrackedPhysEnt = null or !Allocated
        var player = NewPlayer(new Vector3(100f, 100f, 526f));
        var hTracked = index.RegisterPhysEnt(player, SpatialCategory.Player);
        entries[hTracked.Id].TrackedPhysEnt = null; // simulate untracked/cleared
        index.SyncDynamicEntities(); // tests e.TrackedPhysEnt == null branch in line 475
        entries[hTracked.Id].Allocated = false; // simulate unallocated
        index.SyncDynamicEntities(); // tests !e.Allocated branch in line 475

        // Restore so removal works
        entries[hTracked.Id].Allocated = true;
        entries[hTracked.Id].TrackedPhysEnt = player;
        index.Remove(hTracked);
        index.Clear();

        // 4. QueryListCore and ScanCellListEntries branch coverage:
        // Register static active matching, static inactive, static category mismatch, dynamic matching, dynamic mismatch
        var hActiveStatic = index.RegisterStatic(
            SpatialCategory.ItemZone,
            SpatialShapeKind.Aabb,
            new Vector3(100f, 100f, 100f),
            new Vector3(95f, 95f, 95f),
            new Vector3(105f, 105f, 105f));

        var hInactiveStatic = index.RegisterStatic(
            SpatialCategory.ItemZone,
            SpatialShapeKind.Aabb,
            new Vector3(100f, 100f, 100f),
            new Vector3(95f, 95f, 95f),
            new Vector3(105f, 105f, 105f),
            active: false);

        var hMismatchStatic = index.RegisterStatic(
            SpatialCategory.Geometry,
            SpatialShapeKind.Aabb,
            new Vector3(100f, 100f, 100f),
            new Vector3(95f, 95f, 95f),
            new Vector3(105f, 105f, 105f));

        var hActiveDyn = index.RegisterDynamic(
            SpatialCategory.Player,
            SpatialShapeKind.Sphere,
            new Vector3(100f, 100f, 100f),
            new Vector3(95f, 95f, 95f),
            new Vector3(105f, 105f, 105f));

        var hMismatchDyn = index.RegisterDynamic(
            SpatialCategory.Bouncer,
            SpatialShapeKind.Sphere,
            new Vector3(100f, 100f, 100f),
            new Vector3(95f, 95f, 95f),
            new Vector3(105f, 105f, 105f));

        var list = new List<int>();

        // Query with None mask while Count > 0
        int noneCount = index.QueryAabb(new Vector3(50f, 50f, 50f), new Vector3(150f, 150f, 150f), SpatialCategory.None, list);
        TestAssert.Equal(0, noneCount, "QueryAabb List with None mask returns 0");

        // Single cell query with List<int> (startX == endX, startY == endY) matching ItemZone
        int singleCellList = index.QueryAabb(new Vector3(98f, 98f, 98f), new Vector3(102f, 102f, 102f), SpatialCategory.ItemZone, list);
        TestAssert.Equal(1, singleCellList, "Single cell list query returned active matching item");
        TestAssert.Equal(hActiveStatic.Id, list[0], "matched hActiveStatic");

        // Single cell query matching Player (dynamic in QueryListCore)
        int dynList = index.QueryAabb(new Vector3(98f, 98f, 98f), new Vector3(102f, 102f, 102f), SpatialCategory.Player, list);
        TestAssert.Equal(1, dynList, "QueryListCore found dynamic player entry");
        TestAssert.Equal(hActiveDyn.Id, list[0], "matched hActiveDyn");

        // MultiCell query with List matching Player
        int multiDynList = index.QueryAabb(new Vector3(50f, 50f, 50f), new Vector3(150f, 150f, 150f), SpatialCategory.Player, list);
        TestAssert.Equal(1, multiDynList, "MultiCell QueryListCore found dynamic player entry");

        // Single cell query with Span<int> covering inactive and category mismatch branches
        Span<int> spanBuf = stackalloc int[4];
        var singleCellSpan = index.QueryAabb(new Vector3(98f, 98f, 98f), new Vector3(102f, 102f, 102f), SpatialCategory.ItemZone, spanBuf);
        TestAssert.Equal(1, singleCellSpan.Count, "Single cell span query returned active matching item");

        // Query with filter mismatch (e.g. AABB inside cell but not overlapping items)
        int filterMismatchCount = index.QueryAabb(new Vector3(115f, 115f, 115f), new Vector3(120f, 120f, 120f), SpatialCategory.All, list);
        TestAssert.Equal(0, filterMismatchCount, "0 matches when filter does not overlap items inside cell");

        var filterMismatchSpan = index.QueryAabb(new Vector3(115f, 115f, 115f), new Vector3(120f, 120f, 120f), SpatialCategory.All, spanBuf);
        TestAssert.Equal(0, filterMismatchSpan.Count, "0 span matches when filter does not overlap items inside cell");

        // 5. CollideMapModels single-cell and precise box branches
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        var boxInfo = new ModelInfo
        {
            Name = "objects/box",
            Collide = true,
            EllipseCollide = false,
            CollideCenter = new Vector3(0f, 0f, 5f),
            CollideRadius = new Vector3(5f, 5f, 5f),
            RejectRadius = 10f,
        };

        // Add box in the center of cell (4, 4) -> (256, 256)
        var hBox = phys.AddMapModel(new Vector3(256f, 256f, 512f), yaw: 0f, boxInfo);

        // Single-cell collision (startX == endX, startY == endY)
        // Player well within cell (4, 4) [256..320)
        var pSingle = NewPlayer(new Vector3(256f + 5f + 4.0f, 256f, 526f));
        pSingle.CollideType = Phys.CollideEllipsePrecise; // test CollideEllipsePrecise with Obb
        TestAssert.True(phys.Spatial.CollideMapModels(phys, pSingle, new Vector3(-1f, 0f, 0f)), "single-cell CollideEllipsePrecise collides with Obb box");

        // Test RegisterMapModel directly with RejectRadius <= 0f to trigger fallback reject radius calculation
        var directCollider = new MapModelCollider
        {
            O = new Vector3(300f, 300f, 500f),
            Center = new Vector3(2f, 2f, 4f),
            Radius = new Vector3(4f, 4f, 8f),
            RejectRadius = 0f,
            Collide = true,
            EllipseCollide = false,
        };
        var hDirectMm = index.RegisterMapModel(directCollider);
        TestAssert.True(directCollider.RejectRadius > 0f, "direct collider reject radius computed via fallback");
        TestAssert.True(index.IsValid(hDirectMm), "direct map model registered successfully");

        // Single-cell non-colliding (moving away)
        pSingle.O = new Vector3(256f + 5f + 4.0f, 256f, 526f);
        TestAssert.False(phys.Spatial.CollideMapModels(phys, pSingle, new Vector3(1f, 0f, 0f)), "moving away does not collide");

        // Inactive model
        phys.Spatial.SetActive(hBox, false);
        TestAssert.False(phys.Spatial.CollideMapModels(phys, pSingle, new Vector3(-1f, 0f, 0f)), "inactive model does not collide");
        phys.Spatial.SetActive(hBox, true);

        // Add a high box in the same cell at Z=600 so cell static Z span is [507..610]
        phys.AddMapModel(new Vector3(256f, 256f, 600f), yaw: 0f, boxInfo);

        // Player at Z=550 overlaps the cell Z span, but is vertically between the two models:
        // lower model triggers dBottom >= e.Max.Z, upper model triggers e.Min.Z >= dTop
        pSingle.O = new Vector3(256f, 256f, 550f);
        TestAssert.False(phys.Spatial.CollideMapModels(phys, pSingle, new Vector3(-1f, 0f, 0f)), "player vertically between two models in cell rejected by entry Z checks");

        // Player strictly above/below model within cell
        pSingle.O = new Vector3(256f, 256f, 530f); // above
        TestAssert.False(phys.Spatial.CollideMapModels(phys, pSingle, new Vector3(0f, 0f, -1f)), "above model does not collide");

        pSingle.O = new Vector3(256f, 256f, 490f); // below
        TestAssert.False(phys.Spatial.CollideMapModels(phys, pSingle, new Vector3(0f, 0f, 1f)), "below model does not collide");

        // CollideMapModels when cell has NO MapModel category
        // Position player in empty cell (1, 1)
        var pEmptyCell = NewPlayer(new Vector3(80f, 80f, 512f));
        TestAssert.False(phys.Spatial.CollideMapModels(phys, pEmptyCell, new Vector3(1f, 0f, 0f)), "empty cell returns false");

        // 6. RemoveIdPreserveOrder edge cases (via reflection)
        var removeMethod = typeof(WorldSpatialIndex).GetMethod("RemoveIdPreserveOrder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        int[]? nullArr = null;
        int zeroCount = 0;
        object?[] nullArgs = new object?[] { nullArr, zeroCount, 1 };
        removeMethod!.Invoke(null, nullArgs); // null array / count 0

        int[] arr = new int[] { 10, 20, 30 };
        int count = 3;
        object?[] notFoundArgs = new object?[] { arr, count, 99 };
        removeMethod.Invoke(null, notFoundArgs); // id not found in array
        TestAssert.Equal(3, (int)notFoundArgs[1]!, "count unchanged when ID not found");
    }
}
