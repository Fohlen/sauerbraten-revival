using System;
using System.Collections.Generic;
using System.Diagnostics;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Models;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Spatial;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Benchmark and parity probe comparing <see cref="WorldSpatialIndex"/> against
/// the baseline legacy map model grid (<see cref="LegacyMapModelGrid"/>).
/// <list type="bullet">
///   <item><description>Verifies 100% collision decision parity between both implementations</description></item>
///   <item><description>Measures query throughput over increasing collider counts (50, 250, 1000, 2500)</description></item>
///   <item><description>Measures and asserts ZERO heap allocations during collision queries after warmup</description></item>
///   <item><description>Measures dynamic sub-cell update throughput and memory usage</description></item>
/// </list>
/// </summary>
public class MapModelSpatialBenchmark : GoTestBase
{
    public MapModelSpatialBenchmark(Node testScene) : base(testScene) { }

    private static PhysEnt CreateTestPlayer(Vector3 pos) => new()
    {
        O = pos,
        NewPos = pos,
        Vel = Vector3.Zero,
        Type = Phys.EntPlayer,
        State = PhysEnt.CsAlive,
        PhysState = Phys.PhysFloor,
        Radius = 4.1f,
        XRadius = 4.1f,
        YRadius = 4.1f,
        EyeHeight = 14.0f,
        AboveEye = 1.0f,
        CollideType = Phys.CollideEllipse,
    };

    /// <summary>
    /// Reproduction of the bespoke 2D XY grid previously used in OctaPhysics._mmGrid
    /// to establish an exact baseline comparison.
    /// </summary>
    private sealed class LegacyMapModelGrid
    {
        private const int CellShift = 6; // 64 units
        private readonly int _gridDim;
        private readonly List<int>?[] _cells;
        private readonly List<MapModelCollider> _colliders = new();
        private uint[] _visitStamps = new uint[128];
        private uint _queryStamp;

        public LegacyMapModelGrid(int worldSize)
        {
            _gridDim = Math.Max(1, worldSize >> CellShift);
            _cells = new List<int>?[_gridDim * _gridDim];
        }

        public void Add(MapModelCollider c)
        {
            int idx = _colliders.Count;
            _colliders.Add(c);
            if (_visitStamps.Length < _colliders.Count)
                Array.Resize(ref _visitStamps, _colliders.Count * 2);

            float r = c.RejectRadius > 0f ? c.RejectRadius : (c.Center.Abs() + c.Radius).Length();
            c.RejectRadius = r;
            int maxCell = _gridDim - 1;
            int sx = Math.Clamp((int)MathF.Floor(c.O.X - r) >> CellShift, 0, maxCell);
            int ex = Math.Clamp((int)MathF.Floor(c.O.X + r) >> CellShift, 0, maxCell);
            int sy = Math.Clamp((int)MathF.Floor(c.O.Y - r) >> CellShift, 0, maxCell);
            int ey = Math.Clamp((int)MathF.Floor(c.O.Y + r) >> CellShift, 0, maxCell);

            for (int y = sy; y <= ey; y++)
            {
                int row = y * _gridDim;
                for (int x = sx; x <= ex; x++)
                {
                    ref var list = ref _cells[row + x];
                    list ??= new List<int>();
                    list.Add(idx);
                }
            }
        }

