using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Tests item pickup eligibility (<c>canpickup</c>), stat changes (<c>pickup</c>),
/// quad damage countdown (<c>checkquad</c>), item respawn timers (<c>spawntime</c>, <c>delayspawn</c>),
/// mode-based filtering (<c>canspawnitem</c>), and collision pickup checks (<c>checkitems</c>).
/// </summary>
public class ItemPickupTest : GoTestBase
{
    public ItemPickupTest(Node testScene) : base(testScene) { }

    [Test]
    public void HealthPickupLogic()
    {
        var s = new PlayerState();
        s.Respawn(); // 100/100 HP

        TestAssert.False(s.CanPickup(EntityType.Health), "Cannot pickup health at max HP");

        s.Health = 70;
        TestAssert.True(s.CanPickup(EntityType.Health), "Can pickup health when damaged");

        s.Pickup(EntityType.Health); // Adds 25, clamped to MaxHealth 100
        TestAssert.Equal(95, s.Health, "Health increased by 25");

        s.Pickup(EntityType.Health); // Adds 25, clamped to 100
        TestAssert.Equal(100, s.Health, "Health clamped to MaxHealth 100");
    }

    [Test]
    public void HealthBoostPickupLogic()
    {
        var s = new PlayerState();
        s.Respawn(); // 100/100 HP

        // HealthBoost increases MaxHealth by 50 (up to 200) and adds 100 HP (up to MaxHealth)
        TestAssert.True(s.CanPickup(EntityType.HealthBoost), "Can pickup boost even at 100 HP because maxhealth < 200");

        s.Pickup(EntityType.HealthBoost);
        TestAssert.Equal(150, s.MaxHealth, "MaxHealth increased to 150");
        TestAssert.Equal(150, s.Health, "Health increased to 150");

        s.Pickup(EntityType.HealthBoost);
        TestAssert.Equal(200, s.MaxHealth, "MaxHealth clamped to 200");
        TestAssert.Equal(200, s.Health, "Health clamped to 200");

        TestAssert.False(s.CanPickup(EntityType.HealthBoost), "Cannot pickup boost when at 200/200");

        s.Health = 180;
        TestAssert.True(s.CanPickup(EntityType.HealthBoost), "Can pickup boost when damaged even if maxhealth is 200");
    }

    [Test]
    public void ArmourPickupLogic()
    {
        var s = new PlayerState();
        s.Respawn(); // 0 armour, blue type

        // Blue spawn armour allows picking up green or yellow
        TestAssert.True(s.CanPickup(EntityType.GreenArmour), "Can pickup green armour with blue type");
        TestAssert.True(s.CanPickup(EntityType.YellowArmour), "Can pickup yellow armour with blue type");

        s.Pickup(EntityType.GreenArmour);
        TestAssert.Equal(100, s.Armour, "Green armour set to 100");
        TestAssert.Equal(ArmourType.Green, s.ArmourType, "ArmourType is Green");
        TestAssert.False(s.CanPickup(EntityType.GreenArmour), "Cannot pickup green armour when already at 100 green");

        // Can pickup yellow armour over green armour
        TestAssert.True(s.CanPickup(EntityType.YellowArmour), "Can pickup yellow armour over green armour");
        s.Pickup(EntityType.YellowArmour);
        TestAssert.Equal(200, s.Armour, "Yellow armour set to 200");
        TestAssert.Equal(ArmourType.Yellow, s.ArmourType, "ArmourType is Yellow");

        // When possessing yellow armour >= 100, green armour cannot be picked up (absorbs less)
        TestAssert.False(s.CanPickup(EntityType.GreenArmour), "Cannot pickup green armour when yellow armour >= 100");

        // When yellow armour drops below 100, green armour can be picked up and changes type to Green
        s.Armour = 50;
        TestAssert.True(s.CanPickup(EntityType.GreenArmour), "Can pickup green armour when yellow armour < 100");
        s.Pickup(EntityType.GreenArmour);
        TestAssert.Equal(100, s.Armour, "Armour clamped to 100 for green");
        TestAssert.Equal(ArmourType.Green, s.ArmourType, "ArmourType downgraded to Green");
    }

