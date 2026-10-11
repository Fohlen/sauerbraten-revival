using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the ported <c>guns[NUMGUNS]</c> table (fpsgame/game.h:348) and the
/// third-person <c>vweps[]</c> names (render.cpp:148). A wrong value here would
/// silently change hit registration and HUD/pellet behaviour, so the engine rows
/// are asserted literally.
/// </summary>
public class WeaponCatalogTest : GoTestBase
{
    public WeaponCatalogTest(Node testScene) : base(testScene) { }

    [Test]
    public void HasEngineGunCountAndPlayerRange()
    {
        TestAssert.Equal(12, Guns.Count, "NUMGUNS");
        TestAssert.Equal(Guns.Count, WeaponCatalog.Count, "table length matches NUMGUNS");

        // GUN_FIST..GUN_PISTOL are the only selectable guns.
        for (int i = 0; i < WeaponCatalog.Count; i++)
            TestAssert.Equal(i <= (int)Gun.Pistol, Guns.IsPlayerSelectable(i), $"selectable gun {i}");
    }

    [Test]
    public void RowsAreIndexedAndResolvable()
    {
        for (int i = 0; i < WeaponCatalog.Count; i++)
        {
            TestAssert.Equal(i, (int)WeaponCatalog.All[i].Id, $"row {i} id");
            TestAssert.Equal(i, (int)WeaponCatalog.Get((Gun)i).Id, $"Get({i})");
            TestAssert.Equal(i, (int)WeaponCatalog.TryGet(i)!.Id, $"TryGet({i})");
        }
        TestAssert.True(WeaponCatalog.TryGet(-1) == null, "try get below range");
        TestAssert.True(WeaponCatalog.TryGet(WeaponCatalog.Count) == null, "try get above range");
    }

    [Test]
    public void RifleMatchesEngineRow()
    {
        // GUN_RIFLE is the insta mode's only weapon — the first consumer.
        var rifle = WeaponCatalog.Get(Gun.Rifle);
        TestAssert.Equal("rifle", rifle.Name, "name");
        TestAssert.Equal("rifle", rifle.File, "hudgun file");
        TestAssert.Equal(1500, rifle.AttackDelay, "attackdelay");
        TestAssert.Equal(100, rifle.Damage, "damage");
        TestAssert.Equal(0, rifle.Spread, "spread");
        TestAssert.Equal(0, rifle.ProjSpeed, "projspeed (hitscan)");
        TestAssert.Equal(30, rifle.KickAmount, "kickamount");
        TestAssert.Equal(2048, rifle.Range, "range");
        TestAssert.Equal(1, rifle.Rays, "rays");
        TestAssert.Equal(80, rifle.HitPush, "hitpush");
        TestAssert.Equal(0, rifle.ExpRad, "exprad");
        TestAssert.Equal(0, rifle.Ttl, "ttl");
        TestAssert.Equal(GameSound.Rifle, rifle.Sound, "sound");
        TestAssert.True(rifle.Hitscan, "rifle is hitscan");
        TestAssert.True(rifle.PlayerSelectable, "rifle is selectable");
    }

    [Test]
    public void ShotgunAndLaunchersMatchEngineRows()
    {
        var sg = WeaponCatalog.Get(Gun.Shotgun);
        TestAssert.Equal(1400, sg.AttackDelay, "shotgun attackdelay");
        TestAssert.Equal(10, sg.Damage, "shotgun damage");
        TestAssert.Equal(400, sg.Spread, "shotgun spread");
        TestAssert.Equal(20, sg.Rays, "shotgun rays");
        TestAssert.Equal(1024, sg.Range, "shotgun range");

        var gl = WeaponCatalog.Get(Gun.GrenadeLauncher);
        TestAssert.Equal(200, gl.ProjSpeed, "gl projspeed");
        TestAssert.Equal(45, gl.ExpRad, "gl exprad");
        TestAssert.Equal(1500, gl.Ttl, "gl ttl");
        TestAssert.True(!gl.Hitscan, "gl is a projectile");

        // The engine's projectile pseudo-guns reuse the launcher particle types.
        TestAssert.Equal(1, WeaponCatalog.Get(Gun.Fireball).Part, "fireball part");
        TestAssert.Equal(2, WeaponCatalog.Get(Gun.Iceball).Part, "iceball part");
        TestAssert.Equal(3, WeaponCatalog.Get(Gun.Slimeball).Part, "slimeball part");
    }

