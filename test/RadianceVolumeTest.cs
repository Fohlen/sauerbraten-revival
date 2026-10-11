using System;
using System.Runtime.InteropServices;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Lighting;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Unit tests for the core radiance data structures: <see cref="RadianceProbe"/>
/// and <see cref="RadianceGrid"/> (docs/radiance-hints.md §2, §3, §10).
/// </summary>
public class RadianceVolumeTest : GoTestBase
{
    public RadianceVolumeTest(Node testScene) : base(testScene) { }

    // =========================================================================
    // L1 Spherical Harmonics Math (§2.1, §10)
    // =========================================================================

    [Test]
    public void L1SHMath_DirectionalProjection_MatchesTheoreticalIrradiance()
    {
        // Directional light with radiance L = 4.0 arriving from +Z (facing towards light = +Z).
        float radiance = 4.0f;
        var lightDir = new Vector3(0, 0, 1);

        var probe = RadianceProbe.Zero;
        probe.AddDirectional(lightDir, radiance);

        // Folded coefficients:
        // c0 = 0.25 * L = 1.0
        // c1 = 0.5 * L * lightDir = (0, 0, 2.0)
        TestAssert.True(Mathf.Abs(probe.Amb - 1.0f) < 1e-5f, $"c0 ambient should be 1.0, was {probe.Amb}");
        TestAssert.True(probe.Dir.IsEqualApprox(new Vector3(0, 0, 2.0f)), $"c1 directional should be (0, 0, 2), was {probe.Dir}");

        // Normal facing directly toward light (n = +Z):
        // E(n) = c0 + dot(c1, n) = 1.0 + 2.0 = 3.0 = 0.75 * L
        float eFacing = probe.Evaluate(new Vector3(0, 0, 1));
        TestAssert.True(Mathf.Abs(eFacing - 3.0f) < 1e-5f, $"Facing light irradiance should be 3.0, was {eFacing}");

        // Normal pointing directly away from light (n = -Z):
        // E(n) = c0 + dot(c1, -n) = 1.0 - 2.0 = -1.0 = -0.25 * L
        float eAway = probe.Evaluate(new Vector3(0, 0, -1));
        TestAssert.True(Mathf.Abs(eAway - (-1.0f)) < 1e-5f, $"Facing away irradiance should be -1.0, was {eAway}");

        // Clamped evaluation:
        float eClamped = probe.EvaluateClamped(new Vector3(0, 0, -1));
        TestAssert.True(eClamped == 0.0f, $"Clamped negative irradiance should be 0, was {eClamped}");

        // Orthogonal normal (n = +X):
        // E(n) = c0 + dot(c1, n) = 1.0 + 0 = 1.0 = 0.25 * L
        float eOrthogonal = probe.Evaluate(new Vector3(1, 0, 0));
        TestAssert.True(Mathf.Abs(eOrthogonal - 1.0f) < 1e-5f, $"Orthogonal irradiance should be 1.0, was {eOrthogonal}");
    }

    [Test]
    public void L1SHMath_Superposition_AddsMultipleLights()
    {
        var probe = RadianceProbe.Zero;
        // Light 1 from +X, radiance 2
        probe.AddDirectional(new Vector3(1, 0, 0), 2.0f);
        // Light 2 from +Y, radiance 2
        probe.AddDirectional(new Vector3(0, 1, 0), 2.0f);

        // c0 = 0.25 * 2 + 0.25 * 2 = 1.0
        // c1 = 0.5 * 2 * (1, 0, 0) + 0.5 * 2 * (0, 1, 0) = (1, 1, 0)
        TestAssert.True(Mathf.Abs(probe.Amb - 1.0f) < 1e-5f, "Superposition c0 should be 1.0");
        TestAssert.True(probe.Dir.IsEqualApprox(new Vector3(1, 1, 0)), "Superposition c1 should be (1, 1, 0)");

        // Diagonal normal (1/√2, 1/√2, 0)
        float invSqrt2 = 1.0f / Mathf.Sqrt(2f);
        float eDiag = probe.Evaluate(new Vector3(invSqrt2, invSqrt2, 0));
        float expected = 1.0f + 2.0f * invSqrt2;
        TestAssert.True(Mathf.Abs(eDiag - expected) < 1e-4f, $"Diagonal irradiance should be {expected}, was {eDiag}");
    }

