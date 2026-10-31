using System;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the engine map lists table (data/lists.cfg SVN r6889) and verifies
/// the ADR 0010 3-tier cascade, hot-path lookup, and mode -> pool mappings.
/// </summary>
public class MapListCatalogTest : GoTestBase
{
    public MapListCatalogTest(Node testScene) : base(testScene) { }

    [Test]
    public void HasSixEngineMapLists()
    {
        TestAssert.Equal(6, MapListCatalog.Defaults.Length, "default map list slots count");
        TestAssert.Equal(6, MapListCatalog.Count, "active catalog count matches defaults");
        TestAssert.Equal(6, MapListCatalog.All.Length, "All array length matches defaults");
    }

    [Test]
    public void RowsAreIndexedAndResolvable()
    {
        for (int i = 0; i < MapListCatalog.Count; i++)
        {
            var id = (MapListId)i;
            TestAssert.Equal(id, MapListCatalog.All[i].Id, $"row {i} Id");
            TestAssert.Equal(id, MapListCatalog.Get(id).Id, $"Get({id})");
            TestAssert.Equal(id, MapListCatalog.Get(i).Id, $"Get({i})");
            TestAssert.Equal(id, MapListCatalog.TryGet(i)!.Id, $"TryGet({i})");
            TestAssert.Equal(id, MapListCatalog.TryGet(id)!.Id, $"TryGet({id})");
        }

        TestAssert.True(MapListCatalog.TryGet(-1) == null, "TryGet below range returns null");
        TestAssert.True(MapListCatalog.TryGet(MapListCatalog.Count) == null, "TryGet above range returns null");

        // Clamped lookup
        TestAssert.Equal(MapListId.Dm, MapListCatalog.Get(-5).Id, "clamped low index returns Dm");
        TestAssert.Equal(MapListId.Rpg, MapListCatalog.Get(99).Id, "clamped high index returns Rpg");
    }

    [Test]
    public void LoadsEveryImportedEngineList()
    {
        foreach (string id in new[] { "dm", "capture", "ctf", "sp", "concept", "rpg" })
            TestAssert.True(MapListCatalog.TryGet(id) != null, $"map list '{id}' is committed");
    }

    [Test]
    public void CtfPoolIsTheEngineCtfList()
    {
        MapList ctf = MapListCatalog.Get(MapListId.Ctf);

        TestAssert.Equal(110, ctf.Maps.Length, "engine MapList_CTF size");
        TestAssert.Equal("aastha", ctf.Maps[0], "engine order preserved");
        TestAssert.True(Array.IndexOf(ctf.Maps, "bt_falls") >= 0, "ctf map present");
        TestAssert.True(Array.IndexOf(ctf.Maps, "dust2") >= 0, "ctf map present");
        TestAssert.True(Array.IndexOf(ctf.Maps, "metl3") < 0, "deathmatch map absent from the ctf pool");
    }

    [Test]
    public void DeathmatchPoolIsTheEngineDmList()
    {
        MapList dm = MapListCatalog.Get(MapListId.Dm);

        TestAssert.Equal(187, dm.Maps.Length, "engine MapList_DM size");
        TestAssert.True(Array.IndexOf(dm.Maps, "metl3") >= 0, "dm map present");
        TestAssert.True(Array.IndexOf(dm.Maps, "church51") >= 0, "dm map present");
    }

    [Test]
    public void CapturePoolIsTheEngineCaptureList()
    {
        MapList cap = MapListCatalog.Get(MapListId.Capture);

        TestAssert.Equal(117, cap.Maps.Length, "engine MapList_CAP size");
        TestAssert.Equal("aastha", cap.Maps[0], "engine order preserved");
        TestAssert.True(Array.IndexOf(cap.Maps, "campo") >= 0, "capture map present");
        TestAssert.True(Array.IndexOf(cap.Maps, "dust2") >= 0, "capture map present");
    }

    [Test]
    public void SpPoolIsTheEngineSpList()
    {
        MapList sp = MapListCatalog.Get(MapListId.Sp);

        TestAssert.Equal(11, sp.Maps.Length, "engine MapList_SP size");
        TestAssert.Equal("crnsp1", sp.Maps[0], "sp order preserved");
        TestAssert.Equal("skrsp1", sp.Maps[^1], "sp order preserved");
    }

