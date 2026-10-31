using System;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the engine crosshair table (rendergl.cpp <c>crosshairs[MAXCROSSHAIRS]</c>, fps.cpp <c>defaultcrosshair</c>)
/// and verifies the ADR 0010 3-tier cascade and hot-path lookups.
/// </summary>
public class CrosshairCatalogTest : GoTestBase
{
    public CrosshairCatalogTest(Node testScene) : base(testScene) { }

    [Test]
    public void HasThreeEngineCrosshairs()
    {
        TestAssert.Equal(3, CrosshairCatalog.Defaults.Length, "default crosshairs count");
        TestAssert.Equal(3, CrosshairCatalog.Count, "active catalog count matches defaults");
        TestAssert.Equal(3, Crosshairs.Count, "Crosshairs count matches defaults");
    }

    [Test]
    public void RowsAreIndexedAndResolvable()
    {
        for (int i = 0; i < CrosshairCatalog.Count; i++)
        {
            var id = (CrosshairType)i;
            TestAssert.Equal(id, CrosshairCatalog.All[i].Id, $"row {i} Id");
            TestAssert.Equal(id, CrosshairCatalog.Get(id).Id, $"Get({id})");
            TestAssert.Equal(id, CrosshairCatalog.Get(i).Id, $"Get({i})");
            TestAssert.Equal(id, Crosshairs.Get(i).Id, $"Crosshairs.Get({i})");
            TestAssert.Equal(id, Crosshairs.Get(id).Id, $"Crosshairs.Get({id})");
            TestAssert.Equal(id, CrosshairCatalog.TryGet(i)!.Id, $"TryGet({i})");
        }

        TestAssert.True(CrosshairCatalog.TryGet(-1) == null, "TryGet below range returns null");
        TestAssert.True(CrosshairCatalog.TryGet(CrosshairCatalog.Count) == null, "TryGet above range returns null");

        // Clamped lookup
        TestAssert.Equal(CrosshairType.Default, CrosshairCatalog.Get(-5).Id, "clamped low index returns default");
        TestAssert.Equal(CrosshairType.Hit, CrosshairCatalog.Get(99).Id, "clamped high index returns hit");
    }

    [Test]
    public void DefaultCrosshairMatchesEnginePath()
    {
        var def = CrosshairCatalog.Get(CrosshairType.Default);
        TestAssert.Equal("default", def.Name, "name");
        TestAssert.Equal("crosshairs/default.png", def.Path, "path");
    }

    [Test]
    public void TeammateCrosshairMatchesEnginePath()
    {
        var teammate = CrosshairCatalog.Get(CrosshairType.Teammate);
        TestAssert.Equal("teammate", teammate.Name, "name");
        TestAssert.Equal("crosshairs/default_teammate.png", teammate.Path, "path");
    }

    [Test]
    public void HitCrosshairMatchesEnginePath()
    {
        var hit = CrosshairCatalog.Get(CrosshairType.Hit);
        TestAssert.Equal("hit", hit.Name, "name");
        TestAssert.Equal("crosshairs/default_hit.png", hit.Path, "path");
    }

    [Test]
    public void LoadsOneTresPerCrosshair()
    {
        for (int i = 0; i < CrosshairCatalog.Defaults.Length; i++)
        {
            string path = $"{CrosshairCatalog.ResourceDir}/{CrosshairCatalog.Defaults[i].Name}.tres";
            TestAssert.True(ResourceLoader.Exists(path), $"resource exists: {path}");
            TestAssert.True(ResourceLoader.Load<CrosshairInfo>(path) != null, $"loads as CrosshairInfo: {path}");
        }
    }

    [Test]
    public void LoadedResourcesMatchDefaults()
    {
        var loaded = CrosshairCatalog.Load();
        TestAssert.Equal(CrosshairCatalog.Defaults.Length, loaded.Length, "row count");
        for (int i = 0; i < loaded.Length; i++)
        {
            var expected = CrosshairCatalog.Defaults[i];
            var actual = loaded[i];
            TestAssert.Equal(expected.Id, actual.Id, $"row {i} Id");
            TestAssert.Equal(expected.Name, actual.Name, $"row {i} Name");
            TestAssert.Equal(expected.Path, actual.Path, $"row {i} Path");
        }
    }

    [Test]
    public void ReloadHotSwapsCatalogue()
    {
        var original = CrosshairCatalog.All;
        try
        {
            var custom = new CrosshairInfo[]
            {
                new() { Id = CrosshairType.Default, Name = "custom_default", Path = "crosshairs/custom.png" },
                new() { Id = CrosshairType.Teammate, Name = "custom_teammate", Path = "crosshairs/custom_team.png" },
                new() { Id = CrosshairType.Hit, Name = "custom_hit", Path = "crosshairs/custom_hit.png" },
            };

            CrosshairCatalog.Override(custom);
            TestAssert.Equal("custom_default", CrosshairCatalog.Get(CrosshairType.Default).Name, "overridden default");
            TestAssert.Equal("custom_teammate", Crosshairs.Get(CrosshairType.Teammate).Name, "overridden teammate via facade");

            CrosshairCatalog.Reload();
            TestAssert.Equal("default", CrosshairCatalog.Get(CrosshairType.Default).Name, "restored default name after reload");
        }
        finally
        {
            CrosshairCatalog.Override(original);
        }
    }

