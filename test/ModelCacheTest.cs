using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Content;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Models;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class ModelCacheTest : GoTestBase
{
    public ModelCacheTest(Node testScene) : base(testScene) { }

    [Setup]
    public void SetUp()
    {
        ModelCache.Clear();
    }

    [Cleanup]
    public void TearDown()
    {
        ModelCache.Clear();
    }

    [Test]
    public void CachesAndRetainsPackedScenes()
    {
        string grenadeGlb = ContentRoots.ProjectPath(ContentKind.Model, "projectiles_grenade.glb");
        if (!ResourceLoader.Exists(grenadeGlb))
        {
            GD.Print("[modelcache] projectiles_grenade.glb missing; skipping test");
            return;
        }

        TestAssert.False(ModelCache.HasScene(grenadeGlb), "cache starts empty");

        var first = ModelCache.GetOrLoadScene(grenadeGlb);
        TestAssert.True(first != null, "loads grenade scene");
        TestAssert.True(ModelCache.HasScene(grenadeGlb), "has scene in cache");
        TestAssert.Equal(1, ModelCache.CachedSceneCount, "one scene cached");

        var second = ModelCache.GetOrLoadScene(grenadeGlb);
        TestAssert.True(object.ReferenceEquals(first, second), "subsequent lookup returns exact cached PackedScene");
    }

    [Test]
    public void PreloadProjectilesCachesScenesAndMaterials()
    {
        var textures = TextureSources.CreateDefault();
        ModelCache.PreloadProjectiles(textures);

        string grenadeGlb = ContentRoots.ProjectPath(ContentKind.Model, "projectiles_grenade.glb");
        string rocketGlb = ContentRoots.ProjectPath(ContentKind.Model, "projectiles_rocket.glb");

        if (ResourceLoader.Exists(grenadeGlb))
            TestAssert.True(ModelCache.HasScene(grenadeGlb), "grenade glb cached");

        if (ResourceLoader.Exists(rocketGlb))
            TestAssert.True(ModelCache.HasScene(rocketGlb), "rocket glb cached");

        var grenadeMat1 = ModelCache.GetGrenadeMaterial(textures);
        var grenadeMat2 = ModelCache.GetGrenadeMaterial(textures);
        TestAssert.True(grenadeMat1 != null, "grenade material created");
        TestAssert.True(object.ReferenceEquals(grenadeMat1, grenadeMat2), "grenade material cached and reused");

        var rocketMat1 = ModelCache.GetRocketMaterial(textures);
        var rocketMat2 = ModelCache.GetRocketMaterial(textures);
        TestAssert.True(rocketMat1 != null, "rocket material created");
        TestAssert.True(object.ReferenceEquals(rocketMat1, rocketMat2), "rocket material cached and reused");
    }

    [Test]
    public void PreloadModelProcessesParts()
    {
        string grenadeGlb = ContentRoots.ProjectPath(ContentKind.Model, "projectiles_grenade.glb");
        if (!ResourceLoader.Exists(grenadeGlb)) return;

        var info = new ModelInfo
        {
            Name = "mock_model",
            GlbPath = grenadeGlb,
            Parts =
            {
                new ModelInfo { Name = "mock_part_0", GlbPath = grenadeGlb },
            }
        };

        int loaded = ModelCache.PreloadModel(info);
        TestAssert.Equal(2, loaded, "preloaded both root and part");
        TestAssert.True(ModelCache.HasScene(grenadeGlb), "scene is in cache");
    }

    [Test]
    public void ClearsCachesCorrectly()
    {
        var textures = TextureSources.CreateDefault();
        ModelCache.GetGrenadeMaterial(textures);
        string grenadeGlb = ContentRoots.ProjectPath(ContentKind.Model, "projectiles_grenade.glb");
        if (ResourceLoader.Exists(grenadeGlb)) ModelCache.PreloadScene(grenadeGlb);

        TestAssert.True(ModelCache.CachedMaterialCount > 0, "materials cached");
        ModelCache.Clear();
        TestAssert.Equal(0, ModelCache.CachedSceneCount, "scenes cleared");
        TestAssert.Equal(0, ModelCache.CachedMaterialCount, "materials cleared");
    }

    [Test]
    public void ModelInstancePopulatesCache()
    {
        string grenadeGlb = ContentRoots.ProjectPath(ContentKind.Model, "projectiles_grenade.glb");
        if (!ResourceLoader.Exists(grenadeGlb)) return;

        TestAssert.False(ModelCache.HasScene(grenadeGlb), "not yet cached");

        var instance = new ModelInstance
        {
            GlbPath = grenadeGlb,
            KeepSkeleton = false, // non-skeletal model drops temporary scene
        };
        TestScene.AddChild(instance);

        TestAssert.True(instance.ModelLoaded, "model loaded");
        TestAssert.True(ModelCache.HasScene(grenadeGlb), "ModelInstance populated ModelCache");

        TestScene.RemoveChild(instance);
        instance.Free();

        // Even after instance is freed, the PackedScene remains cached
        TestAssert.True(ModelCache.HasScene(grenadeGlb), "PackedScene remains cached after ModelInstance freed");
    }
}
