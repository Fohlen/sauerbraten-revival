using System;
using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Lighting;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Unit tests for <see cref="GpuRadianceBaker"/> (GPU Compute Radiance Hints Stage 2):
/// tests GPU compute shader compilation, dispatch, VRAM resource management, and CPU fallback.
/// </summary>
public class GpuRadianceBakerTest : GoTestBase
{
    public GpuRadianceBakerTest(Node testScene) : base(testScene) { }

    [Test]
    public void GpuRadianceBaker_Init_InitializesPipelinesOrReportsHeadless()
    {
        using var baker = new GpuRadianceBaker();
        var rd = RenderingServer.GetRenderingDevice();

        if (rd == null)
        {
            TestAssert.False(baker.IsGpuAvailable, "In headless/no-device environments, IsGpuAvailable should be false");
            TestAssert.True(baker.OutputTextureRd == null, "OutputTextureRd is null when GPU is unavailable");
        }
        else
        {
            TestAssert.True(baker.IsGpuAvailable, "When RenderingDevice is present, compute pipelines should initialize successfully");
        }
    }

    [Test]
    public void RadianceSettings_VoxelResolution_ConfigurableAndCloned()
    {
        var settings = new RadianceSettings { VoxelResolution = 64 };
        TestAssert.Equal(64, settings.VoxelResolution, "VoxelResolution sets correctly");
        var clone = settings.Clone();
        TestAssert.Equal(64, clone.VoxelResolution, "VoxelResolution clones correctly");
    }

    [Test]
    public void GpuRadianceBaker_Bake_EmptyMap_PopulatesOutputTextureAndProbes()
    {
        var rd = RenderingServer.GetRenderingDevice();
        if (rd == null)
        {
            GD.Print("[GpuRadianceBakerTest] Skipping GPU bake test (no RenderingDevice in current environment)");
            return;
        }

        using var baker = new GpuRadianceBaker();
        TestAssert.True(baker.IsGpuAvailable, "GpuRadianceBaker is available");

        var octa = new Octa(10);
        octa.EmptyMap(10);

        var config = new MapConfig
        {
            Ambient = 0x333333, // ~0.2
            Skylight = 0x808080, // ~0.5
            Sunlight = 0xFFFFFF,
            SunlightScale = 1.0f,
            SunlightYaw = 0,
            SunlightPitch = 90, // down
        };

        var bounds = new Aabb(Vector3.Zero, new Vector3(1024f, 1024f, 1024f));
        var radGrid = RadianceGrid.FromSauerBounds(bounds, resolution: 16, marginCells: 0);
        var voxelGrid = VoxelGrid.FromRadianceGrid(radGrid, resolution: 64);
        voxelGrid.Extract(octa);

        var lights = new List<DirectLightSource>
        {
            new DirectLightSource(new Vector3(512f, 512f, 600f), 200f, Colors.White)
        };

        bool ok = baker.Bake(voxelGrid, radGrid, config, lights, skyRays: 8);
        TestAssert.True(ok, "GPU compute dispatch succeeded");
        TestAssert.True(baker.OutputTextureRd != null, "Output Texture3Drd created");
        TestAssert.True(baker.ProbeFinalRid.IsValid, "ProbeFinalRid is valid");

        // Readback to CPU grid and inspect probes
        baker.ReadbackToGrid(radGrid);

        // Probe in open air above floor near the light
        Vector3 probePosWorld = RadianceGrid.SauerToWorld(new Vector3(512f, 512f, 580f));
        RadianceProbe probe = radGrid.Sample(probePosWorld);

        TestAssert.True(probe.Amb > 0f, $"Air probe should have positive ambient irradiance: {probe.Amb}");
        TestAssert.True(probe.Dir.Length() > 0f, $"Air probe should receive directional light from sun/light: {probe.Dir}");

        // Probe deep in solid floor (Sauer Z = 100) should be zeroed out
        Vector3 floorPosWorld = RadianceGrid.SauerToWorld(new Vector3(512f, 512f, 100f));
        RadianceProbe floorProbe = radGrid.Sample(floorPosWorld);
        TestAssert.Equal(0f, floorProbe.Amb, "Solid floor probe ambient should be zeroed out");
        TestAssert.Equal(Vector3.Zero, floorProbe.Dir, "Solid floor probe directional should be zero");
    }

