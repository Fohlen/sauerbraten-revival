using System;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Content;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the ported pickup items table (fpsgame/game.h:328, entities.cpp, server.cpp)
/// and verifies the ADR 0010 3-tier cascade and hot-path lookups.
/// </summary>
public class ItemCatalogTest : GoTestBase
{
    public ItemCatalogTest(Node testScene) : base(testScene) { }

    [Test]
    public void HasElevenEngineItems()
    {
        TestAssert.Equal(11, ItemCatalog.Defaults.Length, "default items count");
        TestAssert.Equal(11, ItemCatalog.Count, "active catalog count matches defaults");
        TestAssert.Equal(11, ItemStats.Count, "ItemStats count matches ItemCatalog");
    }

    [Test]
    public void RowsAreIndexedAndResolvable()
    {
        for (int i = 0; i < ItemCatalog.Count; i++)
        {
            var id = (ItemType)i;
            int entityType = i + ItemCatalog.FirstItemType;

            TestAssert.Equal(id, ItemCatalog.All[i].Id, $"row {i} Id");
            TestAssert.Equal(id, ItemCatalog.Get(id).Id, $"Get({id})");
            TestAssert.Equal(id, ItemCatalog.Get(i).Id, $"Get({i})");
            TestAssert.Equal(id, ItemCatalog.TryGet(i)!.Id, $"TryGet({i})");
            TestAssert.Equal(id, ItemCatalog.GetByEntityType(entityType).Id, $"GetByEntityType({entityType})");
            TestAssert.Equal(id, ItemCatalog.TryGetByEntityType(entityType)!.Id, $"TryGetByEntityType({entityType})");
            TestAssert.Equal(id, ItemStats.Get(entityType).Id, $"ItemStats.Get({entityType})");
        }

        TestAssert.True(ItemCatalog.TryGet(-1) == null, "TryGet below range returns null");
        TestAssert.True(ItemCatalog.TryGet(ItemCatalog.Count) == null, "TryGet above range returns null");
        TestAssert.True(ItemCatalog.TryGetByEntityType(ItemCatalog.FirstItemType - 1) == null, "TryGetByEntityType below range returns null");
        TestAssert.True(ItemCatalog.TryGetByEntityType(ItemCatalog.FirstItemType + ItemCatalog.Count) == null, "TryGetByEntityType above range returns null");

        // Ammo lookups by gun
        TestAssert.Equal(ItemType.Shells, ItemCatalog.ForGun(Gun.Shotgun).Id, "ForGun Shotgun");
        TestAssert.Equal(ItemType.Bullets, ItemCatalog.ForGun(Gun.Chaingun).Id, "ForGun Chaingun");
        TestAssert.Equal(ItemType.Rockets, ItemCatalog.ForGun(Gun.RocketLauncher).Id, "ForGun RocketLauncher");
        TestAssert.Equal(ItemType.RifleRounds, ItemCatalog.ForGun(Gun.Rifle).Id, "ForGun Rifle");
        TestAssert.Equal(ItemType.Grenades, ItemCatalog.ForGun(Gun.GrenadeLauncher).Id, "ForGun GrenadeLauncher");
        TestAssert.Equal(ItemType.Cartridges, ItemCatalog.ForGun(Gun.Pistol).Id, "ForGun Pistol");
        TestAssert.Equal(ItemType.Shells, ItemStats.ForGun(Gun.Shotgun).Id, "ItemStats.ForGun Shotgun");
    }

    [Test]
    public void ShellsMatchesEngineRow()
    {
        var shells = ItemCatalog.Get(ItemType.Shells);
        TestAssert.Equal("shells", shells.Name, "name");
        TestAssert.Equal("SG", shells.ShortName, "short name");
        TestAssert.Equal(10, shells.Add, "add");
        TestAssert.Equal(30, shells.Max, "max");
        TestAssert.Equal(GameSound.ItemAmmo, shells.Sound, "sound");
        TestAssert.Equal(GameSound.None, shells.VoiceSound, "voice sound");
        TestAssert.Equal((int)Gun.Shotgun, shells.Info, "info gun");
        TestAssert.Equal("ammo/shells", shells.Model, "model");
        TestAssert.Equal(12.0f, shells.PickupRadius, "pickup radius");
        TestAssert.False(shells.DelaySpawn, "delayspawn");
        TestAssert.Equal(4, shells.BaseSpawnTime, "base spawn time");
        TestAssert.True(shells.ScalesWithPlayers, "scales with players");
        TestAssert.Equal(HudIcon.Sg, shells.Icon, "icon");
    }

