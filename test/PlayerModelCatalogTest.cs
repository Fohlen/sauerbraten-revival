using System;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Models;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the ported player models table (render.cpp <c>playermodels[5]</c>)
/// and verifies the ADR 0010 3-tier cascade and hot-path lookups.
/// </summary>
public class PlayerModelCatalogTest : GoTestBase
{
    public PlayerModelCatalogTest(Node testScene) : base(testScene) { }

    [Test]
    public void HasFiveEnginePlayerModels()
    {
        TestAssert.Equal(5, PlayerModelCatalog.Defaults.Length, "default player models count");
        TestAssert.Equal(5, PlayerModelCatalog.Count, "active catalog count matches defaults");
        TestAssert.Equal(5, Playermodels.Count, "Playermodels count matches defaults");
    }

    [Test]
    public void RowsAreIndexedAndResolvable()
    {
        for (int i = 0; i < PlayerModelCatalog.Count; i++)
        {
            var id = (PlayerModelId)i;
            TestAssert.Equal(id, PlayerModelCatalog.All[i].Id, $"row {i} Id");
            TestAssert.Equal(id, PlayerModelCatalog.Get(id).Id, $"Get({id})");
            TestAssert.Equal(id, PlayerModelCatalog.Get(i).Id, $"Get({i})");
            TestAssert.Equal(id, Playermodels.Get(i).Id, $"Playermodels.Get({i})");
            TestAssert.Equal(id, ModelCatalog.Get(id).Id, $"ModelCatalog.Get({id})");
            TestAssert.Equal(id, PlayerModelCatalog.TryGet(i)!.Id, $"TryGet({i})");
        }

        TestAssert.True(PlayerModelCatalog.TryGet(-1) == null, "TryGet below range returns null");
        TestAssert.True(PlayerModelCatalog.TryGet(PlayerModelCatalog.Count) == null, "TryGet above range returns null");

        // Clamped lookup
        TestAssert.Equal(PlayerModelId.MrFixit, PlayerModelCatalog.Get(-5).Id, "clamped low index returns mrfixit");
        TestAssert.Equal(PlayerModelId.CaptainCannon, PlayerModelCatalog.Get(99).Id, "clamped high index returns captaincannon");
    }

    [Test]
    public void MrFixitMatchesEngineRow()
    {
        var fixit = PlayerModelCatalog.Get(PlayerModelId.MrFixit);
        TestAssert.Equal("mrfixit", fixit.Name, "name");
        TestAssert.Equal("mrfixit", fixit.Ffa, "ffa");
        TestAssert.Equal("mrfixit/blue", fixit.BlueTeam, "blue");
        TestAssert.Equal("mrfixit/red", fixit.RedTeam, "red");
        TestAssert.Equal("mrfixit/hudguns", fixit.Hudguns, "hudguns");
        TestAssert.Equal("mrfixit/horns", fixit.Quad, "quad");
        TestAssert.Equal(3, fixit.Armour.Length, "armour count");
        TestAssert.Equal("mrfixit/armor/blue", fixit.Armour[0], "blue armour");
        TestAssert.Equal("mrfixit/armor/green", fixit.Armour[1], "green armour");
        TestAssert.Equal("mrfixit/armor/yellow", fixit.Armour[2], "yellow armour");
        TestAssert.Equal("mrfixit", fixit.FfaIcon, "ffa icon");
        TestAssert.Equal("mrfixit_blue", fixit.BlueIcon, "blue icon");
        TestAssert.Equal("mrfixit_red", fixit.RedIcon, "red icon");
        TestAssert.True(fixit.Ragdoll, "ragdoll");

        // Team helpers
        TestAssert.Equal("mrfixit", fixit.Body(0), "neutral body");
        TestAssert.Equal("mrfixit/blue", fixit.Body(1), "blue body");
        TestAssert.Equal("mrfixit/red", fixit.Body(2), "red body");
        TestAssert.Equal("mrfixit", fixit.Icon(0), "neutral icon");
        TestAssert.Equal("mrfixit_blue", fixit.Icon(1), "blue icon");
        TestAssert.Equal("mrfixit_red", fixit.Icon(2), "red icon");
    }