    [Test]
    public void RadianceVolume_Bake_IntegratesGpuComputeWithFallback()
    {
        var octa = new Octa(10);
        octa.EmptyMap(10);

        var config = new MapConfig
        {
            Ambient = 0x191919,
            Skylight = 0x808080,
            Sunlight = 0xFFFFFF,
            SunlightScale = 1.0f,
            SunlightYaw = 0,
            SunlightPitch = 90,
        };

        var bounds = new Aabb(Vector3.Zero, new Vector3(1024f, 1024f, 1024f));
        var grid = RadianceGrid.FromSauerBounds(bounds, resolution: 16, marginCells: 0);
        var volume = new RadianceVolume(grid);

        volume.Bake(octa, config);

        TestAssert.True(volume.Texture != null, "RadianceVolume.Texture must be populated after Bake");
        var rd = RenderingServer.GetRenderingDevice();
        if (rd != null && volume.GpuBaker != null && volume.GpuBaker.IsGpuAvailable)
        {
            TestAssert.True(volume.Texture is Texture3Drd, "When GPU compute is available, Texture should be Texture3Drd");
            TestAssert.True(volume.VoxelGrid != null, "VoxelGrid should be cached on volume");
        }
        else
        {
            TestAssert.True(volume.Texture is ImageTexture3D, "In headless or CPU mode, Texture should be ImageTexture3D");
        }
    }

    [Test]
    public void GpuRadianceBaker_IncrementalBake_DirtyMinMax_UpdatesSubBox()
    {
        var rd = RenderingServer.GetRenderingDevice();
        if (rd == null)
        {
            GD.Print("[GpuRadianceBakerTest] Skipping GPU incremental bake test (no RenderingDevice in current environment)");
            return;
        }

        var octa = new Octa(10);
        octa.EmptyMap(10); // Floor at Z in [0, 512), open air in [512, 1024)

        var config = new MapConfig
        {
            Ambient = 0x333333,
            Skylight = 0x808080,
            Sunlight = 0xFFFFFF,
            SunlightScale = 1.0f,
            SunlightYaw = 0,
            SunlightPitch = 90,
        };

        var bounds = new Aabb(Vector3.Zero, new Vector3(1024f, 1024f, 1024f));
        var radGrid = RadianceGrid.FromSauerBounds(bounds, resolution: 16, marginCells: 0);
        var volume = new RadianceVolume(radGrid);

        // Initial full bake
        volume.Bake(octa, config);
        TestAssert.True(volume.GpuBaker != null && volume.GpuBaker.IsGpuAvailable, "GpuBaker must be available");

        volume.ReadbackFromGpu();

        // Sample air probe at (256, 256, 580)
        Vector3 probePos1 = RadianceGrid.SauerToWorld(new Vector3(256f, 256f, 580f));
        RadianceProbe initialAirProbe = radGrid.Sample(probePos1);
        TestAssert.True(initialAirProbe.Amb > 0f, "Initial air probe has ambient light");

        // Sample untouched air probe far away in another quadrant at (768, 768, 580)
        Vector3 probePosFar = RadianceGrid.SauerToWorld(new Vector3(768f, 768f, 580f));
        RadianceProbe initialFarProbe = radGrid.Sample(probePosFar);
        TestAssert.True(initialFarProbe.Amb > 0f, "Initial far probe has ambient light");

        // Modify octant 4: Sauer [0..512, 0..512, 512..1024] to be entirely solid
        octa.WorldRoot[4].SolidFaces();

        // Mark only the modified octant dirty
        var dirtyBox = new Aabb(new Vector3(0f, 0f, 512f), new Vector3(512f, 512f, 512f));
        volume.MarkDirty(dirtyBox);

        // Readback probes after incremental update
        volume.ReadbackFromGpu();

        // The probe at (256, 256, 580) is now inside solid geometry -> should be zeroed out
        RadianceProbe updatedProbe = radGrid.Sample(probePos1);
        TestAssert.Equal(0f, updatedProbe.Amb, "Solidified probe ambient should now be zero");
        TestAssert.Equal(Vector3.Zero, updatedProbe.Dir, "Solidified probe directional should be zero");

        // Untouched probe far away must stay non-zero and match its initial lighting
        RadianceProbe updatedFarProbe = radGrid.Sample(probePosFar);
        TestAssert.True(Mathf.Abs(updatedFarProbe.Amb - initialFarProbe.Amb) < 1e-3f,
            $"Untouched far probe ambient should be preserved: before={initialFarProbe.Amb}, after={updatedFarProbe.Amb}");
    }
}