    [Test]
    public void HealthAndHealthBoostMatchEngineRows()
    {
        var health = ItemCatalog.Get(ItemType.Health);
        TestAssert.Equal("health", health.Name, "health name");
        TestAssert.Equal("H", health.ShortName, "health short name");
        TestAssert.Equal(25, health.Add, "health add");
        TestAssert.Equal(100, health.Max, "health max");
        TestAssert.Equal(GameSound.ItemHealth, health.Sound, "health sound");
        TestAssert.Equal(GameSound.None, health.VoiceSound, "health voice sound");
        TestAssert.Equal(-1, health.Info, "health info");
        TestAssert.Equal("health", health.Model, "health model");
        TestAssert.False(health.DelaySpawn, "health delay spawn");
        TestAssert.Equal(5, health.BaseSpawnTime, "health base spawn time");
        TestAssert.True(health.ScalesWithPlayers, "health scales with players");
        TestAssert.Equal(HudIcon.Health, health.Icon, "health icon");

        var boost = ItemCatalog.Get(ItemType.HealthBoost);
        TestAssert.Equal("healthboost", boost.Name, "boost name");
        TestAssert.Equal("MH", boost.ShortName, "boost short name");
        TestAssert.Equal(100, boost.Add, "boost add");
        TestAssert.Equal(200, boost.Max, "boost max");
        TestAssert.Equal(GameSound.ItemHealth, boost.Sound, "boost sound");
        TestAssert.Equal(GameSound.VBoost, boost.VoiceSound, "boost voice sound");
        TestAssert.Equal(50, boost.Info, "boost info (step)");
        TestAssert.Equal("boost", boost.Model, "boost model");
        TestAssert.True(boost.DelaySpawn, "boost delay spawn");
        TestAssert.Equal(60, boost.BaseSpawnTime, "boost base spawn time");
        TestAssert.False(boost.ScalesWithPlayers, "boost scales with players");
        TestAssert.Equal(HudIcon.Health, boost.Icon, "boost icon");
    }

    [Test]
    public void ArmourAndQuadMatchEngineRows()
    {
        var green = ItemCatalog.Get(ItemType.GreenArmour);
        TestAssert.Equal("greenarmour", green.Name, "green armour name");
        TestAssert.Equal("GA", green.ShortName, "green armour short name");
        TestAssert.Equal(100, green.Add, "green armour add");
        TestAssert.Equal(100, green.Max, "green armour max");
        TestAssert.Equal(GameSound.ItemArmour, green.Sound, "green armour sound");
        TestAssert.Equal((int)ArmourType.Green, green.Info, "green armour type");
        TestAssert.Equal("armor/green", green.Model, "green armour model");
        TestAssert.True(green.DelaySpawn, "green armour delay spawn");
        TestAssert.Equal(20, green.BaseSpawnTime, "green armour base spawn time");
        TestAssert.False(green.ScalesWithPlayers, "green armour scales with players");
        TestAssert.Equal(HudIcon.GreenArmour, green.Icon, "green armour icon");

        var yellow = ItemCatalog.Get(ItemType.YellowArmour);
        TestAssert.Equal("yellowarmour", yellow.Name, "yellow armour name");
        TestAssert.Equal("YA", yellow.ShortName, "yellow armour short name");
        TestAssert.Equal(200, yellow.Add, "yellow armour add");
        TestAssert.Equal(200, yellow.Max, "yellow armour max");
        TestAssert.Equal(GameSound.ItemArmour, yellow.Sound, "yellow armour sound");
        TestAssert.Equal((int)ArmourType.Yellow, yellow.Info, "yellow armour type");
        TestAssert.Equal("armor/yellow", yellow.Model, "yellow armour model");
        TestAssert.True(yellow.DelaySpawn, "yellow armour delay spawn");
        TestAssert.Equal(30, yellow.BaseSpawnTime, "yellow armour base spawn time");
        TestAssert.False(yellow.ScalesWithPlayers, "yellow armour scales with players");
        TestAssert.Equal(HudIcon.YellowArmour, yellow.Icon, "yellow armour icon");

        var quad = ItemCatalog.Get(ItemType.QuadDamage);
        TestAssert.Equal("quaddamage", quad.Name, "quad name");
        TestAssert.Equal("Q", quad.ShortName, "quad short name");
        TestAssert.Equal(20000, quad.Add, "quad add");
        TestAssert.Equal(30000, quad.Max, "quad max");
        TestAssert.Equal(GameSound.ItemPup, quad.Sound, "quad sound");
        TestAssert.Equal(GameSound.VQuad, quad.VoiceSound, "quad voice sound");
        TestAssert.Equal(-1, quad.Info, "quad info");
        TestAssert.Equal("quad", quad.Model, "quad model");
        TestAssert.True(quad.DelaySpawn, "quad delay spawn");
        TestAssert.Equal(70, quad.BaseSpawnTime, "quad base spawn time");
        TestAssert.False(quad.ScalesWithPlayers, "quad scales with players");
        TestAssert.Equal(HudIcon.Quad, quad.Icon, "quad icon");
    }