    [Test]
    public void Ogro2ReusesMrFixitHudguns()
    {
        // Engine note: ogro2 reuses mrfixit's hudguns
        var ogro2 = PlayerModelCatalog.Get(PlayerModelId.Ogro2);
        TestAssert.Equal("ogro2", ogro2.Name, "name");
        TestAssert.Equal("mrfixit/hudguns", ogro2.Hudguns, "ogro2 reuses mrfixit hudguns");
        TestAssert.Equal("ogro", ogro2.FfaIcon, "ogro ffa icon");
    }

    [Test]
    public void LoadsOneTresPerPlayerModel()
    {
        for (int i = 0; i < PlayerModelCatalog.Defaults.Length; i++)
        {
            string path = $"{PlayerModelCatalog.ResourceDir}/{PlayerModelCatalog.Defaults[i].Name}.tres";
            TestAssert.True(ResourceLoader.Exists(path), $"resource exists: {path}");
            TestAssert.True(ResourceLoader.Load<PlayerModelInfo>(path) != null, $"loads as PlayerModelInfo: {path}");
        }
    }

    [Test]
    public void LoadedResourcesMatchDefaults()
    {
        var loaded = PlayerModelCatalog.Load();
        TestAssert.Equal(PlayerModelCatalog.Defaults.Length, loaded.Length, "row count");
        for (int i = 0; i < loaded.Length; i++)
        {
            var a = PlayerModelCatalog.Defaults[i];
            var b = loaded[i];
            TestAssert.Equal(a.Id, b.Id, $"row {i} Id");
            TestAssert.Equal(a.Name, b.Name, $"row {i} Name");
            TestAssert.Equal(a.Ffa, b.Ffa, $"row {i} Ffa");
            TestAssert.Equal(a.BlueTeam, b.BlueTeam, $"row {i} BlueTeam");
            TestAssert.Equal(a.RedTeam, b.RedTeam, $"row {i} RedTeam");
            TestAssert.Equal(a.Hudguns, b.Hudguns, $"row {i} Hudguns");
            TestAssert.Equal(a.Quad, b.Quad, $"row {i} Quad");
            TestAssert.Equal(a.Vwep, b.Vwep, $"row {i} Vwep");
            TestAssert.Equal(a.Armour.Length, b.Armour.Length, $"row {i} Armour length");
            for (int k = 0; k < a.Armour.Length; k++)
                TestAssert.Equal(a.Armour[k], b.Armour[k], $"row {i} Armour[{k}]");
            TestAssert.Equal(a.FfaIcon, b.FfaIcon, $"row {i} FfaIcon");
            TestAssert.Equal(a.BlueIcon, b.BlueIcon, $"row {i} BlueIcon");
            TestAssert.Equal(a.RedIcon, b.RedIcon, $"row {i} RedIcon");
            TestAssert.Equal(a.Ragdoll, b.Ragdoll, $"row {i} Ragdoll");
        }
    }