    [Test]
    public void ConceptPoolIsTheEngineConceptList()
    {
        MapList concept = MapListCatalog.Get(MapListId.Concept);

        TestAssert.Equal(10, concept.Maps.Length, "engine MapList_Concept size");
        TestAssert.Equal("firstevermap", concept.Maps[0], "concept order preserved");
        TestAssert.Equal("sauerbraten2", concept.Maps[^1], "concept order preserved");
    }

    [Test]
    public void RpgPoolIsTheEngineRpgList()
    {
        MapList rpg = MapListCatalog.Get(MapListId.Rpg);

        TestAssert.Equal(2, rpg.Maps.Length, "engine MapList_RPG size");
        TestAssert.Equal("k_rpg1", rpg.Maps[0], "rpg map 0");
        TestAssert.Equal("rpg_01", rpg.Maps[1], "rpg map 1");
    }

    [Test]
    public void LoadsOneTresPerMapList()
    {
        foreach (string stem in new[] { "dm", "capture", "ctf", "sp", "concept", "rpg" })
        {
            string path = $"{MapListCatalog.ResourceDir}/{stem}.tres";
            TestAssert.True(ResourceLoader.Exists(path), $"resource exists: {path}");
            TestAssert.True(ResourceLoader.Load<MapList>(path) != null, $"loads as MapList: {path}");
        }
    }

    [Test]
    public void ReloadHotSwapsCatalogue()
    {
        var original = MapListCatalog.All;
        try
        {
            var custom = new MapList[]
            {
                new() { Id = MapListId.Dm, Name = "custom_dm", Maps = new[] { "church51" } },
                new() { Id = MapListId.Capture, Name = "custom_cap", Maps = new[] { "campo" } },
                new() { Id = MapListId.Ctf, Name = "custom_ctf", Maps = new[] { "dust2" } },
                new() { Id = MapListId.Sp, Name = "custom_sp", Maps = new[] { "level9" } },
                new() { Id = MapListId.Concept, Name = "custom_concept", Maps = new[] { "canyon" } },
                new() { Id = MapListId.Rpg, Name = "custom_rpg", Maps = new[] { "k_rpg1" } },
            };

            MapListCatalog.Override(custom);
            TestAssert.Equal("custom_dm", MapListCatalog.Get(MapListId.Dm).Name, "overridden dm name");
            TestAssert.Equal(1, MapListCatalog.Get(MapListId.Dm).Maps.Length, "overridden dm map count");

            MapListCatalog.Reload();
            TestAssert.Equal("ffa", MapListCatalog.Get(MapListId.Dm).Name, "restored default name after reload");
            TestAssert.Equal(187, MapListCatalog.Get(MapListId.Dm).Maps.Length, "restored default map count");
        }
        finally
        {
            MapListCatalog.Override(original);
        }
    }

    [Test]
    public void ResolvesIdFromNameOrFilenameWhenIdOmitted()
    {
        string tmpDir = "user://test_maplist_name_resolve";
        DirAccess.MakeDirAbsolute(tmpDir);

        try
        {
            var omittedId = new MapList
            {
                Id = MapListId.None,
                Name = "ctf",
                Maps = new[] { "mod_map_1", "mod_map_2" }
            };
            ResourceSaver.Save(omittedId, $"{tmpDir}/ctf.tres");

            var loaded = MapListCatalog.Load(MapListCatalog.ResourceDir, tmpDir, null);
            TestAssert.Equal(MapListId.Ctf, loaded[(int)MapListId.Ctf].Id, "id resolved to Ctf");
            TestAssert.Equal(2, loaded[(int)MapListId.Ctf].Maps.Length, "maps overridden by resolved entry");
            TestAssert.Equal("mod_map_1", loaded[(int)MapListId.Ctf].Maps[0], "map content overridden");
        }
        finally
        {
            CleanupDir(tmpDir);
        }
    }