    [Test]
    public void SpawnTimesMatchEngineFormula()
    {
        var shells = ItemCatalog.Get(ItemType.Shells);
        TestAssert.Equal(16000, shells.CalculateSpawnTime(1), "ammo 1 player (np=4 -> 16s)");
        TestAssert.Equal(12000, shells.CalculateSpawnTime(3), "ammo 3 players (np=3 -> 12s)");
        TestAssert.Equal(8000, shells.CalculateSpawnTime(5), "ammo 5 players (np=2 -> 8s)");

        var health = ItemCatalog.Get(ItemType.Health);
        TestAssert.Equal(20000, health.CalculateSpawnTime(1), "health 1 player (np=4 -> 20s)");
        TestAssert.Equal(15000, health.CalculateSpawnTime(3), "health 3 players (np=3 -> 15s)");
        TestAssert.Equal(10000, health.CalculateSpawnTime(5), "health 5 players (np=2 -> 10s)");

        var ga = ItemCatalog.Get(ItemType.GreenArmour);
        TestAssert.Equal(20000, ga.CalculateSpawnTime(1), "ga 1 player");
        TestAssert.Equal(20000, ga.CalculateSpawnTime(5), "ga 5 players");

        var ya = ItemCatalog.Get(ItemType.YellowArmour);
        TestAssert.Equal(30000, ya.CalculateSpawnTime(1), "ya 1 player");
        TestAssert.Equal(30000, ya.CalculateSpawnTime(5), "ya 5 players");

        var boost = ItemCatalog.Get(ItemType.HealthBoost);
        TestAssert.Equal(60000, boost.CalculateSpawnTime(1), "boost 1 player");
        TestAssert.Equal(60000, boost.CalculateSpawnTime(5), "boost 5 players");

        var quad = ItemCatalog.Get(ItemType.QuadDamage);
        TestAssert.Equal(70000, quad.CalculateSpawnTime(1), "quad 1 player");
        TestAssert.Equal(70000, quad.CalculateSpawnTime(5), "quad 5 players");
    }

    [Test]
    public void LoadsOneTresPerItem()
    {
        for (int i = 0; i < ItemCatalog.Defaults.Length; i++)
        {
            string path = $"{ItemCatalog.ResourceDir}/{ItemCatalog.Defaults[i].Name}.tres";
            if (!ResourceLoader.Exists(path))
            {
                // Ensure canonical resource file exists
                DirAccess.MakeDirAbsolute(ItemCatalog.ResourceDir);
                ResourceSaver.Save(ItemCatalog.Defaults[i], path);
            }

            TestAssert.True(ResourceLoader.Exists(path), $"resource exists: {path}");
            TestAssert.True(ResourceLoader.Load<ItemInfo>(path) != null, $"loads as ItemInfo: {path}");
        }
    }