    [Test]
    public void L1SHMath_TintedEvaluation_ScalesAmbientOnly()
    {
        var probe = new RadianceProbe(new Vector3(0, 1, 0), 2.0f);
        var skyTint = new Color(0.5f, 0.8f, 1.0f);

        // Facing light (n = +Y): dot(c1, n) = 1.0
        // irr = skyTint * amb + dot = (0.5 * 2 + 1, 0.8 * 2 + 1, 1.0 * 2 + 1) = (2.0, 2.6, 3.0)
        Color irrFacing = probe.Evaluate(new Vector3(0, 1, 0), skyTint);
        TestAssert.True(Mathf.Abs(irrFacing.R - 2.0f) < 1e-5f, $"R channel should be 2.0, was {irrFacing.R}");
        TestAssert.True(Mathf.Abs(irrFacing.G - 2.6f) < 1e-5f, $"G channel should be 2.6, was {irrFacing.G}");
        TestAssert.True(Mathf.Abs(irrFacing.B - 3.0f) < 1e-5f, $"B channel should be 3.0, was {irrFacing.B}");
    }

    [Test]
    public void L1SHMath_ProbeLerp_InterpolatesLinearCoefficients()
    {
        var a = new RadianceProbe(new Vector3(0, 0, 0), 0.0f);
        var b = new RadianceProbe(new Vector3(2, 4, 6), 10.0f);

        var mid = RadianceProbe.Lerp(a, b, 0.5f);
        TestAssert.True(mid.Dir.IsEqualApprox(new Vector3(1, 2, 3)), "Midpoint Dir should be (1, 2, 3)");
        TestAssert.True(Mathf.Abs(mid.Amb - 5.0f) < 1e-5f, "Midpoint Amb should be 5.0");

        // Evaluating lerped probe at normal (0, 0, 1): 5.0 + 3 = 8.0
        TestAssert.True(Mathf.Abs(mid.Evaluate(new Vector3(0, 0, 1)) - 8.0f) < 1e-5f, "Midpoint evaluate should be 8.0");
    }

    // =========================================================================
    // Coordinate conversions (§3)
    // =========================================================================

    [Test]
    public void Coordinates_SauerbratenAndGodotWorldSpaceRoundTrip()
    {
        var sauerPoint = new Vector3(123.4f, 567.8f, 910.1f);
        Vector3 worldPoint = RadianceGrid.SauerToWorld(sauerPoint);
        Vector3 roundTrip = RadianceGrid.WorldToSauer(worldPoint);

        TestAssert.True(sauerPoint.IsEqualApprox(roundTrip), "Sauer <-> World point roundtrip failed");

        // Axis orientations:
        // Sauerbraten +Z (Up) -> Godot +Y (Up)
        TestAssert.True(RadianceGrid.SauerToWorld(new Vector3(0, 0, 1)).IsEqualApprox(new Vector3(0, 1, 0)), "+Z Up -> +Y Up");
        // Sauerbraten +Y (Forward) -> Godot -Z (Forward)
        TestAssert.True(RadianceGrid.SauerToWorld(new Vector3(0, 1, 0)).IsEqualApprox(new Vector3(0, 0, -1)), "+Y Forward -> -Z Forward");
        // Sauerbraten +X (Right) -> Godot +X (Right)
        TestAssert.True(RadianceGrid.SauerToWorld(new Vector3(1, 0, 0)).IsEqualApprox(new Vector3(1, 0, 0)), "+X Right -> +X Right");

        // Bounds transformation:
        var sBounds = new Aabb(new Vector3(10, 20, 30), new Vector3(100, 200, 300));
        Aabb wBounds = RadianceGrid.SauerBoundsToWorld(sBounds);
        // Dimensions must be preserved (permuted):
        TestAssert.True(Mathf.Abs(wBounds.Size.X - 100f) < 1e-5f, "X dimension preserved");
        TestAssert.True(Mathf.Abs(wBounds.Size.Y - 300f) < 1e-5f, "Y dimension (Sauer Z) preserved");
        TestAssert.True(Mathf.Abs(wBounds.Size.Z - 200f) < 1e-5f, "Z dimension (Sauer Y) preserved");
    }

