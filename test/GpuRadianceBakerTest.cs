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
}