    [Test]
    public void QuadDamagePickupAndCountdown()
    {
        var s = new PlayerState();
        s.Respawn();

        TestAssert.Equal(0, s.QuadMillis, "Initial quad is 0");
        TestAssert.True(s.CanPickup(EntityType.QuadDamage), "Can pickup quad");

        s.Pickup(EntityType.QuadDamage);
        TestAssert.Equal(20000, s.QuadMillis, "Quad added 20000 ms");

        s.Pickup(EntityType.QuadDamage);
        TestAssert.Equal(30000, s.QuadMillis, "Quad clamped to max 30000 ms");
        TestAssert.False(s.CanPickup(EntityType.QuadDamage), "Cannot pickup quad when at max 30000 ms");

        bool expired = s.CheckQuad(10000);
        TestAssert.False(expired, "Quad not expired after 10s");
        TestAssert.Equal(20000, s.QuadMillis, "20s remaining");

        expired = s.CheckQuad(20000);
        TestAssert.True(expired, "Quad expired exactly when timer hit 0");
        TestAssert.Equal(0, s.QuadMillis, "Quad reset to 0");

        expired = s.CheckQuad(5000);
        TestAssert.False(expired, "CheckQuad returns false when already 0");
    }

    [Test]
    public void AmmoPickupLogic()
    {
        var s = new PlayerState();
        s.Respawn();

        // Shotgun ammo (I_SHELLS == 8, Gun.Shotgun == 1, Add == 10, Max == 30)
        TestAssert.Equal(0, s.Ammo[(int)Gun.Shotgun], "SG initial ammo 0");
        TestAssert.True(s.CanPickup(EntityType.Shells), "Can pickup shells");
        TestAssert.False(s.HasMaxAmmo(EntityType.Shells), "Does not have max shells");

        s.Pickup(EntityType.Shells);
        TestAssert.Equal(10, s.Ammo[(int)Gun.Shotgun], "SG ammo 10");

        s.Pickup(EntityType.Shells);
        s.Pickup(EntityType.Shells);
        TestAssert.Equal(30, s.Ammo[(int)Gun.Shotgun], "SG ammo max 30");
        TestAssert.True(s.HasMaxAmmo(EntityType.Shells), "Has max shells");
        TestAssert.False(s.CanPickup(EntityType.Shells), "Cannot pickup shells at max ammo");

        // Pistol ammo (I_CARTRIDGES == 13, Gun.Pistol == 6, Add == 30, Max == 120)
        TestAssert.Equal(0, s.Ammo[(int)Gun.Pistol], "Pistol initial ammo 0");
        s.Pickup(EntityType.Cartridges);
        TestAssert.Equal(30, s.Ammo[(int)Gun.Pistol], "Pistol ammo 30");
    }

    [Test]
    public void SpawnTimeAndDelayMatchesEngine()
    {
        // 1 player (np < 3 -> np = 4): ammo = 16s, health = 20s
        TestAssert.Equal(16000, ItemManager.SpawnTime(EntityType.Shells, numPlayers: 1), "Ammo spawntime (1 player)");
        TestAssert.Equal(20000, ItemManager.SpawnTime(EntityType.Health, numPlayers: 1), "Health spawntime (1 player)");

        // 3 players (np = 3): ammo = 12s, health = 15s
        TestAssert.Equal(12000, ItemManager.SpawnTime(EntityType.Shells, numPlayers: 3), "Ammo spawntime (3 players)");
        TestAssert.Equal(15000, ItemManager.SpawnTime(EntityType.Health, numPlayers: 3), "Health spawntime (3 players)");

        // 5 players (np > 4 -> np = 2): ammo = 8s, health = 10s
        TestAssert.Equal(8000, ItemManager.SpawnTime(EntityType.Shells, numPlayers: 5), "Ammo spawntime (5 players)");
        TestAssert.Equal(10000, ItemManager.SpawnTime(EntityType.Health, numPlayers: 5), "Health spawntime (5 players)");

        // Fixed respawn times independent of player count
        TestAssert.Equal(20000, ItemManager.SpawnTime(EntityType.GreenArmour, numPlayers: 1), "Green armour 20s");
        TestAssert.Equal(30000, ItemManager.SpawnTime(EntityType.YellowArmour, numPlayers: 1), "Yellow armour 30s");
        TestAssert.Equal(60000, ItemManager.SpawnTime(EntityType.HealthBoost, numPlayers: 1), "Health boost 60s");
        TestAssert.Equal(70000, ItemManager.SpawnTime(EntityType.QuadDamage, numPlayers: 1), "Quad damage 70s");

        // DelaySpawn rules
        TestAssert.False(ItemManager.DelaySpawn(EntityType.Shells), "Shells not delayed");
        TestAssert.False(ItemManager.DelaySpawn(EntityType.Health), "Health not delayed");
        TestAssert.True(ItemManager.DelaySpawn(EntityType.GreenArmour), "Green armour delayed");
        TestAssert.True(ItemManager.DelaySpawn(EntityType.YellowArmour), "Yellow armour delayed");
        TestAssert.True(ItemManager.DelaySpawn(EntityType.HealthBoost), "Boost delayed");
        TestAssert.True(ItemManager.DelaySpawn(EntityType.QuadDamage), "Quad delayed");
    }