    [Test]
    public void CascadeUserOverrideTakesPrecedence()
    {
        string modDir = "user://test_maplist_cascade_mod";
        string userDir = "user://test_maplist_cascade_user";
        DirAccess.MakeDirAbsolute(modDir);
        DirAccess.MakeDirAbsolute(userDir);

        try
        {
            var modMapList = new MapList
            {
                Id = MapListId.Dm,
                Name = "ffa",
                Maps = new[] { "mod_map" }
            };
            ResourceSaver.Save(modMapList, $"{modDir}/dm.tres");

            var userMapList = new MapList
            {
                Id = MapListId.Dm,
                Name = "ffa",
                Maps = new[] { "user_map_1", "user_map_2" }
            };
            ResourceSaver.Save(userMapList, $"{userDir}/dm.tres");

            var loaded = MapListCatalog.Load(MapListCatalog.ResourceDir, modDir, userDir);
            TestAssert.Equal(2, loaded[(int)MapListId.Dm].Maps.Length, "user override wins over mod override");
            TestAssert.Equal("user_map_1", loaded[(int)MapListId.Dm].Maps[0], "user map 0");
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
        string modDir = "user://test_maplist_catalog_pack";
        DirAccess.MakeDirAbsolute(modDir);

        try
        {
            var pack = new MapListCatalogResource();
            pack.MapLists.Add(new MapList
            {
                Id = MapListId.Ctf,
                Name = "ctf",
                Maps = new[] { "pack_ctf_map" }
            });
            ResourceSaver.Save(pack, $"{modDir}/pack.tres");

            var loaded = MapListCatalog.Load(MapListCatalog.ResourceDir, modDir, null);
            TestAssert.Equal(1, loaded[(int)MapListId.Ctf].Maps.Length, "pack definition applied");
            TestAssert.Equal("pack_ctf_map", loaded[(int)MapListId.Ctf].Maps[0], "pack map applied");
            TestAssert.Equal(187, loaded[(int)MapListId.Dm].Maps.Length, "base dm maps preserved");
        }
        finally
        {
            CleanupDir(modDir);
        }
    }

    [Test]
    public void ForModeSelectsTheEnginePool()
    {
        // ML_ModeType 5/6/7 -> ML_ModeMaps 2 (ctf pool).
        foreach (string id in new[] { "ctf", "insta_ctf", "effic_ctf", "protect", "insta_protect", "effic_protect" })
            TestAssert.Equal(MapListId.Ctf, MapListCatalog.ForMode(GameModeCatalog.TryGet(id))?.Id, $"{id} uses the ctf pool");

        // ML_ModeType 3/4 -> ML_ModeMaps 1 (capture pool); hold is an M_CTF mode
        // but the engine still feeds it from the capture list.
        foreach (string id in new[] { "capture", "regen_capture", "hold", "insta_hold", "effic_hold" })
            TestAssert.Equal(MapListId.Capture, MapListCatalog.ForMode(GameModeCatalog.TryGet(id))?.Id, $"{id} uses the capture pool");

        // ML_ModeType 1/2 (and edit) -> ML_ModeMaps 0; our single picker starts at dm.
        foreach (string id in new[] { "ffa", "insta", "effic", "tactics", "teamplay", "instateam", "coop_edit" })
            TestAssert.Equal(MapListId.Dm, MapListCatalog.ForMode(GameModeCatalog.TryGet(id))?.Id, $"{id} uses the dm pool");
    }

    [Test]
    public void ForModeFallsBackToFlagsForModModes()
    {
        var ctf = new GameModeInfo { Id = "my_ctf", Flags = ModeFlags.Ctf | ModeFlags.Team };
        TestAssert.Equal(MapListId.Ctf, MapListCatalog.ForMode(ctf)?.Id, "unknown ctf mode");

        var hold = new GameModeInfo { Id = "my_hold", Flags = ModeFlags.Ctf | ModeFlags.Hold | ModeFlags.Team };
        TestAssert.Equal(MapListId.Capture, MapListCatalog.ForMode(hold)?.Id, "hold flag wins over ctf");

        var capture = new GameModeInfo { Id = "my_capture", Flags = ModeFlags.Capture | ModeFlags.Team };
        TestAssert.Equal(MapListId.Capture, MapListCatalog.ForMode(capture)?.Id, "unknown capture mode");

        var ffa = new GameModeInfo { Id = "my_ffa" };
        TestAssert.Equal(MapListId.Dm, MapListCatalog.ForMode(ffa)?.Id, "unknown mode defaults to dm");

        TestAssert.Equal(MapListId.Dm, MapListCatalog.ForMode(null)?.Id, "no mode defaults to dm");
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
}
