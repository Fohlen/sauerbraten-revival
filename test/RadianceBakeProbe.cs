using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Stage 0 of the radiance-hints plan: measure, on real maps, what a world-space
/// L1-SH probe volume would cost — before any of it is built.
///
/// It samples the world on a regular grid over the map's geometry bounds,
/// estimates the fraction of probes that fall inside solid geometry, and measures
/// the two passes the bake will run:
/// the sky/hemisphere pass (<see cref="OctaRaycast.RayCube"/> ) and the direct
/// light pass (distance-cull every <c>ET_LIGHT</c>, one shadow ray per in-range
/// light). It prints per-resolution extrapolations plus the GPU memory for an
/// <c>Rgbah</c> volume (two textures: directional vector + ambient colour).
///
/// This is a diagnostic probe (like <see cref="A3ClipsProbe"/>): it never asserts
/// on timing. The numbers are a **Debug-build upper bound** — the test runtime is
/// Debug and the game's Release build should be meaningfully faster. See the
/// radiance-hints plan for how the results pick the grid resolution.
/// </summary>
public class RadianceBakeProbe : GoTestBase
{
    public RadianceBakeProbe(Node testScene) : base(testScene) { }

    /// <summary>Probe-grid resolutions to evaluate (cells per axis over the geometry bounds cube).</summary>
    private static readonly int[] Grids = { 32, 64, 128 };

    /// <summary>Cosine-weighted hemisphere rays cast per probe for the sky pass.</summary>
    private const int SkyRays = 32;

    /// <summary>Cap on sampled probes per grid (measured on an evenly-spread 3D sub-lattice).</summary>
    private const int MaxSampleProbes = 8192;

    /// <summary>Bytes per probe texel: <c>Rgbah</c> = 4 × 16-bit half float.</summary>
    private const int BytesPerTexel = 8;

    /// <summary>Directional-vector texture + ambient-colour texture.</summary>
    private const int TextureCount = 2;

    private const float InfiniteRange = 4096f; // LightBuilder.InfiniteRange

    [Test]
    [Skip("Stage 0 radiance diagnostic probe (evaluates 32/64/128 grid bake costs without assertions). Set RUN_PROBES=1 or run directly via --run-tests=RadianceBakeProbe.")]
    public void MeasureBakeCosts()
    {
        if (ShouldSkip()) return;
        Measure("church51");
        Measure("earthstation");
    }

    private void Measure(string mapName)
    {
        OgzMap map;
        try
        {
            map = TestData.ReadMap(mapName);
        }
        catch (FileNotFoundException)
        {
            GD.Print($"[radiance] {mapName}: map missing, skipping");
            return;
        }

        var octa = map.ToOcta();
        var lights = CollectLights(map);
        (Vector3 bmin, Vector3 bmax) = GeometryBounds(octa);
        Vector3 bsize = bmax - bmin;
        float extent = Mathf.Max(bsize.X, Mathf.Max(bsize.Y, bsize.Z));
        GD.Print($"[radiance] {mapName}: worldsize={octa.WorldSize} bounds={bsize} lights={lights.Count}");

        Warmup(octa, bmin, extent);

        foreach (int n in Grids)
        {
            int total = n * n * n;
            // Sample on an evenly-spread m³ lattice. A single-axis stride aliases on
            // axis-aligned geometry (a wall every 4th cell looks all-solid), so the
            // solid fraction is measured on a 3D sub-lattice and extrapolated.
            int m = Math.Max(1, (int)Math.Round(Math.Cbrt(MaxSampleProbes)));
            int sampledCount = m * m * m;
            int validSample = 0;
            var probes = new List<Vector3>();

            foreach (Vector3 p in SampleProbeCenters(n, m, bmin, extent / n))
            {
                Cube? c = octa.LeafCube(new Vector3I((int)p.X, (int)p.Y, (int)p.Z));
                if (c != null && !c.IsEntirelySolid())
                {
                    validSample++;
                    probes.Add(p);
                }
            }

            double validFrac = sampledCount > 0 ? (double)validSample / sampledCount : 0;
            long validTotal = (long)Math.Round(total * validFrac);
            TestAssert.True(probes.Count > 0, $"{mapName} {n}^3: at least one air probe");

            float cell = extent / n;
            long memBytes = (long)total * BytesPerTexel * TextureCount;

            // A ray leaving the geometry bounds sees sky; cap it a little beyond.
            float maxRay = extent * 1.5f;

            // ---- sky/hemisphere pass ----
            var dirs = new Vector3[SkyRays];
            for (int i = 0; i < SkyRays; i++) dirs[i] = CosineHemisphere(i, SkyRays);

            int rays = 0, sky = 0;
            var swSky = Stopwatch.StartNew();
            foreach (Vector3 p in probes)
            {
                foreach (Vector3 d in dirs)
                {
                    float dist = OctaRaycast.RayCube(octa, p, d, maxRay, 0, 0, out _);
                    rays++;
                    if (dist >= maxRay * 0.999f) sky++;
                }
            }
            swSky.Stop();

            double nsPerRay = rays > 0 ? swSky.Elapsed.TotalMilliseconds * 1e6 / rays : 0;
            double skyMs = validTotal * SkyRays * nsPerRay / 1e6;
            double skyFrac = rays > 0 ? (double)sky / rays : 0;

            // ---- direct-light pass ----
            long inRange = 0;
            var swDirect = Stopwatch.StartNew();
            foreach (Vector3 p in probes)
            {
                foreach ((Vector3 lp, float lr) in lights)
                {
                    if (p.DistanceSquaredTo(lp) > lr * lr) continue;
                    inRange++;
                    Vector3 d = lp - p;
                    float len = d.Length();
                    if (len > 0f) OctaRaycast.RayCube(octa, p, d / len, len, 0, 0, out _);
                }
            }
            swDirect.Stop();

            double directMs = probes.Count > 0
                ? validTotal * swDirect.Elapsed.TotalMilliseconds / probes.Count
                : 0;
            double avgInRange = probes.Count > 0 ? (double)inRange / probes.Count : 0;

            // The real-time metric: one 128u chunk (WorldChunkRenderer default)
            // rebaked in isolation. Full-map bake is a one-time load; this is what
            // an editor edit costs.
            double chunkCells = Math.Pow(128.0 / cell, 3) * validFrac;
            double perEditSkyMs = chunkCells * SkyRays * nsPerRay / 1e6;
            double perEditDirectMs = probes.Count > 0
                ? chunkCells * swDirect.Elapsed.TotalMilliseconds / probes.Count
                : 0;

            GD.Print(
                $"[radiance]   {n,3}^3 cell={cell,6:F1}u valid={validTotal,9} ({validFrac,4:P0} of {sampledCount}) " +
                $"mem={memBytes / 1024.0 / 1024.0,5:F1}MB  " +
                $"ray={nsPerRay,5:F0}ns sky={skyFrac,4:P0}  " +
                $"fullBake={skyMs / 1000.0,6:F2}s direct={directMs / 1000.0,6:F2}s  " +
                $"edit128={perEditSkyMs,5:F1}/{perEditDirectMs,4:F1}ms avgInRange={avgInRange,5:F2}");
        }
    }