    [Test]
    public void ModeBasedItemFiltering()
    {
        // NoItems mode (e.g. instagib)
        TestAssert.False(ItemManager.CanSpawnItem(EntityType.Shells, ModeFlags.NoItems), "No items in NoItems mode");
        TestAssert.False(ItemManager.CanSpawnItem(EntityType.Health, ModeFlags.NoItems), "No health in NoItems mode");
        TestAssert.False(ItemManager.CanSpawnItem(EntityType.QuadDamage, ModeFlags.NoItems), "No quad in NoItems mode");

        // NoAmmo mode (e.g. efficiency)
        TestAssert.False(ItemManager.CanSpawnItem(EntityType.Shells, ModeFlags.NoAmmo), "No ammo in NoAmmo mode");
        TestAssert.False(ItemManager.CanSpawnItem(EntityType.Cartridges, ModeFlags.NoAmmo), "No cartridges in NoAmmo mode");
        TestAssert.True(ItemManager.CanSpawnItem(EntityType.Health, ModeFlags.NoAmmo), "Health allowed in NoAmmo mode");
        TestAssert.True(ItemManager.CanSpawnItem(EntityType.GreenArmour, ModeFlags.NoAmmo), "Armour allowed in NoAmmo mode");
        TestAssert.True(ItemManager.CanSpawnItem(EntityType.QuadDamage, ModeFlags.NoAmmo), "Quad allowed in NoAmmo mode");

        // Standard FFA
        TestAssert.True(ItemManager.CanSpawnItem(EntityType.Shells, ModeFlags.None), "Ammo allowed in FFA");
        TestAssert.True(ItemManager.CanSpawnItem(EntityType.Health, ModeFlags.None), "Health allowed in FFA");
        TestAssert.True(ItemManager.CanSpawnItem(EntityType.GreenArmour, ModeFlags.None), "Armour allowed in FFA");
    }

    [Test]
    public void ItemManagerSpawningAndAdvancing()
    {
        var manager = new ItemManager { AutoAdvance = false };
        var itemHealth = new Item
        {
            ItemType = EntityType.Health,
            BasePosition = new Vector3(10, 20, 30),
            Position = new Vector3(10, 20, 30),
        };
        var itemQuad = new Item
        {
            ItemType = EntityType.QuadDamage,
            BasePosition = new Vector3(40, 50, 60),
            Position = new Vector3(40, 50, 60),
        };
        manager.AddItem(itemHealth);
        manager.AddItem(itemQuad);

        // Standard FFA spawn (delayed quad, immediate health)
        manager.SpawnItems(GameModeCatalog.Get("ffa"), force: false);
        TestAssert.True(itemHealth.Spawned, "Health spawned immediately");
        TestAssert.False(itemQuad.Spawned, "Quad delayed");
        TestAssert.Equal(70000, itemQuad.SpawnTime, "Quad spawntime initialized to 70s");

        // Advance 30 seconds
        manager.Advance(30000, 30000);
        TestAssert.False(itemQuad.Spawned, "Quad still not spawned after 30s");
        TestAssert.Equal(40000, itemQuad.SpawnTime, "Quad 40s remaining");

        // Advance remaining 40 seconds
        bool quadSpawnedEventFired = false;
        manager.ItemSpawned += item =>
        {
            if (item.ItemType == EntityType.QuadDamage) quadSpawnedEventFired = true;
        };
        manager.Advance(40000, 70000);
        TestAssert.True(itemQuad.Spawned, "Quad spawned after 70s");
        TestAssert.Equal(0, itemQuad.SpawnTime, "Quad spawntime reset to 0");
        TestAssert.True(quadSpawnedEventFired, "ItemSpawned event fired");

        manager.Free();
    }

