using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Lighting;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Runtime.Formats.Save;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies the native map lighting path bakes radiance from the environment
/// command (ADR 0009 §6 / ADR 0002): <see cref="MapLevel"/> must own a baked
/// <see cref="RadianceVolume"/> and shade chunk surfaces with it. Also verifies
/// entity lights are dynamically re-applied and synchronized with radiance.
/// </summary>
public class MapRadianceTest : GoTestBase
{
    public MapRadianceTest(Node testScene) : base(testScene) { }

    private static MapConfig Config => MapEnvironment.ToConfig(MapEnvironment.CreateDefault());

    [Test]
    public void MapLevelBakesRadianceForAnUntexturedNativeMap()
    {
        string dir = System.IO.Path.Combine(OS.GetUserDataDir(), "map_radiance_" + System.Guid.NewGuid().ToString("N"));
        var octa = new Octa(8);
        octa.EmptyMap(8);
        MapStore.SaveCompacted(dir, octa, environment: MapEnvironment.CreateDefault());

        var level = new MapLevel
        {
            MapDir = dir,
            ChunkSize = 64,
            BakeRadiance = true,
            RadianceSettings = new LightingSettings { Resolution = 8, SkyRays = 4 },
        };
        level.Build();

        TestAssert.True(level.Radiance != null, "radiance volume baked");
        TestAssert.True(level.Radiance!.Texture != null, "radiance 3D texture created");
        TestAssert.True(ReferenceEquals(level.Renderer!.Radiance, level.Radiance), "renderer ticks the baked volume");
        TestAssert.True(level.Renderer.FallbackMaterial is ShaderMaterial, "untextured map uses the radiance shader fallback");

        level.Free();
    }

    [Test]
    public void ManifestMaterialSetUsesTheRadianceShader()
    {
        var octa = new Octa(6);
        octa.EmptyMap(6);
        var volume = RadianceVolume.CreateForOcta(octa, new LightingSettings { Resolution = 4, SkyRays = 1 });
        volume.Bake(octa, Config);

        var data = new ManifestData();
        data.Materials.Add(new MaterialEntry
        {
            Index = 1,
            Slot = 1,
            Scale = 1f,
            DiffusePath = "",
            NormalPath = "",
            GlowPath = "",
        });

        MaterialSet set = ManifestBuilder.ToRadianceMaterialSet(data, new ResourceTextureSource(), volume, Config);

        TestAssert.True(set.MaterialFor(1) is ShaderMaterial, "manifest surface samples radiance");
    }

    [Test]
    public void MapLevelReappliesWorldLightsWhenEntitiesChange()
    {
        string dir = System.IO.Path.Combine(OS.GetUserDataDir(), "map_lights_" + System.Guid.NewGuid().ToString("N"));
        var octa = new Octa(8);
        octa.EmptyMap(8);

        var light1 = new MapEntity
        {
            Id = 1,
            Type = EntityType.Light,
            Position = new Vector3(100, 100, 100),
            Attr1 = 150,
            Attr2 = 255,
            Attr3 = 255,
            Attr4 = 255,
        };
        MapStore.SaveCompacted(dir, octa, new List<MapEntity> { light1 }, environment: MapEnvironment.CreateDefault());

        var level = new MapLevel
        {
            MapDir = dir,
            ChunkSize = 64,
            BakeRadiance = true,
            RadianceSettings = new LightingSettings { Resolution = 8, SkyRays = 4 },
        };
        level.Build();

        var lightsRoot = level.GetNodeOrNull<Node3D>(LightBuilder.LightsNodeName);
        TestAssert.True(lightsRoot != null, "Lights node exists");
        TestAssert.Equal(1, lightsRoot!.GetChildCount(), "initial 1 light child");

        // Submit adding a second light entity
        var light2 = new MapEntity
        {
            Id = 2,
            Type = EntityType.Light,
            Position = new Vector3(200, 200, 200),
            Attr1 = 100,
            Attr2 = 255,
            Attr3 = 0,
            Attr4 = 0,
        };
        level.Sink!.Submit(Command.CreateAddEntity(light2));

        // Re-sync lights
        level.SyncLights();

        TestAssert.Equal(2, lightsRoot.GetChildCount(), "updated to 2 light children after entity add");

        // Submit removing the first light entity
        level.Sink.Submit(Command.CreateRemoveEntity(light1.Id));
        level.SyncLights();

        TestAssert.Equal(1, lightsRoot.GetChildCount(), "updated to 1 light child after entity removal");

        level.Free();
    }
}