    [Test]
    public void ResolvesIdFromNameOrFilenameWhenIdOmitted()
    {
        string tmpDir = "user://test_crosshair_name_resolve";
        DirAccess.MakeDirAbsolute(tmpDir);

        try
        {
            var omittedId = new CrosshairInfo
            {
                Id = CrosshairType.None,
                Name = "hit",
                Path = "crosshairs/mod_hit.png"
            };
            ResourceSaver.Save(omittedId, $"{tmpDir}/hit.tres");

            var loaded = CrosshairCatalog.Load(CrosshairCatalog.ResourceDir, tmpDir, null);
            TestAssert.Equal(CrosshairType.Hit, loaded[(int)CrosshairType.Hit].Id, "id resolved to Hit");
            TestAssert.Equal("crosshairs/mod_hit.png", loaded[(int)CrosshairType.Hit].Path, "path overridden by resolved entry");
        }
        finally
        {
            if (DirAccess.DirExistsAbsolute(tmpDir))
            {
                using var dir = DirAccess.Open(tmpDir);
                if (dir != null)
                {
                    dir.ListDirBegin();
                    string file;
                    while ((file = dir.GetNext()) != "")
                    {
                        if (!dir.CurrentIsDir()) dir.Remove(file);
                    }
                }
                DirAccess.RemoveAbsolute(tmpDir);
            }
        }
    }

    [Test]
    public void CascadeUserOverrideTakesPrecedence()
    {
        string modDir = "user://test_crosshair_cascade_mod";
        string userDir = "user://test_crosshair_cascade_user";
        DirAccess.MakeDirAbsolute(modDir);
        DirAccess.MakeDirAbsolute(userDir);

        try
        {
            var modCrosshair = new CrosshairInfo
            {
                Id = CrosshairType.Default,
                Name = "default",
                Path = "crosshairs/mod_default.png"
            };
            ResourceSaver.Save(modCrosshair, $"{modDir}/default.tres");

            var userCrosshair = new CrosshairInfo
            {
                Id = CrosshairType.Default,
                Name = "default",
                Path = "crosshairs/user_default.png"
            };
            ResourceSaver.Save(userCrosshair, $"{userDir}/default.tres");

            var loaded = CrosshairCatalog.Load(CrosshairCatalog.ResourceDir, modDir, userDir);
            TestAssert.Equal("crosshairs/user_default.png", loaded[(int)CrosshairType.Default].Path, "user override wins over mod override");
        }
        finally
        {
            CleanupDir(modDir);
            CleanupDir(userDir);
        }
    }

    [Test]
    public void CatalogResourceCanBeLoadedInOverrideDir()
    {
        string modDir = "user://test_crosshair_catalog_pack";
        DirAccess.MakeDirAbsolute(modDir);

        try
        {
            var pack = new CrosshairCatalogResource();
            pack.Crosshairs.Add(new CrosshairInfo
            {
                Id = CrosshairType.Teammate,
                Name = "teammate",
                Path = "crosshairs/pack_teammate.png"
            });
            ResourceSaver.Save(pack, $"{modDir}/pack.tres");

            var loaded = CrosshairCatalog.Load(CrosshairCatalog.ResourceDir, modDir, null);
            TestAssert.Equal("crosshairs/pack_teammate.png", loaded[(int)CrosshairType.Teammate].Path, "pack definition applied");
            TestAssert.Equal("crosshairs/default.png", loaded[(int)CrosshairType.Default].Path, "default slot preserved");
        }
        finally
        {
            CleanupDir(modDir);
        }
    }

    [Test]
    public void CrosshairRendererLoadsCatalogTexturesAndFallsBack()
    {
        var renderer = new CrosshairRenderer();
        var fakeTextures = new FakeTextureSource();

        renderer.LoadTextures(fakeTextures);

        TestAssert.True(renderer.GetTexture(CrosshairRenderer.CrosshairDefault) != null, "default texture loaded");
        TestAssert.True(renderer.GetTexture(CrosshairRenderer.CrosshairTeammate) != null, "teammate texture loaded");
        TestAssert.True(renderer.GetTexture(CrosshairRenderer.CrosshairHit) != null, "hit texture loaded");

        // Missing index falls back to default
        TestAssert.True(renderer.GetTexture(99) == renderer.GetTexture(CrosshairRenderer.CrosshairDefault), "out of range index falls back to default");

        // LoadCrosshair ports rendergl.cpp:2086 loadcrosshair
        renderer.LoadCrosshair("crosshairs/custom.png", 3, fakeTextures);
        TestAssert.True(renderer.GetTexture(3) != null, "slot 3 loaded");
    }

    private static void CleanupDir(string dirPath)
    {
        if (DirAccess.DirExistsAbsolute(dirPath))
        {
            using var dir = DirAccess.Open(dirPath);
            if (dir != null)
            {
                dir.ListDirBegin();
                string file;
                while ((file = dir.GetNext()) != "")
                {
                    if (!dir.CurrentIsDir()) dir.Remove(file);
                }
            }
            DirAccess.RemoveAbsolute(dirPath);
        }
    }

    private sealed class FakeTextureSource : ITextureSource
    {
        private readonly ImageTexture _dummy = ImageTexture.CreateFromImage(Image.CreateEmpty(16, 16, false, Image.Format.Rgba8));

        public Texture2D? Get(string path) => _dummy;
        public Vector2I Size(string path) => new(16, 16);
        public Image? GetImage(string path) => null;
    }
}