    [Test]
    public void ItemManagerSeparatePhysicsAndVisualUpdates()
    {
        var manager = new ItemManager { AutoAdvance = false };
        var itemQuad = new Item
        {
            ItemType = EntityType.QuadDamage,
            BasePosition = new Vector3(40, 50, 60),
            Position = new Vector3(40, 50, 60),
        };
        manager.AddItem(itemQuad);
        manager.SpawnItems(GameModeCatalog.Get("ffa"), force: false);
        TestAssert.False(itemQuad.Spawned, "Quad delayed");
        TestAssert.Equal(70000, itemQuad.SpawnTime, "Quad spawntime initialized to 70s");

        // Physics tick (UpdateRespawns) steps countdown without changing visual bobbing
        manager.UpdateRespawns(30000);
        TestAssert.Equal(40000, itemQuad.SpawnTime, "Quad 40s remaining");
        TestAssert.Equal(new Vector3(40, 50, 60), itemQuad.Position, "Position unaffected by UpdateRespawns");

        manager.UpdateRespawns(40000);
        TestAssert.True(itemQuad.Spawned, "Quad spawned");
        TestAssert.Equal(0, itemQuad.SpawnTime, "Quad spawntime 0");

        // Visual update (UpdateVisual) computes bobbing and yaw
        manager.UpdateVisual(5000);
        TestAssert.True(itemQuad.Visible, "Quad is visible");
        float expectedBob = 1.0f + Mathf.Sin((5000 / 100.0f) + 40 + 50) / 20.0f;
        TestAssert.True(Mathf.Abs(itemQuad.Position.Z - (60 + expectedBob)) < 0.001f, "Bobbing applied by UpdateVisual");

        manager.Free();
    }

    [Test]
    public void ItemPickupInteraction()
    {
        var manager = new ItemManager { AutoAdvance = false };
        var item = new Item
        {
            ItemType = EntityType.Health,
            BasePosition = new Vector3(100, 100, 10),
            Position = new Vector3(100, 100, 10),
            Spawned = true,
            PickupRadius = 12.0f,
        };
        manager.AddItem(item);

        var player = new MapPlayer();
        player.Combat.Respawn();
        player.Combat.Health = 50; // Damaged, so can pickup health
        player.Entity.O = new Vector3(100, 100, 10 + player.Entity.EyeHeight); // Feet at (100, 100, 10)

        Item? picked = null;
        manager.ItemPickedUp += (p, it) => picked = it;

        // Player standing directly at item base position
        var result = manager.CheckPickups(player, 1000);
        TestAssert.True(result == item, "Item was picked up");
        TestAssert.True(picked == item, "ItemPickedUp event fired");
        TestAssert.False(item.Spawned, "Item is now despawned");
        TestAssert.True(item.SpawnTime > 0, "Item has respawn countdown");
        TestAssert.Equal(75, player.Combat.Health, "Player health increased to 75");

        // Second check returns null since item is despawned
        result = manager.CheckPickups(player, 1000);
        TestAssert.True(result == null, "Cannot pickup despawned item");

        manager.Free();
        player.Free();
    }

    [Test]
    public void OutOfRangePlayerCannotPickup()
    {
        var manager = new ItemManager { AutoAdvance = false };
        var item = new Item
        {
            ItemType = EntityType.Health,
            BasePosition = new Vector3(0, 0, 0),
            Position = new Vector3(0, 0, 0),
            Spawned = true,
            PickupRadius = 12.0f,
        };
        manager.AddItem(item);

        var player = new MapPlayer();
        player.Combat.Respawn();
        player.Combat.Health = 50;
        // Feet at distance 15 (> 12)
        player.Entity.O = new Vector3(15, 0, player.Entity.EyeHeight);

        var result = manager.CheckPickups(player, 1000);
        TestAssert.True(result == null, "Player out of range cannot pickup");
        TestAssert.True(item.Spawned, "Item remains spawned");

        manager.Free();
        player.Free();
    }

    [Test]
    public void ItemVisualBobbingAndSpinning()
    {
        var item = new Item
        {
            ItemType = EntityType.Health,
            BasePosition = new Vector3(10, 20, 30),
            Spawned = true,
        };

        // At lastMillis = 0
        item.UpdateVisual(0);
        TestAssert.True(item.Visible, "Spawned item is visible");
        float expectedBob0 = 1.0f + Mathf.Sin(10 + 20) / 20.0f;
        TestAssert.True(Mathf.Abs(item.Position.Z - (30 + expectedBob0)) < 1e-4f, "Z position bobs correctly at t=0");

        // At lastMillis = 1000 (100 degrees yaw)
        item.UpdateVisual(1000);
        float expectedBob1000 = 1.0f + Mathf.Sin((1000 / 100.0f) + 10 + 20) / 20.0f;
        TestAssert.True(Mathf.Abs(item.Position.Z - (30 + expectedBob1000)) < 1e-4f, "Z position bobs correctly at t=1000");

        // When despawned, Visible is false and position returns to BasePosition
        item.Spawned = false;
        item.UpdateVisual(2000);
        TestAssert.False(item.Visible, "Despawned item is hidden");
        TestAssert.Equal(item.BasePosition, item.Position, "Position returns to BasePosition when despawned");

        item.Free();
    }
}