    [Test]
    public void OnlyPlayerGunsHaveModels()
    {
        for (int i = 0; i < WeaponCatalog.Count; i++)
        {
            var w = WeaponCatalog.All[i];
            bool player = i <= (int)Gun.Pistol;
            TestAssert.Equal(player, w.HasModel, $"row {i} ({w.Name}) has a hudgun file");
        }
        TestAssert.Equal(GameSound.None, WeaponCatalog.Get(Gun.Barrel).Sound, "barrel has no attack sound");
    }

    [Test]
    public void LoadsOneTresPerGun()
    {
        // Every default row has an authored resource the loader will pick up.
        for (int i = 0; i < WeaponCatalog.Defaults.Length; i++)
        {
            string path = $"{WeaponCatalog.ResourceDir}/{WeaponCatalog.Defaults[i].Name}.tres";
            TestAssert.True(ResourceLoader.Exists(path), $"resource exists: {path}");
            TestAssert.True(ResourceLoader.Load<WeaponInfo>(path) != null, $"loads as WeaponInfo: {path}");
        }
    }

    [Test]
    public void LoadedResourcesMatchDefaults()
    {
        // Two representations exist (code defaults + authored .tres); this keeps
        // them from drifting. The engine-row tests above lock the active catalogue.
        var loaded = WeaponCatalog.Load();
        TestAssert.Equal(WeaponCatalog.Defaults.Length, loaded.Length, "row count");
        for (int i = 0; i < loaded.Length; i++)
        {
            var a = WeaponCatalog.Defaults[i];
            var b = loaded[i];
            TestAssert.Equal(a.Id, b.Id, $"row {i} Id");
            TestAssert.Equal(a.Sound, b.Sound, $"row {i} Sound");
            TestAssert.Equal(a.AttackDelay, b.AttackDelay, $"row {i} AttackDelay");
            TestAssert.Equal(a.Damage, b.Damage, $"row {i} Damage");
            TestAssert.Equal(a.Spread, b.Spread, $"row {i} Spread");
            TestAssert.Equal(a.ProjSpeed, b.ProjSpeed, $"row {i} ProjSpeed");
            TestAssert.Equal(a.KickAmount, b.KickAmount, $"row {i} KickAmount");
            TestAssert.Equal(a.Range, b.Range, $"row {i} Range");
            TestAssert.Equal(a.Rays, b.Rays, $"row {i} Rays");
            TestAssert.Equal(a.HitPush, b.HitPush, $"row {i} HitPush");
            TestAssert.Equal(a.ExpRad, b.ExpRad, $"row {i} ExpRad");
            TestAssert.Equal(a.Ttl, b.Ttl, $"row {i} Ttl");
            TestAssert.Equal(a.Name, b.Name, $"row {i} Name");
            TestAssert.Equal(a.File, b.File, $"row {i} File");
            TestAssert.Equal(a.Part, b.Part, $"row {i} Part");
        }
    }

    [Test]
    public void VwepsMatchRenderTable()
    {
        string[] expected =
        {
            "vwep/fist", "vwep/shotg", "vwep/chaing", "vwep/rocket",
            "vwep/rifle", "vwep/gl", "vwep/pistol",
        };
        TestAssert.Equal(expected.Length, WeaponCatalog.Vweps.Length, "vweps count");
        for (int i = 0; i < expected.Length; i++)
            TestAssert.Equal(expected[i], WeaponCatalog.Vwep((Gun)i)!, $"vwep {i}");
        TestAssert.True(WeaponCatalog.Vwep(Gun.Fireball) == null, "pseudo-guns have no vwep");
    }
}