    [Test]
    public void LoadedResourcesMatchDefaults()
    {
        var loaded = ItemCatalog.Load();
        TestAssert.Equal(ItemCatalog.Defaults.Length, loaded.Length, "row count");
        for (int i = 0; i < loaded.Length; i++)
        {
            var a = ItemCatalog.Defaults[i];
            var b = loaded[i];
            TestAssert.Equal(a.Id, b.Id, $"row {i} Id");
            TestAssert.Equal(a.Name, b.Name, $"row {i} Name");
            TestAssert.Equal(a.ShortName, b.ShortName, $"row {i} ShortName");
            TestAssert.Equal(a.Add, b.Add, $"row {i} Add");
            TestAssert.Equal(a.Max, b.Max, $"row {i} Max");
            TestAssert.Equal(a.Sound, b.Sound, $"row {i} Sound");
            TestAssert.Equal(a.VoiceSound, b.VoiceSound, $"row {i} VoiceSound");
            TestAssert.Equal(a.Info, b.Info, $"row {i} Info");
            TestAssert.Equal(a.Model, b.Model, $"row {i} Model");
            TestAssert.Equal(a.PickupRadius, b.PickupRadius, $"row {i} PickupRadius");
            TestAssert.Equal(a.DelaySpawn, b.DelaySpawn, $"row {i} DelaySpawn");
            TestAssert.Equal(a.BaseSpawnTime, b.BaseSpawnTime, $"row {i} BaseSpawnTime");
            TestAssert.Equal(a.ScalesWithPlayers, b.ScalesWithPlayers, $"row {i} ScalesWithPlayers");
            TestAssert.Equal(a.Icon, b.Icon, $"row {i} Icon");
        }
    }