    /// <summary>A short mixed-direction ray loop to warm tiered JIT before timing.</summary>
    private static void Warmup(Octa octa, Vector3 origin, float extent)
    {
        float maxRay = extent * 1.5f;
        for (int i = 0; i < 20000; i++)
        {
            float t = i * 0.61803f;
            var d = new Vector3(
                Mathf.Cos(t) * Mathf.Sin(t * 0.7f),
                Mathf.Sin(t) * Mathf.Sin(t * 0.7f),
                Mathf.Cos(t * 0.7f));
            var p = origin + new Vector3(
                (i * 37 % 1000) / 1000f * extent,
                (i * 91 % 1000) / 1000f * extent,
                (i * 53 % 1000) / 1000f * extent);
            OctaRaycast.RayCube(octa, p, d, maxRay, 0, 0, out _);
        }
    }

    /// <summary>The map's point lights: every <c>ET_LIGHT</c> with its attenuation radius.</summary>
    private static List<(Vector3 Pos, float Radius)> CollectLights(OgzMap map)
    {
        var lights = new List<(Vector3, float)>();
        foreach (var (_, e) in map.OfType(EntityType.Light))
            lights.Add((e.Position, e.Attr1 > 0 ? e.Attr1 : InfiniteRange));
        return lights;
    }

    /// <summary>
    /// A spread <paramref name="m"/>³ lattice of cell centres on the
    /// <paramref name="n"/>³ grid, covering the geometry cube whose corner is
    /// <paramref name="origin"/> with cell edge <paramref name="cell"/>. Even in all
    /// three axes, so the solid fraction is unbiased (see <see cref="Measure"/>).
    /// </summary>
    private static IEnumerable<Vector3> SampleProbeCenters(int n, int m, Vector3 origin, float cell)
    {
        for (int sz = 0; sz < m; sz++)
            for (int sy = 0; sy < m; sy++)
                for (int sx = 0; sx < m; sx++)
                {
                    int x = Math.Min(n - 1, sx * n / m);
                    int y = Math.Min(n - 1, sy * n / m);
                    int z = Math.Min(n - 1, sz * n / m);
                    yield return origin + new Vector3((x + 0.5f) * cell, (y + 0.5f) * cell, (z + 0.5f) * cell);
                }
    }

    /// <summary>
    /// AABB of all non-empty geometry (Sauerbraten coords), walked from the octree
    /// roots. Empty subtrees are skipped, so this is the built region the probe
    /// grid should actually span — not the full (mostly empty) world cube.
    /// </summary>
    private static (Vector3 Min, Vector3 Max) GeometryBounds(Octa octa)
    {
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        Walk(octa.WorldRoot, 0, 0, 0, octa.WorldSize / 2);
        return any ? (min, max) : (Vector3.Zero, Vector3.One);

        void Walk(Cube[] cubes, int x, int y, int z, int size)
        {
            for (int i = 0; i < 8; i++)
            {
                Cube c = cubes[i];
                int cx = x + ((i & 1) != 0 ? size : 0);
                int cy = y + ((i & 2) != 0 ? size : 0);
                int cz = z + ((i & 4) != 0 ? size : 0);
                if (c.Children != null) { Walk(c.Children, cx, cy, cz, size >> 1); continue; }
                if (c.IsEmpty()) continue;
                any = true;
                min = min.Min(new Vector3(cx, cy, cz));
                max = max.Max(new Vector3(cx + size, cy + size, cz + size));
            }
        }
    }

    /// <summary>
    /// A cosine-weighted hemisphere direction around +Z (Sauerbraten up), using a
    /// golden-angle spiral. Cosine weighting matches the diffuse irradiance the
    /// probe stores, so the sky pass is a direct Monte Carlo estimate.
    /// </summary>
    private static Vector3 CosineHemisphere(int i, int n)
    {
        float r = Mathf.Sqrt((i + 0.5f) / n);
        float phi = i * 2.39996323f; // golden angle
        return new Vector3(r * Mathf.Cos(phi), r * Mathf.Sin(phi), Mathf.Sqrt(Mathf.Max(0f, 1f - r * r)));
    }
}
