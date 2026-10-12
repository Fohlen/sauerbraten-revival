using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Lighting;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Unit tests for the core radiance data structures: <see cref="RadianceProbe"/>
/// and <see cref="RadianceGrid"/> (docs/adr/0002-radiance-hints.md §2, §3, §10).
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

    // =========================================================================
    // RadianceVolume & Shader Tests (Stage 1)
    // =========================================================================

    [Test]
    public void RadianceShader_LoadsSuccessfully()
    {
        var shader = ResourceLoader.Load<Shader>(MaterialBuilder.RadianceShaderPath);
        TestAssert.True(shader != null, "radiance.gdshader loads");
    }

    [Test]
    public void RadianceVolume_DecodeColor_HandlesPackedRgbAndGrayscale()
    {
        // 0xFF8040 -> R=1.0, G=0.502, B=0.251
        Color c1 = RadianceVolume.DecodeColor(0xFF8040);
        TestAssert.True(Mathf.Abs(c1.R - 1.0f) < 1e-2f, "Red decoded");
        TestAssert.True(Mathf.Abs(c1.G - 0.502f) < 1e-2f, "Green decoded");
        TestAssert.True(Mathf.Abs(c1.B - 0.251f) < 1e-2f, "Blue decoded");

        // Engine grayscale byte convention: <= 255 expands to (c, c, c)
        Color cGray = RadianceVolume.DecodeColor(128);
        TestAssert.True(Mathf.Abs(cGray.R - (128f / 255f)) < 1e-3f, "Grayscale red");
        TestAssert.True(Mathf.Abs(cGray.G - (128f / 255f)) < 1e-3f, "Grayscale green");
        TestAssert.True(Mathf.Abs(cGray.B - (128f / 255f)) < 1e-3f, "Grayscale blue");
    }

    [Test]
    public void RadianceVolume_CalculateSunlightDirSauer_MatchesSphericalYawPitch()
    {
        // Sunlight directly overhead: pitch = 90
        Vector3 overhead = RadianceVolume.CalculateSunlightDirSauer(0, 90);
        TestAssert.True(overhead.IsEqualApprox(new Vector3(0, 0, 1)), $"Overhead sun should be +Z, was {overhead}");

        // Sunlight from yaw 0 (Sauerbraten +Y forward), pitch 0 (horizon)
        // vec(yaw=0, pitch=0) = (-sin(0)*cos(0), cos(0)*cos(0), sin(0)) = (0, 1, 0)
        Vector3 horizonNorth = RadianceVolume.CalculateSunlightDirSauer(0, 0);
        TestAssert.True(horizonNorth.IsEqualApprox(new Vector3(0, 1, 0)), $"Yaw 0 pitch 0 should be +Y, was {horizonNorth}");

        // Sunlight from yaw 90 (Sauerbraten -X), pitch 0
        // vec(yaw=90, pitch=0) = (-sin(90), cos(90), 0) = (-1, 0, 0)
        Vector3 horizonEast = RadianceVolume.CalculateSunlightDirSauer(90, 0);
        TestAssert.True(horizonEast.IsEqualApprox(new Vector3(-1, 0, 0)), $"Yaw 90 pitch 0 should be -X, was {horizonEast}");
    }

    [Test]
    public void RadianceVolume_BakeSkyAndSun_BakesOpenAirProbe()
    {
        // Create an empty octree world (size 64 = scale 6)
        var octa = new Octa(6);
        octa.EmptyMap(6);

        var volume = new RadianceVolume(new RadianceGrid(new Vector3(0, 0, 0), 64f, 4));
        volume.SkyRays = 8;

        var config = new MapConfig
        {
            Ambient = 0x191919, // ~0.1
            Skylight = 0x808080, // ~0.5
            Sunlight = 0xFFFFFF, // 1.0
            SunlightScale = 1.0f,
            SunlightYaw = 0,
            SunlightPitch = 90, // straight down
        };

        volume.BakeSkyAndSun(octa, config);

        TestAssert.True(volume.Texture != null, "3D texture generated after bake");

        // The center probe in an empty map should see full sky and full sun
        var centerProbe = volume.Grid[2, 2, 2];
        TestAssert.True(centerProbe.Amb > 0f, "Ambient irradiance should be accumulated");
        TestAssert.True(centerProbe.Dir.Length() > 0f, "Directional irradiance should have sun/sky component");
    }

    [Test]
    public void MaterialBuilder_BuildsRadianceMaterials()
    {
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePath);
        var textures = new DumpTextureSource(TestData.ReferenceRoot);
        var octa = new Octa(6);
        octa.EmptyMap(6);

        var volume = new RadianceVolume(new RadianceGrid(Vector3.Zero, 64f, 4));
        volume.SkyRays = 4;
        volume.BakeSkyAndSun(octa, catalogue.Config);

        var set = MaterialBuilder.Build(catalogue, textures, volume);
        TestAssert.True(set != null, "MaterialSet built");

        int shaderMats = 0;
        foreach (var pair in set!.Materials)
        {
            if (pair.Value is ShaderMaterial sm && sm.Shader != null)
            {
                shaderMats++;
                TestAssert.True(sm.GetShaderParameter("use_radiance").AsBool(), "Radiance enabled");
                TestAssert.True(sm.GetShaderParameter("radiance_tex").Obj == volume.Texture, "Radiance 3D texture bound");
            }
        }

        TestAssert.True(shaderMats > 0, "Materials are ShaderMaterials bound with radiance");
    }

    // =========================================================================
    // Stage 2 Direct Entity Lights & Spatial Index Tests (§4.2)
    // =========================================================================

    [Test]
    public void LightSpatialIndex_PrunesLightsOutsideCellInfluence()
    {
        var bounds = new Aabb(new Vector3(0, 0, 0), new Vector3(256, 256, 256));
        var lights = new List<DirectLightSource>
        {
            // Light 0 in corner (0, 0, 0) with radius 50
            new DirectLightSource(new Vector3(10, 10, 10), 50f, Colors.Red),
            // Light 1 far away in opposite corner (200, 200, 200) with radius 30
            new DirectLightSource(new Vector3(200, 200, 200), 30f, Colors.Blue),
        };

        var index = new LightSpatialIndex(bounds, lights, targetCellSize: 64f);
        var queryResults = new List<DirectLightSource>();

        // Query near Light 0
        index.QueryLights(new Vector3(10, 10, 10), queryResults);
        TestAssert.Equal(1, queryResults.Count, "Only light 0 in range");
        TestAssert.Equal(Colors.Red, queryResults[0].Color, "Result is light 0");

        // Query in middle (100, 100, 100) - neither light reaches
        index.QueryLights(new Vector3(100, 100, 100), queryResults);
        TestAssert.Equal(0, queryResults.Count, "No lights in middle cell");

        // Query near Light 1
        index.QueryLights(new Vector3(200, 200, 200), queryResults);
        TestAssert.Equal(1, queryResults.Count, "Only light 1 in range");
        TestAssert.Equal(Colors.Blue, queryResults[0].Color, "Result is light 1");
    }

    [Test]
    public void RadianceVolume_BakeDirectLights_IlluminatesProbeFromNearbyLight()
    {
        var octa = new Octa(10);
        octa.EmptyMap(10); // scale 10: worldsize 1024. Low-Z (z < 512) is solid floor, high-Z (z >= 512) is air.

        // Grid covering [0, 512] in Sauer X and Y, and [512, 1024] in Sauer Z (all air).
        // In Sauer space: pos = (0, 0, 512), size = 512.
        // In Godot space:
        // x = 0, y = 512, z = -512
        var sauerBounds = new Aabb(new Vector3(0, 0, 512f), new Vector3(512f, 512f, 512f));
        var grid = RadianceGrid.FromSauerBounds(sauerBounds, resolution: 4, marginCells: 0);
        var volume = new RadianceVolume(grid);
        volume.SkyRays = 4;

        // Map config with no sun and minimal ambient so direct light dominates
        var config = new MapConfig
        {
            Ambient = 0,
            Skylight = 0,
            Sunlight = 0,
        };

        // Light placed at Sauer (256, 256, 768) with radius 200
        var lights = new List<DirectLightSource>
        {
            new DirectLightSource(new Vector3(256, 256, 768), 200f, Colors.White)
        };

        volume.Bake(octa, config, lights);

        // A probe close to the light should have high irradiance
        Vector3 probeWorld = RadianceGrid.SauerToWorld(new Vector3(256, 256, 760));
        var probeClose = volume.Grid.Sample(probeWorld);
        TestAssert.True(probeClose.Dir.Length() > 0f || probeClose.Amb > 0f, "Close probe receives light energy");
    }

    // =========================================================================
    // Stage 3 Indirect Bounce & Ambient Occlusion Tests (§4.3)
    // =========================================================================

    [Test]
    public void RadianceVolume_BakeIndirectBounce_ReceivesReflectedDiffuseFromFloor()
    {
        // World with solid floor at low Z (< 512) and air at high Z (>= 512)
        var octa = new Octa(10);
        octa.EmptyMap(10);

        // Sunlight shining straight down (+Z pitch = 90)
        var config = new MapConfig
        {
            Ambient = 0,
            Skylight = 0,
            Sunlight = 0xFFFFFF, // Full white sun
            SunlightScale = 1.0f,
            SunlightYaw = 0,
            SunlightPitch = 90,
        };

        // Grid covering air space near floor [512, 600]
        var sauerBounds = new Aabb(new Vector3(256, 256, 512), new Vector3(64, 64, 64));
        var grid = RadianceGrid.FromSauerBounds(sauerBounds, resolution: 4, marginCells: 0);
        var volume = new RadianceVolume(grid) { SkyRays = 8 };

        // Test with catalogue containing a bright vslot
        var catalogue = new MetadataCatalogue();
        catalogue.VSlots[1] = new CatalogueVSlot
        {
            Index = 1,
            Slot = 1,
            Color = new Vector3(1f, 1f, 1f) // 100% white diffuse albedo
        };

        volume.Bake(octa, config, directLights: null, catalogue: catalogue);

        // Probes above the lit floor should receive indirect bounce from the floor
        var probe = volume.Grid[2, 2, 0];
        TestAssert.True(probe.Amb > 0f, $"Probe above floor should receive reflected bounce, Amb was {probe.Amb}");
    }

    [Test]
    public void RadianceVolume_AmbientOcclusion_EnclosedProbeHasLowerAmbientThanOpenSky()
    {
        // 1. World with open air in upper half
        var octaOpen = new Octa(10);
        octaOpen.EmptyMap(10);

        // 2. World with ceiling (all high-Z octants 4, 5, 6, 7 are solid)
        var octaCeiling = new Octa(10);
        octaCeiling.EmptyMap(10);
        // Make octant 4 solid: covers Sauer [0..512, 0..512, 512..1024]
        octaCeiling.WorldRoot[4].SolidFaces();

        var config = new MapConfig
        {
            Ambient = 0,
            Skylight = 0xFFFFFF, // 1.0 skylight
            Sunlight = 0,
        };

        // Open air probe in octaOpen at (600, 256, 600)
        var sauerBoundsOpen = new Aabb(new Vector3(600, 256, 600), new Vector3(32, 32, 32));
        var gridOpen = RadianceGrid.FromSauerBounds(sauerBoundsOpen, resolution: 2, marginCells: 0);
        var volOpen = new RadianceVolume(gridOpen) { SkyRays = 16 };
        volOpen.Bake(octaOpen, config);

        // Probe under ceiling in octaCeiling at (256, 256, 600)
        // Wait, octant 4 is solid [0..512, 0..512, 512..1024], so (256, 256, 600) is inside solid octant 4!
        // Instead: in octaCeiling, make octants 4, 5 solid, but probe is at (600, 256, 600) in air, with ceiling next to it,
        // or probe at (600, 256, 520) with low sky visibility.
        // Even simpler: probe in open space vs probe with an overhead ceiling:
        // In a world of scale 10, octant 0..3 are floor [0..1024, 0..1024, 0..512].
        // If we compare probe right next to a high wall or under a ceiling:
        // In octaOpen: probe at (600, 256, 600) has empty space all around.
        // In octaWall: octant 5 [512..1024, 0..512, 512..1024] is air. Octants 4, 6, 7 are solid walls.
        var octaOccluded = new Octa(10);
        octaOccluded.EmptyMap(10);
        octaOccluded.WorldRoot[4].SolidFaces();
        octaOccluded.WorldRoot[6].SolidFaces();
        octaOccluded.WorldRoot[7].SolidFaces();

        // Probe at (600, 256, 520) in octaOccluded is surrounded on 3 sides by solid blocks
        var sauerBoundsOccluded = new Aabb(new Vector3(520, 256, 520), new Vector3(32, 32, 32));
        var gridOccluded = RadianceGrid.FromSauerBounds(sauerBoundsOccluded, resolution: 2, marginCells: 0);
        var volOccluded = new RadianceVolume(gridOccluded) { SkyRays = 16 };
        volOccluded.Bake(octaOccluded, config);

        var probeOccluded = volOccluded.Grid[0, 0, 0];
        var probeOpen = volOpen.Grid[0, 0, 0];

        TestAssert.True(probeOpen.Amb > probeOccluded.Amb,
            $"Open sky probe should have higher ambient irradiance than corner/occluded probe. Open={probeOpen.Amb}, Occluded={probeOccluded.Amb}");
    }

    [Test]
    public void RadianceVolume_AlbedoLookup_ResolvesCatalogueAndDefault()
    {
        var catalogue = new MetadataCatalogue();
        catalogue.VSlots[5] = new CatalogueVSlot
        {
            Index = 5,
            Slot = 5,
            Color = new Vector3(0.8f, 0.4f, 0.2f) // Average = (0.8+0.4+0.2)/3 = 0.4667
        };

        float albedo5 = RadianceVolume.ComputeVSlotAlbedo(5, catalogue, null);
        TestAssert.True(Mathf.Abs(albedo5 - 0.4667f) < 0.01f, $"Computed albedo should match vslot color average, was {albedo5}");

        // Unknown vslot should return DefaultAlbedo (0.5)
        float albedoUnknown = RadianceVolume.ComputeVSlotAlbedo(999, catalogue, null);
        TestAssert.Equal(RadianceVolume.DefaultAlbedo, albedoUnknown, "Unknown vslot returns DefaultAlbedo");

        // Null catalogue should return DefaultAlbedo
        float albedoNullCat = RadianceVolume.ComputeVSlotAlbedo(5, null, null);
        TestAssert.Equal(RadianceVolume.DefaultAlbedo, albedoNullCat, "Null catalogue returns DefaultAlbedo");
    }

    // =========================================================================
    // Stage 4: Incremental Updates & Editor Dirty Wiring
    // =========================================================================

    [Test]
    public void RadianceVolume_MarkDirty_EnqueuesExactProbeRegion()
    {
        var bounds = new Aabb(Vector3.Zero, new Vector3(64, 64, 64));
        var grid = RadianceGrid.FromSauerBounds(bounds, resolution: 4, marginCells: 0);
        var volume = new RadianceVolume(grid);

        TestAssert.Equal(0, volume.PendingCount, "Initially no dirty probes");

        // Mark a region covering a subset of probes
        // In Sauer space: AABB (0, 0, 0) to (32, 32, 32)
        volume.MarkDirty(new Aabb(Vector3.Zero, new Vector3(32, 32, 32)));

        TestAssert.True(volume.PendingCount > 0, "Dirty probes should be queued");
        int countBefore = volume.PendingCount;

        // Duplicate mark should not re-add duplicate cells
        volume.MarkDirty(new Aabb(Vector3.Zero, new Vector3(32, 32, 32)));
        TestAssert.Equal(countBefore, volume.PendingCount, "Duplicate dirty mark should not increase pending queue");
    }

    [Test]
    public void RadianceVolume_Process_BudgetsRebakes()
    {
        var octa = new Octa(10);
        octa.EmptyMap(10);
        var config = new MapConfig { Skylight = 0xFFFFFF };

        var bounds = new Aabb(new Vector3(512, 512, 512), new Vector3(64, 64, 64));
        var grid = RadianceGrid.FromSauerBounds(bounds, resolution: 4, marginCells: 0);
        var volume = new RadianceVolume(grid)
        {
            SkyRays = 4,
            BudgetPerFrame = 8,
        };

        volume.Bake(octa, config);
        TestAssert.Equal(0, volume.PendingCount, "Initial bake clears pending dirty probes");

        // Mark all 64 probes dirty
        volume.MarkDirty(bounds);
        TestAssert.Equal(64, volume.PendingCount, "All 64 probes should be marked dirty");

        // Process 1 frame (delta): should process BudgetPerFrame = 8 probes
        volume.Process(0.016);
        TestAssert.Equal(56, volume.PendingCount, "8 probes rebaked in frame 1, 56 remaining");
        TestAssert.Equal(8, volume.RebakeCount, "RebakeCount tracked");

        // Process another frame
        volume.Process(0.016);
        TestAssert.Equal(48, volume.PendingCount, "Another 8 probes rebaked in frame 2, 48 remaining");
        TestAssert.Equal(16, volume.RebakeCount, "RebakeCount tracked");

        // Flush remaining
        volume.FlushDirty();
        TestAssert.Equal(0, volume.PendingCount, "FlushDirty clears all remaining dirty probes");
        TestAssert.Equal(64, volume.RebakeCount, "All 64 probes rebaked in total");
    }

    [Test]
    public void RadianceVolume_IncrementalRebake_ReflectsGeometryChange()
    {
        var octa = new Octa(10);
        octa.EmptyMap(10);

        var config = new MapConfig
        {
            Ambient = 0,
            Skylight = 0xFFFFFF,
            Sunlight = 0,
        };

        // Probe placed in open air at (512, 512, 550)
        var bounds = new Aabb(new Vector3(512, 512, 550), new Vector3(16, 16, 16));
        var grid = RadianceGrid.FromSauerBounds(bounds, resolution: 2, marginCells: 0);
        var volume = new RadianceVolume(grid) { SkyRays = 16 };

        // Initial bake: open sky overhead
        volume.Bake(octa, config);
        float initialAmb = volume.Grid[0, 0, 0].Amb;
        TestAssert.True(initialAmb > 0.1f, $"Initial probe has high sky irradiance: {initialAmb}");

        // Now modify the geometry: add a solid ceiling over the probe (octant 4 or 5)
        // Solidifying octant 4 covering Sauer [0..512, 0..512, 512..1024]
        octa.WorldRoot[4].SolidFaces();
        octa.WorldRoot[5].SolidFaces();

        // Mark the probe region dirty and flush
        volume.MarkDirty(bounds, margin: 16f);
        TestAssert.True(volume.PendingCount > 0, "Probes queued dirty after geometry change");

        volume.FlushDirty();
        TestAssert.Equal(0, volume.PendingCount, "All probes rebaked");

        float rebakedAmb = volume.Grid[0, 0, 0].Amb;
        TestAssert.True(rebakedAmb < initialAmb,
            $"Rebaked probe under ceiling should have lower irradiance than open sky: rebaked={rebakedAmb}, initial={initialAmb}");
    }

    // =========================================================================
    // Stage 5: Quality Knobs & Static Ambient Switch
    // =========================================================================

    [Test]
    public void RadianceSettings_PresetsAndProperties_MatchExpectedValues()
    {
        var def = RadianceSettings.Default;
        TestAssert.Equal(RadianceQualityMode.RadianceHints, def.Mode, "Default mode is RadianceHints");
        TestAssert.Equal(16, def.SkyRays, "Default SkyRays is 16");
        TestAssert.Equal(256, def.BudgetPerFrame, "Default BudgetPerFrame is 256");
        TestAssert.True(def.MaxRayDistance == null, "Default MaxRayDistance is null (auto)");
        TestAssert.Equal(RadianceGrid.DefaultResolution, def.Resolution, "Default Resolution is 32");

        var staticAmb = RadianceSettings.StaticAmbient;
        TestAssert.Equal(RadianceQualityMode.StaticAmbient, staticAmb.Mode, "StaticAmbient mode is StaticAmbient");

        var low = RadianceSettings.Low;
        TestAssert.Equal(4, low.SkyRays, "Low SkyRays is 4");
        TestAssert.Equal(128, low.BudgetPerFrame, "Low BudgetPerFrame is 128");
        TestAssert.Equal(256f, low.MaxRayDistance ?? 0f, "Low MaxRayDistance is 256");

        var ultra = RadianceSettings.Ultra;
        TestAssert.Equal(32, ultra.SkyRays, "Ultra SkyRays is 32");
        TestAssert.Equal(512, ultra.BudgetPerFrame, "Ultra BudgetPerFrame is 512");
        TestAssert.Equal(48, ultra.Resolution, "Ultra Resolution is 48");

        var cloned = low.Clone();
        TestAssert.Equal(low.Mode, cloned.Mode, "Cloned Mode matches");
        TestAssert.Equal(low.SkyRays, cloned.SkyRays, "Cloned SkyRays matches");
        TestAssert.Equal(low.BudgetPerFrame, cloned.BudgetPerFrame, "Cloned BudgetPerFrame matches");
        TestAssert.Equal(low.MaxRayDistance, cloned.MaxRayDistance, "Cloned MaxRayDistance matches");
        TestAssert.Equal(low.Resolution, cloned.Resolution, "Cloned Resolution matches");
    }

    [Test]
    public void RadianceVolume_SettingsForwarding_UpdatesUnderlyingKnobs()
    {
        var grid = new RadianceGrid(Vector3.Zero, 64f, 4);
        var volume = new RadianceVolume(grid, new RadianceSettings { SkyRays = 8, BudgetPerFrame = 64 });

        TestAssert.Equal(8, volume.SkyRays, "Initial SkyRays forwarded from Settings");
        TestAssert.Equal(64, volume.BudgetPerFrame, "Initial BudgetPerFrame forwarded from Settings");

        volume.SkyRays = 32;
        TestAssert.Equal(32, volume.Settings.SkyRays, "SkyRays setter updates Settings");

        volume.BudgetPerFrame = 512;
        TestAssert.Equal(512, volume.Settings.BudgetPerFrame, "BudgetPerFrame setter updates Settings");

        volume.MaxRayDistance = 128f;
        TestAssert.Equal(128f, volume.Settings.MaxRayDistance, "MaxRayDistance setter updates Settings");

        volume.Mode = RadianceQualityMode.StaticAmbient;
        TestAssert.Equal(RadianceQualityMode.StaticAmbient, volume.Settings.Mode, "Mode setter updates Settings");
    }

    [Test]
    public void RadianceVolume_StaticAmbientMode_FillsAmbientImmediatelyWithoutRays()
    {
        var octa = new Octa(10);
        octa.EmptyMap(10);

        var config = new MapConfig
        {
            Ambient = 0x808080, // 0.502 grayscale
            Skylight = 0xFFFFFF,
            Sunlight = 0xFFFFFF,
        };

        var grid = new RadianceGrid(Vector3.Zero, 64f, 4);
        var volume = new RadianceVolume(grid, RadianceSettings.StaticAmbient);

        volume.Bake(octa, config);

        // Every probe in the grid should have zero directional and exact ambient irradiance
        Color ambColor = RadianceVolume.DecodeColor(config.Ambient);
        float expectedAmb = (ambColor.R + ambColor.G + ambColor.B) / 3.0f;

        for (int z = 0; z < grid.Resolution; z++)
        {
            for (int y = 0; y < grid.Resolution; y++)
            {
                for (int x = 0; x < grid.Resolution; x++)
                {
                    ref readonly var probe = ref grid[x, y, z];
                    TestAssert.Equal(Vector3.Zero, probe.Dir, "StaticAmbient mode has zero directional vector");
                    TestAssert.True(Mathf.Abs(probe.Amb - expectedAmb) < 1e-4f,
                        $"StaticAmbient mode probe has exact ambient {expectedAmb}, got {probe.Amb}");
                }
            }
        }

        // Marking dirty and rebaking should also keep ambient in StaticAmbient mode
        volume.MarkCellDirty(new Vector3I(0, 0, 0));
        TestAssert.Equal(1, volume.PendingCount, "1 probe marked dirty");
        volume.Process(0.016);
        TestAssert.Equal(0, volume.PendingCount, "Pending probe rebaked in static mode");
        TestAssert.Equal(Vector3.Zero, grid[0, 0, 0].Dir, "Rebaked probe has zero directional vector");
        TestAssert.True(Mathf.Abs(grid[0, 0, 0].Amb - expectedAmb) < 1e-4f, "Rebaked probe retains ambient");
    }

    [Test]
    public void MaterialBuilder_BuildRadianceMaterial_BindsAmbientColorAndConfiguresUseRadiance()
    {
        var octa = new Octa(10);
        octa.EmptyMap(10);

        var grid = new RadianceGrid(Vector3.Zero, 64f, 4);
        var volHints = new RadianceVolume(grid, RadianceSettings.High);
        var volStatic = new RadianceVolume(grid, RadianceSettings.StaticAmbient);

        var catalogue = new MetadataCatalogue();
        catalogue.Config.Ambient = 0x336699;
        catalogue.Config.SkyboxColour = 0xFFFFFF;
        catalogue.VSlots[0] = new CatalogueVSlot { Index = 0, Slot = 0 };

        volHints.Bake(octa, catalogue.Config);
        volStatic.Bake(octa, catalogue.Config);

        var textures = new DumpTextureSource(TestData.ReferenceRoot);

        // 1. RadianceHints mode -> use_radiance = true
        var setHints = MaterialBuilder.Build(catalogue, textures, volHints);
        var matHints = setHints.MaterialFor(0) as ShaderMaterial;
        TestAssert.True(matHints != null, "ShaderMaterial generated for RadianceHints");
        TestAssert.Equal(true, (bool)matHints!.GetShaderParameter("use_radiance"), "use_radiance is true for RadianceHints");
        var ambHints = (Vector3)matHints.GetShaderParameter("ambient_color");
        Color expectedAmb = RadianceVolume.DecodeColor(catalogue.Config.Ambient);
        TestAssert.True(ambHints.IsEqualApprox(new Vector3(expectedAmb.R, expectedAmb.G, expectedAmb.B)), "ambient_color bound in shader");

        // 2. StaticAmbient mode -> use_radiance = false (bypasses 3D texture lookups)
        var setStatic = MaterialBuilder.Build(catalogue, textures, volStatic);
        var matStatic = setStatic.MaterialFor(0) as ShaderMaterial;
        TestAssert.True(matStatic != null, "ShaderMaterial generated for StaticAmbient");
        TestAssert.Equal(false, (bool)matStatic!.GetShaderParameter("use_radiance"), "use_radiance is false for StaticAmbient");
        var ambStatic = (Vector3)matStatic.GetShaderParameter("ambient_color");
        TestAssert.True(ambStatic.IsEqualApprox(new Vector3(expectedAmb.R, expectedAmb.G, expectedAmb.B)), "ambient_color bound in shader");
    }
}