        public bool Collide(OctaPhysics phys, PhysEnt d, Vector3 dir)
        {
            if (_colliders.Count == 0) return false;
            int maxCell = _gridDim - 1;
            int sx = Math.Clamp((int)MathF.Floor(d.O.X - d.Radius) >> CellShift, 0, maxCell);
            int ex = Math.Clamp((int)MathF.Floor(d.O.X + d.Radius) >> CellShift, 0, maxCell);
            int sy = Math.Clamp((int)MathF.Floor(d.O.Y - d.Radius) >> CellShift, 0, maxCell);
            int ey = Math.Clamp((int)MathF.Floor(d.O.Y + d.Radius) >> CellShift, 0, maxCell);

            bool multiCell = sx != ex || sy != ey;
            uint stamp = multiCell ? ++_queryStamp : 0;
            float dTop = d.O.Z + d.AboveEye;
            float dBottom = d.O.Z - d.EyeHeight;

            for (int y = sy; y <= ey; y++)
            {
                int row = y * _gridDim;
                for (int x = sx; x <= ex; x++)
                {
                    var list = _cells[row + x];
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        int idx = list[i];
                        if (multiCell)
                        {
                            if (_visitStamps[idx] == stamp) continue;
                            _visitStamps[idx] = stamp;
                        }
                        var c = _colliders[idx];
                        if (!c.Collide) continue;
                        float cz = c.O.Z + c.Center.Z;
                        if (cz - c.Radius.Z >= dTop || dBottom >= cz + c.Radius.Z) continue;
                        if (MathF.Abs(d.O.X - c.O.X) > d.Radius + c.RejectRadius ||
                            MathF.Abs(d.O.Y - c.O.Y) > d.Radius + c.RejectRadius) continue;

                        if (c.EllipseCollide)
                        {
                            if (phys.EllipseCollide(d, dir, c.O, c.Center, c.Yaw, c.Radius.X, c.Radius.Y, c.Radius.Z, c.Radius.Z))
                                return true;
                        }
                        else
                        {
                            if (phys.EllipseBoxCollide(d, dir, c.O, c.Center, c.Yaw, c.Radius.X, c.Radius.Y, c.Radius.Z, c.Radius.Z))
                                return true;
                        }
                    }
                }
            }
            return false;
        }
    }

    [Test]
    public void BenchmarkMapModelCollisionParityAndZeroAllocations()
    {
        const int worldSize = 2048;
        var octa = new World.Octa();
        octa.EmptyMap(11); // 2048
        var phys = new OctaPhysics(octa);

        int[] colliderCounts = { 50, 250, 1000, 2500 };
        var rng = new Random(42);

        GD.Print("\n=== Map Model Spatial Index Benchmark (WorldSize=2048, 64u Cells) ===");
        GD.Print("Colliders | Legacy (ms) | Spatial (ms) | Speedup | Parity Matches | Allocations");
        GD.Print("-----------------------------------------------------------------------------");

        foreach (int count in colliderCounts)
        {
            var legacy = new LegacyMapModelGrid(worldSize);
            var spatial = new WorldSpatialIndex(worldSize: worldSize, cellShift: 6, initialCapacity: count);

            // Populate both grids with identical random colliders
            for (int i = 0; i < count; i++)
            {
                float x = rng.Next(64, worldSize - 64);
                float y = rng.Next(64, worldSize - 64);
                float z = 512f + rng.Next(-50, 200);
                bool ellipse = (i % 2) == 0;
                float radX = 4f + rng.Next(0, 16);
                float radY = 4f + rng.Next(0, 16);
                float radZ = 8f + rng.Next(0, 24);

                var collider = new MapModelCollider
                {
                    O = new Vector3(x, y, z),
                    Yaw = rng.Next(0, 360),
                    Collide = true,
                    EllipseCollide = ellipse,
                    Center = new Vector3(0, 0, radZ * 0.5f),
                    Radius = new Vector3(radX, radY, radZ),
                };

                legacy.Add(collider);
                spatial.RegisterMapModel(collider, i);
            }

            // Generate representative query positions (5,000 queries)
            const int numQueries = 5000;
            var players = new PhysEnt[numQueries];
            var dirs = new Vector3[numQueries];
            for (int q = 0; q < numQueries; q++)
            {
                players[q] = CreateTestPlayer(new Vector3(
                    rng.Next(64, worldSize - 64),
                    rng.Next(64, worldSize - 64),
                    526f + rng.Next(-30, 150)));
                dirs[q] = new Vector3(rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f, 0f).Normalized();
            }

            // Warmup JIT
            for (int w = 0; w < 100; w++)
            {
                legacy.Collide(phys, players[w], dirs[w]);
                spatial.CollideMapModels(phys, players[w], dirs[w]);
            }

            // Measure Legacy Grid
            long t0Legacy = Stopwatch.GetTimestamp();
            long startMemLegacy = GC.GetAllocatedBytesForCurrentThread();
            int legacyHits = 0;
            for (int q = 0; q < numQueries; q++)
            {
                if (legacy.Collide(phys, players[q], dirs[q])) legacyHits++;
            }
            long t1Legacy = Stopwatch.GetTimestamp();
            long allocLegacy = GC.GetAllocatedBytesForCurrentThread() - startMemLegacy;

            // Measure WorldSpatialIndex
            long t0Spatial = Stopwatch.GetTimestamp();
            long startMemSpatial = GC.GetAllocatedBytesForCurrentThread();
            int spatialHits = 0;
            for (int q = 0; q < numQueries; q++)
            {
                if (spatial.CollideMapModels(phys, players[q], dirs[q])) spatialHits++;
            }
            long t1Spatial = Stopwatch.GetTimestamp();
            long allocSpatial = GC.GetAllocatedBytesForCurrentThread() - startMemSpatial;

            // Verify parity
            int matches = 0;
            for (int q = 0; q < numQueries; q++)
            {
                bool h1 = legacy.Collide(phys, players[q], dirs[q]);
                bool h2 = spatial.CollideMapModels(phys, players[q], dirs[q]);
                if (h1 == h2) matches++;
            }

            TestAssert.Equal(numQueries, matches, $"100% parity on {count} colliders ({matches}/{numQueries})");
            TestAssert.Equal(legacyHits, spatialHits, "hit counts match exactly");
            TestAssert.Equal(0L, allocSpatial, "WorldSpatialIndex produces ZERO allocations on collision queries");

            double legacyMs = (t1Legacy - t0Legacy) * 1000.0 / Stopwatch.Frequency;
            double spatialMs = (t1Spatial - t0Spatial) * 1000.0 / Stopwatch.Frequency;
            double speedup = legacyMs / Math.Max(0.001, spatialMs);

            GD.Print($"{count,9} | {legacyMs,11:F2} | {spatialMs,12:F2} | {speedup,6:F2}x | {matches,7}/{numQueries} | {allocSpatial,5} bytes (Legacy: {allocLegacy} bytes)");
        }
        GD.Print("-----------------------------------------------------------------------------\n");
    }

    [Test]
    public void BenchmarkDynamicUpdateFastPath()
    {
        var spatial = new WorldSpatialIndex(worldSize: 1024, cellShift: 6);
        var player = CreateTestPlayer(new Vector3(200f, 200f, 526f));
        var handle = spatial.RegisterPhysEnt(player, SpatialCategory.Player, userId: 1);

        const int iterations = 50000;

        // Warmup
        for (int w = 0; w < 100; w++)
        {
            player.O += new Vector3(0.01f, 0.01f, 0f);
            spatial.UpdatePhysEnt(handle);
        }

        long t0 = Stopwatch.GetTimestamp();
        long startAlloc = GC.GetAllocatedBytesForCurrentThread();

        // 50,000 sub-cell movements inside the same cell
        for (int i = 0; i < iterations; i++)
        {
            player.O = new Vector3(200f + (i % 20) * 0.5f, 200f + (i % 20) * 0.5f, 526f);
            spatial.UpdatePhysEnt(handle);
        }

        long t1 = Stopwatch.GetTimestamp();
        long allocBytes = GC.GetAllocatedBytesForCurrentThread() - startAlloc;

        TestAssert.Equal(0L, allocBytes, "Dynamic in-place updates produce ZERO heap allocations");
        TestAssert.True(spatial.InPlaceUpdates >= iterations, "All sub-cell updates executed through O(1) in-place fast path");

        double elapsedSeconds = (t1 - t0) / (double)Stopwatch.Frequency;
        double opsPerSec = iterations / elapsedSeconds;
        GD.Print($"[Benchmark] Dynamic In-Place Updates: {iterations} updates in {elapsedSeconds * 1000.0:F2} ms ({opsPerSec / 1_000_000.0:F2} million ops/sec, {allocBytes} bytes allocated)");
    }
}
