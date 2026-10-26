using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game;

namespace SauerbratenRevival.Tests;

/// <summary>
/// The front-end map pools imported from the engine's <c>data/lists.cfg</c>
/// (SVN r6889) and the mode → pool mapping from
/// <c>data/menus/_utility.cfg</c> (<c>ML_ModeMaps</c>). See
/// <see cref="MapListCatalog"/>.
/// </summary>
public class MapListCatalogTest : GoTestBase
{
    public MapListCatalogTest(Node testScene) : base(testScene) { }

    [Test]
    public void LoadsEveryImportedEngineList()
    {
        foreach (string id in new[] { "dm", "capture", "ctf", "sp", "concept", "rpg" })
            TestAssert.True(MapListCatalog.TryGet(id) != null, $"map list '{id}' is committed");
    }

    [Test]
    public void CtfPoolIsTheEngineCtfList()
    {
        MapList ctf = MapListCatalog.Get("ctf");

        TestAssert.Equal(110, ctf.Maps.Length, "engine MapList_CTF size");
        TestAssert.Equal("aastha", ctf.Maps[0], "engine order preserved");
        TestAssert.True(System.Array.IndexOf(ctf.Maps, "bt_falls") >= 0, "ctf map present");
        TestAssert.True(System.Array.IndexOf(ctf.Maps, "dust2") >= 0, "ctf map present");
        TestAssert.True(System.Array.IndexOf(ctf.Maps, "metl3") < 0, "deathmatch map absent from the ctf pool");
    }

    [Test]
    public void DeathmatchPoolIsTheEngineDmList()
    {
        MapList dm = MapListCatalog.Get("dm");

        TestAssert.Equal(187, dm.Maps.Length, "engine MapList_DM size");
        TestAssert.True(System.Array.IndexOf(dm.Maps, "metl3") >= 0, "dm map present");
        TestAssert.True(System.Array.IndexOf(dm.Maps, "church51") >= 0, "dm map present");
    }

    [Test]
    public void ForModeSelectsTheEnginePool()
    {
        // ML_ModeType 5/6/7 -> ML_ModeMaps 2 (ctf pool).
        foreach (string id in new[] { "ctf", "insta_ctf", "effic_ctf", "protect", "insta_protect", "effic_protect" })
            TestAssert.Equal("ctf", MapListCatalog.ForMode(GameModeCatalog.TryGet(id))?.Id, $"{id} uses the ctf pool");

        // ML_ModeType 3/4 -> ML_ModeMaps 1 (capture pool); hold is an M_CTF mode
        // but the engine still feeds it from the capture list.
        foreach (string id in new[] { "capture", "regen_capture", "hold", "insta_hold", "effic_hold" })
            TestAssert.Equal("capture", MapListCatalog.ForMode(GameModeCatalog.TryGet(id))?.Id, $"{id} uses the capture pool");

        // ML_ModeType 1/2 (and edit) -> ML_ModeMaps 0; our single picker starts at dm.
        foreach (string id in new[] { "ffa", "insta", "effic", "tactics", "teamplay", "instateam", "coop_edit" })
            TestAssert.Equal("dm", MapListCatalog.ForMode(GameModeCatalog.TryGet(id))?.Id, $"{id} uses the dm pool");
    }

    [Test]
    public void ForModeFallsBackToFlagsForModModes()
    {
        var ctf = new GameModeInfo { Id = "my_ctf", Flags = ModeFlags.Ctf | ModeFlags.Team };
        TestAssert.Equal("ctf", MapListCatalog.ForMode(ctf)?.Id, "unknown ctf mode");

        var hold = new GameModeInfo { Id = "my_hold", Flags = ModeFlags.Ctf | ModeFlags.Hold | ModeFlags.Team };
        TestAssert.Equal("capture", MapListCatalog.ForMode(hold)?.Id, "hold flag wins over ctf");

        var capture = new GameModeInfo { Id = "my_capture", Flags = ModeFlags.Capture | ModeFlags.Team };
        TestAssert.Equal("capture", MapListCatalog.ForMode(capture)?.Id, "unknown capture mode");

        var ffa = new GameModeInfo { Id = "my_ffa" };
        TestAssert.Equal("dm", MapListCatalog.ForMode(ffa)?.Id, "unknown mode defaults to dm");

        TestAssert.Equal("dm", MapListCatalog.ForMode(null)?.Id, "no mode defaults to dm");
    }
}
