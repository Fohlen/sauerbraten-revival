using System.Numerics;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Game.Objectives;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class GameModeCatalogTest : GoTestBase
{
    public GameModeCatalogTest(Node testScene) : base(testScene) { }

    [Test]
    public void CatalogLoadsAllBuiltInModes()
    {
        string[] required =
        {
            "ffa", "coop_edit", "teamplay", "insta", "instateam", "effic", "efficteam",
            "tactics", "tactics_team", "ctf", "insta_ctf", "effic_ctf"
        };

        TestAssert.Equal(required.Length, GameModeCatalog.All.Length, "built-in mode count");

        foreach (var id in required)
        {
            var mode = GameModeCatalog.TryGet(id);
            TestAssert.True(mode != null, $"mode '{id}' exists in catalog");
            TestAssert.Equal(id, mode!.Id, $"mode '{id}' id matches");
            TestAssert.True(mode.Objective != null, $"mode '{id}' objective is baked");
        }
    }

    [Test]
    public void WeaponPoolMask_InstaHasOnlyRifle()
    {
        var insta = GameModeCatalog.Get("insta");
        int expectedMask = 1 << (int)Gun.Rifle;
        TestAssert.Equal(expectedMask, insta.WeaponPoolMask, "insta weapon pool mask");
        TestAssert.Equal(1, insta.WeaponPoolCount, "insta weapon pool count is 1");
    }

    [Test]
    public void WeaponPoolMask_EfficOmitsPistol()
    {
        var effic = GameModeCatalog.Get("effic");
        int expectedMask = (1 << (int)Gun.Shotgun) |
                           (1 << (int)Gun.Chaingun) |
                           (1 << (int)Gun.RocketLauncher) |
                           (1 << (int)Gun.Rifle) |
                           (1 << (int)Gun.GrenadeLauncher);

        TestAssert.Equal(expectedMask, effic.WeaponPoolMask, "effic weapon pool mask omits pistol");
        TestAssert.Equal(5, effic.WeaponPoolCount, "effic weapon pool count is 5");
    }

    [Test]
    public void WeaponPoolMask_FfaHasAllSixPlayerWeapons()
    {
        var ffa = GameModeCatalog.Get("ffa");
        int expectedMask = (1 << (int)Gun.Shotgun) |
                           (1 << (int)Gun.Chaingun) |
                           (1 << (int)Gun.RocketLauncher) |
                           (1 << (int)Gun.Rifle) |
                           (1 << (int)Gun.GrenadeLauncher) |
                           (1 << (int)Gun.Pistol);

        TestAssert.Equal(expectedMask, ffa.WeaponPoolMask, "ffa weapon pool mask has 6 guns");
        TestAssert.Equal(6, ffa.WeaponPoolCount, "ffa weapon pool count is 6");
    }

    [Test]
    public void CanSpawnItem_RespectsAllowedItemsMask()
    {
        var ffa = GameModeCatalog.Get("ffa");
        TestAssert.True(ffa.CanSpawnItem(ItemType.Shells), "ffa shells");
        TestAssert.True(ffa.CanSpawnItem(ItemType.Health), "ffa health");
        TestAssert.True(ffa.CanSpawnItem(ItemType.QuadDamage), "ffa quad");

        var insta = GameModeCatalog.Get("insta");
        TestAssert.False(insta.CanSpawnItem(ItemType.Shells), "insta no shells");
        TestAssert.False(insta.CanSpawnItem(ItemType.Health), "insta no health");
        TestAssert.False(insta.CanSpawnItem(ItemType.QuadDamage), "insta no quad");

        var nonAmmoMode = new GameModeInfo { Id = "custom_nonammo", AllowedItems = ItemSpawnMask.NonAmmo };
        nonAmmoMode.Bake();
        TestAssert.False(nonAmmoMode.CanSpawnItem(ItemType.Shells), "nonammo disallows ammo");
        TestAssert.False(nonAmmoMode.CanSpawnItem(ItemType.Rockets), "nonammo disallows ammo");
        TestAssert.True(nonAmmoMode.CanSpawnItem(ItemType.Health), "nonammo allows health");
        TestAssert.True(nonAmmoMode.CanSpawnItem(ItemType.YellowArmour), "nonammo allows armour");
        TestAssert.True(nonAmmoMode.CanSpawnItem(ItemType.QuadDamage), "nonammo allows quad");
    }

    [Test]
    public void CreateObjective_ReturnsCorrectTypedInstance()
    {
        var ctf = GameModeCatalog.Get("ctf");
        var ctfObj = ctf.CreateObjective();
        TestAssert.True(ctfObj is FlagObjective, "ctf creates FlagObjective");

        var ffa = GameModeCatalog.Get("ffa");
        var ffaObj = ffa.CreateObjective();
        TestAssert.True(ffaObj is DeathmatchObjective, "ffa creates DeathmatchObjective");

        var edit = GameModeCatalog.Get("coop_edit");
        var editObj = edit.CreateObjective();
        TestAssert.True(editObj is EditObjective, "coop_edit creates EditObjective");
    }

    [Test]
    public void Bake_MutatorWithSpawnHealthOverride_PreservesHealthAndInheritsMutatorLoadout()
    {
        var mode = new GameModeInfo
        {
            Id = "insta_custom_health",
            Mutator = "insta",
            SpawnHealth = 50,
        };

        TestAssert.True(mode.HasOverride(ModeOverrideMask.SpawnHealth), "SpawnHealth override is flagged");
        TestAssert.False(mode.HasOverride(ModeOverrideMask.SpawnGun), "SpawnGun override is not flagged");

        mode.Bake();

        TestAssert.Equal(50, mode.SpawnHealth, "custom SpawnHealth override preserved");
        TestAssert.Equal(Gun.Rifle, mode.SpawnGun, "SpawnGun inherited from insta mutator");
        TestAssert.Equal(0, mode.SpawnArmour, "SpawnArmour inherited from insta mutator");
        TestAssert.Equal(ItemSpawnMask.None, mode.AllowedItems, "AllowedItems inherited from insta mutator");
        TestAssert.Equal(1 << (int)Gun.Rifle, mode.WeaponPoolMask, "WeaponPoolMask matches insta mutator");
    }

    [Test]
    public void Bake_MutatorWithDamageScaleOverride_PreservesDamageScaleAndInheritsInstaHealth()
    {
        var mode = new GameModeInfo
        {
            Id = "insta_custom_damage",
            Mutator = "insta",
            DamageScale = 2.0f,
        };

        TestAssert.True(mode.HasOverride(ModeOverrideMask.DamageScale), "DamageScale override is flagged");
        TestAssert.False(mode.HasOverride(ModeOverrideMask.SpawnHealth), "SpawnHealth override is not flagged");

        mode.Bake();

        TestAssert.Equal(2.0f, mode.DamageScale, "custom DamageScale override preserved");
        TestAssert.Equal(1, mode.SpawnHealth, "SpawnHealth inherited from insta mutator");
        TestAssert.Equal(Gun.Rifle, mode.SpawnGun, "SpawnGun inherited from insta mutator");
    }

    [Test]
    public void Bake_MutatorWithMultipleOverrides_ComposesCorrectly()
    {
        var mode = new GameModeInfo
        {
            Id = "insta_composite",
            Mutator = "insta",
            SpawnHealth = 75,
            DamageScale = 1.5f,
            SpawnArmour = 50,
            SpawnArmourType = ArmourType.Blue,
            AllowedItems = ItemSpawnMask.Health,
        };

        mode.Bake();

        TestAssert.Equal(75, mode.SpawnHealth, "custom SpawnHealth preserved");
        TestAssert.Equal(1.5f, mode.DamageScale, "custom DamageScale preserved");
        TestAssert.Equal(50, mode.SpawnArmour, "custom SpawnArmour preserved");
        TestAssert.Equal(ArmourType.Blue, mode.SpawnArmourType, "custom SpawnArmourType preserved");
        TestAssert.Equal(ItemSpawnMask.Health, mode.AllowedItems, "custom AllowedItems preserved");
        TestAssert.True(mode.CanSpawnItem(ItemType.Health), "can spawn health pickup");
        TestAssert.False(mode.CanSpawnItem(ItemType.Shells), "cannot spawn ammo pickups");

        // Non-overridden properties must come from mutator
        TestAssert.Equal(Gun.Rifle, mode.SpawnGun, "SpawnGun inherited from insta");
        TestAssert.Equal(100, mode.MaxHealth, "MaxHealth inherited from insta");
        TestAssert.Equal(100, mode.SpawnAmmo[(int)Gun.Rifle], "Rifle ammo inherited from insta");
    }

    [Test]
    public void Bake_MutatorWithSpawnAmmoOverride_ExpandsWeaponPoolMask()
    {
        var instaAmmo = (int[])GameMutatorCatalog.Get("insta").SpawnAmmo.Clone();
        instaAmmo[(int)Gun.Shotgun] = 10;

        var mode = new GameModeInfo
        {
            Id = "insta_with_shotgun",
            Mutator = "insta",
            SpawnAmmo = instaAmmo,
        };

        TestAssert.True(mode.HasOverride(ModeOverrideMask.SpawnAmmo), "SpawnAmmo override flagged via property setter");

        mode.Bake();

        TestAssert.Equal(10, mode.SpawnAmmo[(int)Gun.Shotgun], "custom shotgun ammo preserved");
        TestAssert.Equal(100, mode.SpawnAmmo[(int)Gun.Rifle], "rifle ammo preserved from custom array");
        int expectedMask = (1 << (int)Gun.Rifle) | (1 << (int)Gun.Shotgun);
        TestAssert.Equal(expectedMask, mode.WeaponPoolMask, "weapon pool mask includes both Rifle and Shotgun");
    }

    [Test]
    public void Bake_ObjectiveDefaultArmour_RespectsModeOverride()
    {
        // CTF default spawn armour is 50
        var ctfDefault = new GameModeInfo { Id = "test_ctf_default", ObjectiveId = "ctf" };
        ctfDefault.Bake();
        TestAssert.Equal(50, ctfDefault.SpawnArmour, "CTF default armour is 50");

        // Custom CTF mode overriding spawn armour to 25
        var ctfCustom = new GameModeInfo { Id = "test_ctf_custom", ObjectiveId = "ctf", SpawnArmour = 25 };
        ctfCustom.Bake();
        TestAssert.Equal(25, ctfCustom.SpawnArmour, "custom CTF armour override of 25 is preserved");

        // Custom CTF mode overriding spawn armour to 100
        var ctfHeavy = new GameModeInfo { Id = "test_ctf_heavy", ObjectiveId = "ctf", SpawnArmour = 100 };
        ctfHeavy.Bake();
        TestAssert.Equal(100, ctfHeavy.SpawnArmour, "custom CTF armour override of 100 is preserved");
    }

    [Test]
    public void Bake_ClearOverrides_RestoresFullMutatorInheritance()
    {
        var mode = new GameModeInfo
        {
            Id = "insta_revert",
            Mutator = "insta",
            SpawnHealth = 200,
        };
        mode.Bake();
        TestAssert.Equal(200, mode.SpawnHealth, "initial override baked");

        mode.ClearOverrides();
        TestAssert.Equal(ModeOverrideMask.None, mode.Overrides, "overrides cleared");

        mode.Bake();
        TestAssert.Equal(1, mode.SpawnHealth, "re-baked health restored to mutator default (1)");
    }
}