    // =========================================================================
    // Grid geometry and coordinates (§3)
    // =========================================================================

    [Test]
    public void Grid_CoordinatesAndUvwRoundTrip()
    {
        var grid = new RadianceGrid(new Vector3(10, 20, 30), 64.0f, 16);
        TestAssert.Equal(16, grid.Resolution, "Resolution should be 16");
        TestAssert.Equal(16 * 16 * 16, grid.TotalProbes, "Total probes 4096");
        TestAssert.True(Mathf.Abs(grid.CellSize - 4.0f) < 1e-5f, "Cell size should be 4.0");

        // UVW roundtrip:
        var worldPos = new Vector3(10 + 32, 20 + 16, 30 + 48);
        Vector3 uvw = grid.WorldToUvw(worldPos);
        TestAssert.True(uvw.IsEqualApprox(new Vector3(0.5f, 0.25f, 0.75f)), "WorldToUvw mapping");

        Vector3 reconstructedWorld = grid.UvwToWorld(uvw);
        TestAssert.True(worldPos.IsEqualApprox(reconstructedWorld), "UvwToWorld mapping roundtrip");

        // Cell indexing roundtrip:
        for (int z = 0; z < grid.Resolution; z += 5)
        {
            for (int y = 0; y < grid.Resolution; y += 5)
            {
                for (int x = 0; x < grid.Resolution; x += 5)
                {
                    int idx = grid.Index(x, y, z);
                    Vector3I cell = grid.CellFromIndex(idx);
                    TestAssert.Equal(x, cell.X, "Cell X roundtrip");
                    TestAssert.Equal(y, cell.Y, "Cell Y roundtrip");
                    TestAssert.Equal(z, cell.Z, "Cell Z roundtrip");

                    // Cell center inside the cell:
                    Vector3 center = grid.CellCenterWorld(x, y, z);
                    Vector3I mappedCell = grid.WorldToCell(center);
                    TestAssert.Equal(cell, mappedCell, "CellCenterWorld maps back to same cell");
                }
            }
        }
    }

    [Test]
    public void Grid_FromWorldBounds_CreatesPaddedCubicGrid()
    {
        // Non-cubic world bounds: size = (600, 200, 400), center = (300, 100, 200)
        var bounds = new Aabb(new Vector3(0, 0, 0), new Vector3(600, 200, 400));
        var grid = RadianceGrid.FromWorldBounds(bounds, resolution: 32, marginCells: 1);

        // Grid must be cubic:
        TestAssert.Equal(32, grid.Resolution, "Resolution is 32");
        TestAssert.True(grid.Bounds.Size.X == grid.Bounds.Size.Y && grid.Bounds.Size.Y == grid.Bounds.Size.Z, "Grid must be cubic");

        // Inner cells along max axis = 30; cell size = 600 / 30 = 20; total extent = 32 * 20 = 640
        TestAssert.True(Mathf.Abs(grid.CellSize - 20.0f) < 1e-4f, $"Cell size should be 20, was {grid.CellSize}");
        TestAssert.True(Mathf.Abs(grid.Extent - 640.0f) < 1e-4f, $"Extent should be 640, was {grid.Extent}");

        // Geometry bounds must be strictly contained inside the grid:
        TestAssert.True(grid.Bounds.Encloses(bounds), "Grid bounds must enclose geometry");

        // Center must match geometry center:
        TestAssert.True(grid.Bounds.GetCenter().IsEqualApprox(bounds.GetCenter()), "Grid center must match geometry center");
    }

    [Test]
    public void Grid_FromOcta_SpansGeometryBounds()
    {
        // Test with a real map's geometry (church51):
        OgzMap map = TestData.ReadMap("church51");
        Octa octa = map.ToOcta();

        Aabb geomBounds = RadianceGrid.CalculateGeometryBounds(octa);
        TestAssert.True(geomBounds.Size.Length() > 100f, "Church51 geometry bounds non-empty");

        var grid = RadianceGrid.FromOcta(octa, resolution: 32, marginCells: 1);
        TestAssert.Equal(32, grid.Resolution, "Grid resolution 32");
        TestAssert.True(grid.Extent > 0f, "Grid extent positive");
        TestAssert.Equal(32768, grid.TotalProbes, "32^3 = 32768 probes");

        // Transforming geometry bounds to world space must be enclosed by grid bounds:
        Aabb worldGeom = RadianceGrid.SauerBoundsToWorld(geomBounds);
        TestAssert.True(grid.Bounds.Encloses(worldGeom), "Grid encloses church51 geometry");
    }