    [Test]
    public void ModOverridesTakePrecedence()
    {
        string tempDir = "user://test_item_mod_overrides";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            var customHealth = new ItemInfo
            {
                Id = ItemType.Health,
                Name = "health",
                Add = 50,
                Max = 150,
                Model = "custom/health",
                PickupRadius = 24.0f,
            };
            string overridePath = $"{tempDir}/health.tres";
            var err = ResourceSaver.Save(customHealth, overridePath);
            TestAssert.Equal(Error.Ok, err, "override saved");

            var loaded = ItemCatalog.Load(ItemCatalog.ResourceDir, modDir: tempDir, userDir: null);
            var health = loaded[(int)ItemType.Health];
            TestAssert.Equal(50, health.Add, "mod override updates add");
            TestAssert.Equal(150, health.Max, "mod override updates max");
            TestAssert.Equal("custom/health", health.Model, "mod override updates model");
            TestAssert.Equal(24.0f, health.PickupRadius, "mod override updates pickup radius");

            // Other items remain unaffected
            TestAssert.Equal(ItemCatalog.Defaults[(int)ItemType.Shells].Add, loaded[(int)ItemType.Shells].Add, "other items remain defaults");
        }
        finally
        {
            if (FileAccess.FileExists($"{tempDir}/health.tres"))
                DirAccess.RemoveAbsolute($"{tempDir}/health.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    [Test]
    public void UserOverridesTakePrecedenceOverModOverrides()
    {
        string modDir = "user://test_item_tier2_mod";
        string userDir = "user://test_item_tier3_user";
        DirAccess.MakeDirAbsolute(modDir);
        DirAccess.MakeDirAbsolute(userDir);
        try
        {
            // Tier 2: mod override sets add to 75
            var modHealth = new ItemInfo { Id = ItemType.Health, Name = "health", Add = 75 };
            ResourceSaver.Save(modHealth, $"{modDir}/health.tres");

            // Tier 3: user override sets add to 999
            var userHealth = new ItemInfo { Id = ItemType.Health, Name = "health", Add = 999 };
            ResourceSaver.Save(userHealth, $"{userDir}/health.tres");

            var loaded = ItemCatalog.Load(ItemCatalog.ResourceDir, modDir: modDir, userDir: userDir);
            TestAssert.Equal(999, loaded[(int)ItemType.Health].Add, "Tier 3 user override wins over Tier 2 mod override");
        }
        finally
        {
            if (FileAccess.FileExists($"{modDir}/health.tres")) DirAccess.RemoveAbsolute($"{modDir}/health.tres");
            if (FileAccess.FileExists($"{userDir}/health.tres")) DirAccess.RemoveAbsolute($"{userDir}/health.tres");
            DirAccess.RemoveAbsolute(modDir);
            DirAccess.RemoveAbsolute(userDir);
        }
    }

    [Test]
    public void CatalogResourceCanBeLoadedInOverrideDir()
    {
        string tempDir = "user://test_item_catalog_override";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            var catalog = new ItemCatalogResource();
            catalog.Items.Add(new ItemInfo { Id = ItemType.Shells, Name = "shells", Add = 88 });
            catalog.Items.Add(new ItemInfo { Id = ItemType.Bullets, Name = "bullets", Add = 66 });

            string catalogPath = $"{tempDir}/pack.tres";
            var err = ResourceSaver.Save(catalog, catalogPath);
            TestAssert.Equal(Error.Ok, err, "catalog saved");

            var loaded = ItemCatalog.Load(ItemCatalog.ResourceDir, modDir: tempDir, userDir: null);
            TestAssert.Equal(88, loaded[(int)ItemType.Shells].Add, "catalog shells override applied");
            TestAssert.Equal(66, loaded[(int)ItemType.Bullets].Add, "catalog bullets override applied");
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
        string tempDir = "user://test_item_reload";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            var customQuad = new ItemInfo { Id = ItemType.QuadDamage, Name = "quaddamage", Add = 50000 };
            ResourceSaver.Save(customQuad, $"{tempDir}/quaddamage.tres");

            ItemCatalog.Reload(ItemCatalog.ResourceDir, modDir: tempDir, userDir: null);
            TestAssert.Equal(50000, ItemCatalog.Get(ItemType.QuadDamage).Add, "Reload hotswapped active catalogue");
            TestAssert.Equal(50000, ItemStats.Get(EntityType.QuadDamage).Add, "ItemStats reflected hotswapped catalogue");
        }
        finally
        {
            // Restore default catalogue state
            ItemCatalog.Reload(ItemCatalog.ResourceDir, modDir: null, userDir: null);
            TestAssert.Equal(ItemCatalog.Defaults[(int)ItemType.QuadDamage].Add, ItemCatalog.Get(ItemType.QuadDamage).Add, "defaults restored");

            if (FileAccess.FileExists($"{tempDir}/quaddamage.tres"))
                DirAccess.RemoveAbsolute($"{tempDir}/quaddamage.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    [Test]
    public void ResolvesIdFromNameOrFilenameWhenIdOmitted()
    {
        string tempDir = "user://test_item_name_resolve";
        DirAccess.MakeDirAbsolute(tempDir);
        try
        {
            // Id omitted (defaults to ItemType.None), but Name is "rockets"
            var unnamedId = new ItemInfo { Id = ItemType.None, Name = "rockets", Add = 77 };
            ResourceSaver.Save(unnamedId, $"{tempDir}/any_filename.tres");

            // Both Id and Name omitted, but filename is "healthboost.tres"
            var noIdOrName = new ItemInfo { Id = ItemType.None, Name = "", Add = 333 };
            ResourceSaver.Save(noIdOrName, $"{tempDir}/healthboost.tres");

            var loaded = ItemCatalog.Load(ItemCatalog.ResourceDir, modDir: tempDir, userDir: null);
            TestAssert.Equal(77, loaded[(int)ItemType.Rockets].Add, "resolved rockets via Name");
            TestAssert.Equal(ItemType.Rockets, loaded[(int)ItemType.Rockets].Id, "assigned ItemType.Rockets Id");
            TestAssert.Equal(333, loaded[(int)ItemType.HealthBoost].Add, "resolved healthboost via filename");
            TestAssert.Equal(ItemType.HealthBoost, loaded[(int)ItemType.HealthBoost].Id, "assigned ItemType.HealthBoost Id");
        }
        finally
        {
            if (FileAccess.FileExists($"{tempDir}/any_filename.tres")) DirAccess.RemoveAbsolute($"{tempDir}/any_filename.tres");
            if (FileAccess.FileExists($"{tempDir}/healthboost.tres")) DirAccess.RemoveAbsolute($"{tempDir}/healthboost.tres");
            DirAccess.RemoveAbsolute(tempDir);
        }
    }

    private sealed class SubclassedItemCatalog : ResourceCatalogBase<ItemInfo, ItemInfo, ItemType>
    {
        public override ItemInfo[] Defaults => ItemCatalog.Defaults;
        public override string BasePath => ItemCatalog.ResourceDir;
        protected override ItemInfo Convert(ItemInfo res) => res;
        protected override ItemInfo CreateEmpty() => new();
    }

    [Test]
    public void ResourceCatalogBaseInheritanceWorks()
    {
        var catalogInstance = new SubclassedItemCatalog();
        TestAssert.Equal(ItemCatalog.Defaults.Length, catalogInstance.All.Length, "inherits active table");
        TestAssert.Equal("shells", catalogInstance.All[(int)ItemType.Shells].Name, "inherits shells name");
    }
}