    [Test]
    public void ModOverridesTakePrecedence()
    {
        string tempDir = "user://test_playermodel_mod_overrides";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            var customFixit = new PlayerModelInfo
            {
                Id = PlayerModelId.MrFixit,
                Name = "mrfixit",
                Ffa = "custom_mrfixit",
                BlueTeam = "custom_mrfixit/blue",
                RedTeam = "custom_mrfixit/red",
                Hudguns = "custom_mrfixit/hudguns",
            };
            string overridePath = $"{tempDir}/mrfixit.tres";
            var err = ResourceSaver.Save(customFixit, overridePath);
            TestAssert.Equal(Error.Ok, err, "override saved");

            var loaded = PlayerModelCatalog.Load(PlayerModelCatalog.ResourceDir, modDir: tempDir, userDir: null);
            var fixit = loaded[(int)PlayerModelId.MrFixit];
            TestAssert.Equal("custom_mrfixit", fixit.Ffa, "mod override updates ffa model");
            TestAssert.Equal("custom_mrfixit/blue", fixit.BlueTeam, "mod override updates blue team");

            // Other models remain unaffected
            TestAssert.Equal(PlayerModelCatalog.Defaults[(int)PlayerModelId.Snoutx10k].Ffa, loaded[(int)PlayerModelId.Snoutx10k].Ffa, "other models remain defaults");
        }
        finally
        {
            if (FileAccess.FileExists($"{tempDir}/mrfixit.tres"))
                DirAccess.RemoveAbsolute($"{tempDir}/mrfixit.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    [Test]
    public void UserOverridesTakePrecedenceOverModOverrides()
    {
        string modDir = "user://test_playermodel_tier2_mod";
        string userDir = "user://test_playermodel_tier3_user";
        DirAccess.MakeDirAbsolute(modDir);
        DirAccess.MakeDirAbsolute(userDir);
        try
        {
            // Tier 2: mod override
            var modFixit = new PlayerModelInfo { Id = PlayerModelId.MrFixit, Name = "mrfixit", Ffa = "mod_fixit" };
            ResourceSaver.Save(modFixit, $"{modDir}/mrfixit.tres");

            // Tier 3: user override
            var userFixit = new PlayerModelInfo { Id = PlayerModelId.MrFixit, Name = "mrfixit", Ffa = "user_fixit" };
            ResourceSaver.Save(userFixit, $"{userDir}/mrfixit.tres");

            var loaded = PlayerModelCatalog.Load(PlayerModelCatalog.ResourceDir, modDir: modDir, userDir: userDir);
            TestAssert.Equal("user_fixit", loaded[(int)PlayerModelId.MrFixit].Ffa, "Tier 3 user override wins over Tier 2 mod override");
        }
        finally
        {
            if (FileAccess.FileExists($"{modDir}/mrfixit.tres")) DirAccess.RemoveAbsolute($"{modDir}/mrfixit.tres");
            if (FileAccess.FileExists($"{userDir}/mrfixit.tres")) DirAccess.RemoveAbsolute($"{userDir}/mrfixit.tres");
            DirAccess.RemoveAbsolute(modDir);
            DirAccess.RemoveAbsolute(userDir);
        }
    }

    [Test]
    public void CatalogResourceCanBeLoadedInOverrideDir()
    {
        string tempDir = "user://test_playermodel_catalog_override";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            var catalog = new PlayerModelCatalogResource();
            catalog.Models.Add(new PlayerModelInfo { Id = PlayerModelId.Inky, Name = "inky", Ffa = "pack_inky" });
            catalog.Models.Add(new PlayerModelInfo { Id = PlayerModelId.CaptainCannon, Name = "captaincannon", Ffa = "pack_cannon" });

            string catalogPath = $"{tempDir}/pack.tres";
            var err = ResourceSaver.Save(catalog, catalogPath);
            TestAssert.Equal(Error.Ok, err, "catalog saved");

            var loaded = PlayerModelCatalog.Load(PlayerModelCatalog.ResourceDir, modDir: tempDir, userDir: null);
            TestAssert.Equal("pack_inky", loaded[(int)PlayerModelId.Inky].Ffa, "catalog inky override applied");
            TestAssert.Equal("pack_cannon", loaded[(int)PlayerModelId.CaptainCannon].Ffa, "catalog captaincannon override applied");
        }
        finally
        {
            if (FileAccess.FileExists($"{tempDir}/pack.tres"))
                DirAccess.RemoveAbsolute($"{tempDir}/pack.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    [Test]
    public void ReloadHotSwapsCatalogue()
    {
        string tempDir = "user://test_playermodel_reload";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            var customSnout = new PlayerModelInfo { Id = PlayerModelId.Snoutx10k, Name = "snoutx10k", Ffa = "swapped_snout" };
            ResourceSaver.Save(customSnout, $"{tempDir}/snoutx10k.tres");

            PlayerModelCatalog.Reload(PlayerModelCatalog.ResourceDir, modDir: tempDir, userDir: null);
            TestAssert.Equal("swapped_snout", PlayerModelCatalog.Get(PlayerModelId.Snoutx10k).Ffa, "Reload hotswapped active catalogue");
            TestAssert.Equal("swapped_snout", Playermodels.Get(1).Ffa, "Playermodels reflected hotswapped catalogue");
        }
        finally
        {
            // Restore default catalogue state
            PlayerModelCatalog.Reload(PlayerModelCatalog.ResourceDir, modDir: null, userDir: null);
            TestAssert.Equal(PlayerModelCatalog.Defaults[(int)PlayerModelId.Snoutx10k].Ffa, PlayerModelCatalog.Get(PlayerModelId.Snoutx10k).Ffa, "defaults restored");

            if (FileAccess.FileExists($"{tempDir}/snoutx10k.tres"))
                DirAccess.RemoveAbsolute($"{tempDir}/snoutx10k.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    [Test]
    public void ResolvesIdFromNameOrFilenameWhenIdOmitted()
    {
        string tempDir = "user://test_playermodel_name_resolve";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            // Id omitted (defaults to PlayerModelId.None), but Name is "inky"
            var unnamedId = new PlayerModelInfo { Id = PlayerModelId.None, Name = "inky", Ffa = "resolved_inky" };
            ResourceSaver.Save(unnamedId, $"{tempDir}/any_filename.tres");

            // Both Id and Name omitted, but filename is "captaincannon.tres"
            var noIdOrName = new PlayerModelInfo { Id = PlayerModelId.None, Name = "", Ffa = "resolved_cannon" };
            ResourceSaver.Save(noIdOrName, $"{tempDir}/captaincannon.tres");

            var loaded = PlayerModelCatalog.Load(PlayerModelCatalog.ResourceDir, modDir: tempDir, userDir: null);
            TestAssert.Equal("resolved_inky", loaded[(int)PlayerModelId.Inky].Ffa, "resolved inky via Name");
            TestAssert.Equal(PlayerModelId.Inky, loaded[(int)PlayerModelId.Inky].Id, "assigned PlayerModelId.Inky");
            TestAssert.Equal("resolved_cannon", loaded[(int)PlayerModelId.CaptainCannon].Ffa, "resolved captaincannon via filename");
            TestAssert.Equal(PlayerModelId.CaptainCannon, loaded[(int)PlayerModelId.CaptainCannon].Id, "assigned PlayerModelId.CaptainCannon");
        }
        finally
        {
            if (FileAccess.FileExists($"{tempDir}/any_filename.tres")) DirAccess.RemoveAbsolute($"{tempDir}/any_filename.tres");
            if (FileAccess.FileExists($"{tempDir}/captaincannon.tres")) DirAccess.RemoveAbsolute($"{tempDir}/captaincannon.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    private sealed class SubclassedPlayerModelCatalog : SauerbratenRevival.World.Content.ResourceCatalogBase<PlayerModelInfo, PlayerModelInfo, PlayerModelId>
    {
        public override PlayerModelInfo[] Defaults => PlayerModelCatalog.Defaults;
        public override string BasePath => PlayerModelCatalog.ResourceDir;
        protected override PlayerModelInfo Convert(PlayerModelInfo res) => res;
        protected override PlayerModelInfo CreateEmpty() => new();
    }

    [Test]
    public void ResourceCatalogBaseInheritanceWorks()
    {
        var catalogInstance = new SubclassedPlayerModelCatalog();
        TestAssert.Equal(PlayerModelCatalog.Defaults.Length, catalogInstance.All.Length, "inherits active table");
        TestAssert.Equal("mrfixit", catalogInstance.All[(int)PlayerModelId.MrFixit].Name, "inherits mrfixit name");
    }
}
