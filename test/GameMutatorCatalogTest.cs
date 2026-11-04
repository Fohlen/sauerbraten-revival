using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class GameMutatorCatalogTest : GoTestBase
{
    public GameMutatorCatalogTest(Node testScene) : base(testScene) { }

    [Test]
    public void CatalogLoadsAllBuiltInMutators()
    {
        string[] required = { "insta", "effic", "tactics", "capture_loadout", "regen_loadout", "rocket_arena" };
        foreach (var id in required)
        {
            var mut = GameMutatorCatalog.TryGet(id);
            TestAssert.True(mut != null, $"mutator '{id}' exists in catalog");
            TestAssert.Equal(id, mut!.Id, $"mutator '{id}' id matches");
        }
    }

    [Test]
    public void InstagibMutatorMatchesClassicRules()
    {
        var insta = GameMutatorCatalog.Get("insta");
        TestAssert.Equal(1, insta.SpawnHealth, "insta spawn health 1");
        TestAssert.Equal(100, insta.MaxHealth, "insta max health 100");
        TestAssert.Equal(0, insta.SpawnArmour, "insta spawn armour 0");
        TestAssert.Equal(Gun.Rifle, insta.SpawnGun, "insta spawn gun rifle");
        TestAssert.Equal(100, insta.SpawnAmmo[(int)Gun.Rifle], "insta rifle ammo 100");
        TestAssert.Equal(ItemSpawnMask.None, insta.AllowedItems, "insta allowed items none");
    }

    [Test]
    public void EfficiencyMutatorMatchesClassicRules()
    {
        var effic = GameMutatorCatalog.Get("effic");
        TestAssert.Equal(100, effic.SpawnHealth, "effic spawn health 100");
        TestAssert.Equal(100, effic.SpawnArmour, "effic spawn armour 100");
        TestAssert.Equal(ArmourType.Green, effic.SpawnArmourType, "effic green armour");
        TestAssert.Equal(Gun.Chaingun, effic.SpawnGun, "effic spawn gun chaingun");
        TestAssert.Equal(20, effic.SpawnAmmo[(int)Gun.Shotgun], "effic shotgun ammo 20");
        TestAssert.Equal(20, effic.SpawnAmmo[(int)Gun.Chaingun], "effic chaingun ammo 20");
        TestAssert.Equal(10, effic.SpawnAmmo[(int)Gun.RocketLauncher], "effic rl ammo 10");
        TestAssert.Equal(10, effic.SpawnAmmo[(int)Gun.Rifle], "effic rifle ammo 10");
        TestAssert.Equal(20, effic.SpawnAmmo[(int)Gun.GrenadeLauncher], "effic gl ammo 20");
        TestAssert.Equal(0, effic.SpawnAmmo[(int)Gun.Pistol], "effic pistol ammo 0");
        TestAssert.Equal(ItemSpawnMask.None, effic.AllowedItems, "effic allowed items none");
    }

    [Test]
    public void TacticsMutatorMatchesClassicRules()
    {
        var tac = GameMutatorCatalog.Get("tactics");
        TestAssert.Equal(100, tac.SpawnHealth, "tac health");
        TestAssert.Equal(100, tac.SpawnArmour, "tac armour");
        TestAssert.Equal(ArmourType.Green, tac.SpawnArmourType, "tac green armour");
        TestAssert.Equal(Gun.Pistol, tac.SpawnGun, "tac pistol");
        TestAssert.Equal(2, tac.RandomGunsCount, "tac random guns 2");
        TestAssert.Equal(2, tac.RandomGunsAmmoScale, "tac random scale 2");
        TestAssert.Equal(ItemSpawnMask.None, tac.AllowedItems, "tac allowed items none");
    }

    [Test]
    public void CaptureLoadoutAllowsNonAmmoPickups()
    {
        var cap = GameMutatorCatalog.Get("capture_loadout");
        TestAssert.Equal(100, cap.SpawnHealth, "cap health");
        TestAssert.Equal(100, cap.SpawnArmour, "cap armour");
        TestAssert.Equal(ArmourType.Green, cap.SpawnArmourType, "cap green armour");
        TestAssert.Equal(ItemSpawnMask.NonAmmo, cap.AllowedItems, "cap allowed nonammo");
    }
}
