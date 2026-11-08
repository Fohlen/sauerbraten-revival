using System;
using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game.Ctf;
using SauerbratenRevival.World.Models;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Spatial;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies <see cref="WorldSpatialIndex"/> and <see cref="SpatialZone"/> (ADR 0012):
/// <list type="bullet">
///   <item><description>Static registration and mapmodel collision parity (<c>OctaPhysics.MmCollide</c>)</description></item>
///   <item><description>O(1) in-place dynamic entity updates vs cell-boundary crossings</description></item>
///   <item><description>Once-per-frame broad-pass updates (<see cref="WorldSpatialIndex.StepBroadphase"/>)</description></item>
///   <item><description>Event-based zone transitions (<c>Entered</c>, <c>Stayed</c>, <c>Exited</c>)</description></item>
///   <item><description>Polling-based zone, radius, and swept-segment queries (CTF, items, projectiles)</description></item>
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
            index.StepBroadphase(evaluateZones: false);
        }

        TestAssert.Equal(20L, index.InPlaceUpdates, "all 20 sub-cell steps took the O(1) in-place fast path");
        TestAssert.Equal(0L, index.BoundaryCrossings, "no cell boundary crossings occurred inside [128..192)");

        // Now move player across the 192.0 cell boundary into cell (3, 2)
        player.O = new Vector3(205f, 155f, 526f);
        index.StepBroadphase(evaluateZones: false);
        TestAssert.Equal(1L, index.BoundaryCrossings, "crossing into cell (3, 2) recorded 1 boundary crossing");

        // Verify query at old cell (150, 150) returns 0 and new cell (205, 155) returns the player
        Span<int> hits = stackalloc int[8];
        int oldCount = index.QueryRadius(new Vector3(150f, 150f, 526f), 8f, SpatialCategory.Player, hits);
        TestAssert.Equal(0, oldCount, "old cell no longer contains player");

        int newCount = index.QueryRadius(new Vector3(205f, 155f, 526f), 8f, SpatialCategory.Player, hits);
        TestAssert.Equal(1, newCount, "new cell contains moved player");
        TestAssert.Equal(handle.Id, hits[0], "matched player handle ID");
    }

    [Test]
    public void BroadphaseZoneEventsFireEnteredStayedAndExitedTransitions()
    {
        var index = new WorldSpatialIndex(worldSize: 1024);

        // Register a CTF Flag zone at (300, 300, 512) with FlagManager.FlagRadius (16)
        Vector3 flagPos = new(300f, 300f, 512f);
        var flagZone = SpatialZone.CreateSphere(
            flagPos,
            FlagManager.FlagRadius,
            SpatialCategory.FlagZone,
            zoneId: 0,
            team: 2);
        index.RegisterZone(flagZone, isStatic: false);

        var events = new List<(ZoneOverlapKind Kind, int ZoneId, int PlayerId)>();
        flagZone.Entered += e => events.Add((e.Kind, e.Zone.ZoneId, e.EntityUserId));
        flagZone.Stayed += e => events.Add((e.Kind, e.Zone.ZoneId, e.EntityUserId));
        flagZone.Exited += e => events.Add((e.Kind, e.Zone.ZoneId, e.EntityUserId));

        // Player starts outside flag radius at X=270 (distance 30 > 16); feet at Z=512 (eye at 526)
        var player = NewPlayer(new Vector3(270f, 300f, 526f));
        index.RegisterPhysEnt(player, SpatialCategory.Player, userId: 42, team: 1);

        // Tick 1: outside zone -> no events
        index.StepBroadphase();
        TestAssert.Equal(0, events.Count, "no events while outside flag radius");

        // Tick 2: player steps inside flag radius (X=290 -> distance 10 < 16) -> Entered
        player.O = new Vector3(290f, 300f, 526f);
        index.StepBroadphase();
        TestAssert.Equal(1, events.Count, "1 event on entering flag radius");
        TestAssert.Equal(ZoneOverlapKind.Entered, events[0].Kind, "Entered event fired");
        TestAssert.Equal(42, events[0].PlayerId, "Entered event reports player 42");

        // Tick 3: player moves slightly but remains inside flag radius (X=295 -> distance 5 < 16) -> Stayed
        player.O = new Vector3(295f, 300f, 526f);
        index.StepBroadphase();
        TestAssert.Equal(2, events.Count, "second event recorded on tick 3");
        TestAssert.Equal(ZoneOverlapKind.Stayed, events[1].Kind, "Stayed event fired (Entered not duplicated)");

        // Tick 4: player leaves flag radius (X=320 -> distance 20 > 16) -> Exited
        player.O = new Vector3(320f, 300f, 526f);
        index.StepBroadphase();
        TestAssert.Equal(3, events.Count, "third event recorded on tick 4");
        TestAssert.Equal(ZoneOverlapKind.Exited, events[2].Kind, "Exited event fired when leaving radius");

        // Tick 5: still outside -> no new events
        index.StepBroadphase();
        TestAssert.Equal(3, events.Count, "no duplicate Exited events while remaining outside");
    }

    [Test]
    public void DynamicFlagDropAndPlayerDeathTriggerZoneTransitionsAndPolling()
    {
        var index = new WorldSpatialIndex(worldSize: 1024);

        // Create CTF Base zone (Cylinder: radius 64, height ±24, matching FlagManager.InsideBase)
        Vector3 baseLoc = new(500f, 500f, 512f);
        var baseZone = SpatialZone.CreateCylinder(
            baseLoc,
            FlagManager.BaseRadius,
            FlagManager.BaseHeight,
            SpatialCategory.BaseZone,
            zoneId: 1,
            team: 1);
        SpatialHandle baseHandle = index.RegisterZone(baseZone, isStatic: true);

        // Create movable Flag zone initially at base
        var flagZone = SpatialZone.CreateSphere(
            baseLoc,
            FlagManager.FlagRadius,
            SpatialCategory.FlagZone,
            zoneId: 1,
            team: 1);
        SpatialHandle flagHandle = index.RegisterZone(flagZone, isStatic: false);

        // Verify polling parity between FlagManager.InsideBase and SpatialZone
        var flagState = new FlagState { Id = 1, Team = 1, SpawnLoc = baseLoc };
        Vector3 insidePt = new(540f, 530f, 512f + 20f); // horiz dist = 50 <= 64, dz = 20 <= 24
        Vector3 tooHighPt = new(500f, 500f, 512f + 25f); // dz = 25 > 24
        Vector3 tooFarPt = new(565f, 500f, 512f);        // horiz dist = 65 > 64

        TestAssert.Equal(FlagManager.InsideBase(flagState, insidePt), baseZone.ContainsPoint(insidePt), "InsideBase parity (inside)");
        TestAssert.Equal(FlagManager.InsideBase(flagState, tooHighPt), baseZone.ContainsPoint(tooHighPt), "InsideBase parity (too high)");
        TestAssert.Equal(FlagManager.InsideBase(flagState, tooFarPt), baseZone.ContainsPoint(tooFarPt), "InsideBase parity (too far)");

        // Register a player standing at (700, 700, 512)
        var player = NewPlayer(new Vector3(700f, 700f, 526f));
        SpatialHandle playerHandle = index.RegisterPhysEnt(player, SpatialCategory.Player, userId: 10, team: 2);

        Span<int> results = stackalloc int[8];
        TestAssert.False(index.IsEntityInZone(flagHandle, playerHandle), "player at (700,700) is not in flag zone at (500,500)");

        // Move dropped flag to (695, 700, 512) (distance 5 < 16 from player feet!)
        index.UpdatePosition(flagHandle, new Vector3(695f, 700f, 512f));
        TestAssert.True(index.IsEntityInZone(flagHandle, playerHandle), "polling immediately detects player inside relocated dropped flag zone");

        int occupants = index.QueryZoneOccupants(flagHandle, SpatialCategory.Player, results);
        TestAssert.Equal(1, occupants, "QueryZoneOccupants finds player in dropped flag zone");
        TestAssert.Equal(playerHandle.Id, results[0], "occupant ID matches player handle");

        // Broad-pass emits Entered when flag is dropped onto player
        int exitedCount = 0;
        flagZone.Exited += _ => exitedCount++;
        index.StepBroadphase();

        // Player dies -> StepBroadphase marks player inactive and emits Exited
        player.State = PhysEnt.CsDead;
        index.StepBroadphase();
        TestAssert.Equal(1, exitedCount, "player death emits ZoneExited on next broad-pass");
        TestAssert.Equal(0, index.QueryZoneOccupants(flagHandle, SpatialCategory.Player, results), "dead player no longer occupies zone");
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
        int segCount = index.QuerySegment(
            new Vector3(180f, 200f, 520f),
            new Vector3(320f, 200f, 520f),
            radius: 2f,
            SpatialCategory.Actors,
            hits);

        TestAssert.Equal(2, segCount, "rocket segment query finds the 2 players along flight path and prunes distant player");

        // 2. Grenade / Rocket explosion at (210, 200, 520) with ExpRad = 40
        int blastCount = index.QueryRadius(
            new Vector3(210f, 200f, 520f),
            radius: 40f,
            SpatialCategory.Actors,
            hits);

        TestAssert.Equal(1, blastCount, "explosion blast radius query finds only the player within 40 units");
        TestAssert.Equal(hNear.Id, hits[0], "explosion hit is pNear");
        TestAssert.True(hMid.IsValid && hFar.IsValid, "other handles remain valid");
    }
}
