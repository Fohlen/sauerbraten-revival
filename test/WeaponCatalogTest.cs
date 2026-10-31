using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Audio;
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

    [Test]
    public void ModOverridesTakePrecedence()
    {
        string tempDir = "user://test_weapon_mod_overrides";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            var customRifle = new WeaponInfo
            {
                Id = Gun.Rifle,
                Name = "rifle",
                Damage = 500,
                AttackDelay = 250,
            };
            string overridePath = $"{tempDir}/rifle.tres";
            var err = ResourceSaver.Save(customRifle, overridePath);
            TestAssert.Equal(Error.Ok, err, "override saved");

            var loaded = WeaponCatalog.Load(WeaponCatalog.ResourceDir, modDir: tempDir, userDir: null);
            var rifle = loaded[(int)Gun.Rifle];
            TestAssert.Equal(500, rifle.Damage, "mod override updates damage");
            TestAssert.Equal(250, rifle.AttackDelay, "mod override updates delay");

            // Other weapons remain unaffected
            TestAssert.Equal(WeaponCatalog.Defaults[(int)Gun.Shotgun].Damage, loaded[(int)Gun.Shotgun].Damage, "other weapons remain defaults");
        }
        finally
        {
            if (Godot.FileAccess.FileExists($"{tempDir}/rifle.tres"))
                DirAccess.RemoveAbsolute($"{tempDir}/rifle.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    [Test]
    public void UserOverridesTakePrecedenceOverModOverrides()
    {
        string modDir = "user://test_weapon_tier2_mod";
        string userDir = "user://test_weapon_tier3_user";
        DirAccess.MakeDirAbsolute(modDir);
        DirAccess.MakeDirAbsolute(userDir);
        try
        {
            // Tier 2: mod override sets damage to 300
            var modRifle = new WeaponInfo { Id = Gun.Rifle, Name = "rifle", Damage = 300 };
            ResourceSaver.Save(modRifle, $"{modDir}/rifle.tres");

            // Tier 3: user override sets damage to 999
            var userRifle = new WeaponInfo { Id = Gun.Rifle, Name = "rifle", Damage = 999 };
            ResourceSaver.Save(userRifle, $"{userDir}/rifle.tres");

            var loaded = WeaponCatalog.Load(WeaponCatalog.ResourceDir, modDir: modDir, userDir: userDir);
            TestAssert.Equal(999, loaded[(int)Gun.Rifle].Damage, "Tier 3 user override wins over Tier 2 mod override");
        }
        finally
        {
            if (Godot.FileAccess.FileExists($"{modDir}/rifle.tres")) DirAccess.RemoveAbsolute($"{modDir}/rifle.tres");
            if (Godot.FileAccess.FileExists($"{userDir}/rifle.tres")) DirAccess.RemoveAbsolute($"{userDir}/rifle.tres");
            DirAccess.RemoveAbsolute(modDir);
            DirAccess.RemoveAbsolute(userDir);
        }
    }

    [Test]
    public void CatalogResourceCanBeLoadedInOverrideDir()
    {
        string tempDir = "user://test_weapon_catalog_override";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            var catalog = new WeaponCatalogResource();
            catalog.Weapons.Add(new WeaponInfo { Id = Gun.Pistol, Name = "pistol", Damage = 88 });
            catalog.Weapons.Add(new WeaponInfo { Id = Gun.Chaingun, Name = "chaingun", Damage = 66 });

            string catalogPath = $"{tempDir}/pack.tres";
            var err = ResourceSaver.Save(catalog, catalogPath);
            TestAssert.Equal(Error.Ok, err, "catalog saved");

            var loaded = WeaponCatalog.Load(WeaponCatalog.ResourceDir, modDir: tempDir, userDir: null);
            TestAssert.Equal(88, loaded[(int)Gun.Pistol].Damage, "catalog pistol override applied");
            TestAssert.Equal(66, loaded[(int)Gun.Chaingun].Damage, "catalog chaingun override applied");
        }
        finally
        {
            if (Godot.FileAccess.FileExists($"{tempDir}/pack.tres"))
                DirAccess.RemoveAbsolute($"{tempDir}/pack.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    [Test]
    public void ReloadHotSwapsCatalogue()
    {
        string tempDir = "user://test_weapon_reload";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            var customShotgun = new WeaponInfo { Id = Gun.Shotgun, Name = "shotgun", Damage = 99 };
            ResourceSaver.Save(customShotgun, $"{tempDir}/shotgun.tres");

            WeaponCatalog.Reload(WeaponCatalog.ResourceDir, modDir: tempDir, userDir: null);
            TestAssert.Equal(99, WeaponCatalog.Get(Gun.Shotgun).Damage, "Reload hotswapped active catalogue");
        }
        finally
        {
            // Restore default catalogue state
            WeaponCatalog.Reload(WeaponCatalog.ResourceDir, modDir: null, userDir: null);
            TestAssert.Equal(WeaponCatalog.Defaults[(int)Gun.Shotgun].Damage, WeaponCatalog.Get(Gun.Shotgun).Damage, "defaults restored");

            if (Godot.FileAccess.FileExists($"{tempDir}/shotgun.tres"))
                DirAccess.RemoveAbsolute($"{tempDir}/shotgun.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    [Test]
    public void ResolvesIdFromNameOrFilenameWhenIdOmitted()
    {
        string tempDir = "user://test_weapon_name_resolve";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            // Id omitted (defaults to Gun.None), but Name is "chaingun"
            var unnamedId = new WeaponInfo { Id = Gun.None, Name = "chaingun", Damage = 77 };
            ResourceSaver.Save(unnamedId, $"{tempDir}/any_filename.tres");

            // Both Id and Name omitted, but filename is "rifle.tres"
            var noIdOrName = new WeaponInfo { Id = Gun.None, Name = "", Damage = 333 };
            ResourceSaver.Save(noIdOrName, $"{tempDir}/rifle.tres");

            var loaded = WeaponCatalog.Load(WeaponCatalog.ResourceDir, modDir: tempDir, userDir: null);
            TestAssert.Equal(77, loaded[(int)Gun.Chaingun].Damage, "resolved chaingun via Name");
            TestAssert.Equal(Gun.Chaingun, loaded[(int)Gun.Chaingun].Id, "assigned Gun.Chaingun Id");
            TestAssert.Equal(333, loaded[(int)Gun.Rifle].Damage, "resolved rifle via filename");
            TestAssert.Equal(Gun.Rifle, loaded[(int)Gun.Rifle].Id, "assigned Gun.Rifle Id");
        }
        finally
        {
            if (Godot.FileAccess.FileExists($"{tempDir}/any_filename.tres")) DirAccess.RemoveAbsolute($"{tempDir}/any_filename.tres");
            if (Godot.FileAccess.FileExists($"{tempDir}/rifle.tres")) DirAccess.RemoveAbsolute($"{tempDir}/rifle.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    private sealed class SubclassedWeaponCatalog : SauerbratenRevival.World.Content.ResourceCatalogBase<WeaponInfo, WeaponInfo, Gun>
    {
        public override WeaponInfo[] Defaults => WeaponCatalog.Defaults;
        public override string BasePath => WeaponCatalog.ResourceDir;
        protected override WeaponInfo Convert(WeaponInfo res) => res;
        protected override WeaponInfo CreateEmpty() => new();
    }

    [Test]
    public void ResourceCatalogBaseInheritanceWorks()
    {
        var catalogInstance = new SubclassedWeaponCatalog();
        TestAssert.Equal(WeaponCatalog.Defaults.Length, catalogInstance.All.Length, "inherits active table");
        TestAssert.Equal("shotgun", catalogInstance.All[(int)Gun.Shotgun].Name, "inherits shotgun name");
    }
}
