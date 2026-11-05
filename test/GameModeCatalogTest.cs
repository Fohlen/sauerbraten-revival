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
}