    [Test]
    public void Grid_TrilinearSample_InterpolatesProbes()
    {
        var grid = new RadianceGrid(Vector3.Zero, 10.0f, 2); // 2x2x2 grid
        // Probe (0, 0, 0) has Amb = 1.0
        // Probe (1, 0, 0) has Amb = 3.0
        // Others 0
        grid[0, 0, 0] = new RadianceProbe(Vector3.Zero, 1.0f);
        grid[1, 0, 0] = new RadianceProbe(Vector3.Zero, 3.0f);

        // Probe centers:
        // (0,0,0) center is at uvw (0.25, 0.25, 0.25)
        // (1,0,0) center is at uvw (0.75, 0.25, 0.25)
        // Exactly halfway between them at uvw (0.5, 0.25, 0.25):
        RadianceProbe sampled = grid.SampleUvw(new Vector3(0.5f, 0.25f, 0.25f));
        TestAssert.True(Mathf.Abs(sampled.Amb - 2.0f) < 1e-4f, $"Interpolated Amb should be 2.0, was {sampled.Amb}");
    }

    // =========================================================================
    // ImageTexture3D generation & update smoke tests (§2.2, §10)
    // =========================================================================

    [Test]
    public void Texture3D_CreatesAndUpdatesRgbah3DTexture()
    {
        int res = 8;
        var grid = new RadianceGrid(Vector3.Zero, 8.0f, res);

        // Populate a few probes with known signed directional vectors and ambient values:
        grid[0, 0, 0] = new RadianceProbe(new Vector3(1.5f, -2.5f, 0.75f), 3.25f);
        grid[7, 7, 7] = new RadianceProbe(new Vector3(-0.5f, 4.0f, -1.0f), 0.5f);

        // Build 2D depth slice images:
        var images = grid.BuildImages();
        TestAssert.Equal(res, images.Count, "Number of images equals grid depth resolution");

        // Verify image slice properties:
        Image slice0 = images[0];
        TestAssert.Equal(res, slice0.GetWidth(), "Slice image width");
        TestAssert.Equal(res, slice0.GetHeight(), "Slice image height");
        TestAssert.Equal(Image.Format.Rgbah, slice0.GetFormat(), "Slice format must be Rgbah");

        // Verify half-float values in first slice:
        byte[] data = slice0.GetData();
        Span<Half> halves = MemoryMarshal.Cast<byte, Half>(data.AsSpan());
        // Texel (0, 0) in slice 0 corresponds to probe [0, 0, 0]:
        TestAssert.True(Mathf.Abs((float)halves[0] - 1.5f) < 1e-2f, "R channel (c1.X)");
        TestAssert.True(Mathf.Abs((float)halves[1] - (-2.5f)) < 1e-2f, "G channel (c1.Y, signed negative)");
        TestAssert.True(Mathf.Abs((float)halves[2] - 0.75f) < 1e-2f, "B channel (c1.Z)");
        TestAssert.True(Mathf.Abs((float)halves[3] - 3.25f) < 1e-2f, "A channel (c0 scalar ambient)");

        // Create the 3D texture:
        ImageTexture3D tex = grid.CreateTexture();
        TestAssert.True(tex != null, "ImageTexture3D created");
        ArgumentNullException.ThrowIfNull(tex);
        TestAssert.Equal(res, tex.GetWidth(), "Texture width");
        TestAssert.Equal(res, tex.GetHeight(), "Texture height");
        TestAssert.Equal(res, tex.GetDepth(), "Texture depth");
        TestAssert.Equal(Image.Format.Rgbah, tex.GetFormat(), "Texture format");

        // Update the texture with modified probe data:
        grid[0, 0, 0] = new RadianceProbe(new Vector3(5.0f, 5.0f, 5.0f), 10.0f);
        grid.UpdateTexture(tex);
        TestAssert.Equal(res, tex.GetWidth(), "Texture width preserved after update");
    }
}
