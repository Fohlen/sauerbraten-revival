using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Editor;
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

        // Submit adding a second light entity; command dispatch updates lights automatically
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

        TestAssert.Equal(2, lightsRoot.GetChildCount(), "updated to 2 light children immediately after entity add command");

        // Re-sync lights is idempotent
        level.SyncLights();
        TestAssert.Equal(2, lightsRoot.GetChildCount(), "remains 2 light children after idempotent SyncLights");

        // Submit removing the first light entity
        level.Sink.Submit(Command.CreateRemoveEntity(light1.Id));
        TestAssert.Equal(1, lightsRoot.GetChildCount(), "updated to 1 light child immediately after entity removal command");

        level.SyncLights();
        TestAssert.Equal(1, lightsRoot.GetChildCount(), "remains 1 light child after idempotent SyncLights");

        level.Free();
    }

    [Test]
    public void MapLevelDoesNotRecomputeLightsDuringPhysicsProcess()
    {
        string dir = System.IO.Path.Combine(OS.GetUserDataDir(), "map_phys_lights_" + System.Guid.NewGuid().ToString("N"));
        var octa = new Octa(8);
        octa.EmptyMap(8);

        var light = new MapEntity
        {
            Id = 1,
            Type = EntityType.Light,
            Position = new Vector3(64, 64, 64),
            Attr1 = 100,
            Attr2 = 255,
            Attr3 = 255,
            Attr4 = 255,
        };
        MapStore.SaveCompacted(dir, octa, new List<MapEntity> { light }, environment: MapEnvironment.CreateDefault());

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

        var lightNodeBefore = lightsRoot.GetChild(0);

        // Run physics process steps - lights must not be re-applied or reconstructed
        for (int i = 0; i < 10; i++)
        {
            level._PhysicsProcess(0.005);
        }

        TestAssert.Equal(1, lightsRoot.GetChildCount(), "light child count unchanged");
        TestAssert.True(ReferenceEquals(lightNodeBefore, lightsRoot.GetChild(0)), "light node was not re-created during physics process");

        level.Free();
    }

    [Test]
    public void MapLevelMarksRendererDirtyWhenGeometryChanges()
    {
        string dir = System.IO.Path.Combine(OS.GetUserDataDir(), "map_geom_lights_" + System.Guid.NewGuid().ToString("N"));
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

        level.Renderer!.FlushDirty();
        TestAssert.Equal(0, level.Renderer.PendingCount, "renderer flushed clean");

        // Submit a geometry mutation command
        var sel = new Selection(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), grid: 8);
        level.Sink!.Submit(Command.CreateEditFace(sel, dir: 0, mode: 0));

        TestAssert.True(level.Renderer.PendingCount > 0, "geometry mutation command marked chunk dirty");

        level.Free();
    }
}
