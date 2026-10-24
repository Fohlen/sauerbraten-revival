using System;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the per-player weapon state (the ported weapon half of
/// <c>fpsstate</c>, fpsgame/game.h:367) and the mode loadouts of
/// <c>spawnstate()</c> (fpsgame/game.h:450). A wrong number here silently changes
/// every spawn, so the engine rows and itemstat values are asserted literally.
/// </summary>
public class PlayerStateTest : GoTestBase
{
    public PlayerStateTest(Node testScene) : base(testScene) { }

    private static PlayerState Fresh() => new();

    [Test]
    public void RespawnSetsEngineDefaults()
    {
        var s = Fresh();
        // Dirty every field so Respawn is shown to reset it.
        s.Health = 1;
        s.MaxHealth = 1;
        s.Armour = 200;
        s.ArmourType = ArmourType.Yellow;
        s.QuadMillis = 30000;
        s.GunSelect = (int)Gun.Rifle;
        s.GunWait = 42;
        s.LastAction = 999;
        s.LastAttackGun = (int)Gun.RocketLauncher;
        s.LastPain = 888;
        s.Attacking = true;
        s.Ammo[(int)Gun.Rifle] = 100;

        s.Respawn();

        TestAssert.Equal(100, s.MaxHealth, "maxhealth");
        TestAssert.Equal(100, s.Health, "health");
        TestAssert.Equal(0, s.Armour, "armour");
        TestAssert.Equal(ArmourType.Blue, s.ArmourType, "armourtype");
        TestAssert.Equal(0, s.QuadMillis, "quadmillis");
        TestAssert.Equal((int)Gun.Pistol, s.GunSelect, "gunselect");
        TestAssert.Equal(0, s.GunWait, "gunwait");
        TestAssert.Equal(0, s.LastAction, "lastaction");
        TestAssert.Equal((int)Gun.Pistol, s.LastAttackGun, "lastattackgun");
        TestAssert.Equal(0, s.LastPain, "lastpain");
        TestAssert.True(!s.Attacking, "attacking");
        for (int i = 0; i < Guns.Count; i++)
            TestAssert.Equal(i == (int)Gun.Fist ? 1 : 0, s.Ammo[i], $"ammo[{i}]");
    }

    [Test]
    public void InstaCtfSpawnsWithRifle()
    {
        var s = Fresh();
        s.Spawn(GameModeCatalog.Get("insta_ctf"));

        TestAssert.Equal(1, s.Health, "health (one-shot)");
        TestAssert.Equal(100, s.MaxHealth, "maxhealth");
        TestAssert.Equal(0, s.Armour, "armour");
        TestAssert.Equal((int)Gun.Rifle, s.GunSelect, "gunselect");
        TestAssert.Equal(100, s.Ammo[(int)Gun.Rifle], "rifle ammo");
        TestAssert.Equal(1, s.Ammo[(int)Gun.Fist], "fist ammo");
        for (int i = 0; i < Guns.Count; i++)
            if (i != (int)Gun.Rifle && i != (int)Gun.Fist)
                TestAssert.Equal(0, s.Ammo[i], $"ammo[{i}] is empty");
    }

    [Test]
    public void EfficiencyCtfSpawnsWithBaseLoadout()
    {
        var s = Fresh();
        s.Spawn(GameModeCatalog.Get("effic_ctf"));

        TestAssert.Equal(100, s.Health, "health");
        TestAssert.Equal(100, s.Armour, "armour");
        TestAssert.Equal(ArmourType.Green, s.ArmourType, "armourtype");
        TestAssert.Equal((int)Gun.Chaingun, s.GunSelect, "gunselect");

        // baseammo(1..5) = itemstats.add*2, then the chaingun is halved.
        TestAssert.Equal(20, s.Ammo[(int)Gun.Shotgun], "SG");
        TestAssert.Equal(20, s.Ammo[(int)Gun.Chaingun], "CG (halved)");
        TestAssert.Equal(10, s.Ammo[(int)Gun.RocketLauncher], "RL");
        TestAssert.Equal(10, s.Ammo[(int)Gun.Rifle], "Rifle");
        TestAssert.Equal(20, s.Ammo[(int)Gun.GrenadeLauncher], "GL");
        TestAssert.Equal(0, s.Ammo[(int)Gun.Pistol], "pistol empty");
        TestAssert.Equal(1, s.Ammo[(int)Gun.Fist], "fist");
    }

    [Test]
    public void FfaAndCoopEditSpawnWithPistolAndGl()
    {
        foreach (string id in new[] { "ffa", "coop_edit" })
        {
            var s = Fresh();
            s.Spawn(GameModeCatalog.Get(id));

            TestAssert.Equal(100, s.Health, $"{id} health");
            TestAssert.Equal(25, s.Armour, $"{id} armour");
            TestAssert.Equal(ArmourType.Blue, s.ArmourType, $"{id} armourtype");
            TestAssert.Equal((int)Gun.Pistol, s.GunSelect, $"{id} gunselect");
            TestAssert.Equal(40, s.Ammo[(int)Gun.Pistol], $"{id} pistol");
            TestAssert.Equal(1, s.Ammo[(int)Gun.GrenadeLauncher], $"{id} gl");
            TestAssert.Equal(1, s.Ammo[(int)Gun.Fist], $"{id} fist");
        }
    }

    [Test]
    public void BaseAmmoMatchesItemStats()
    {
        var s = Fresh();
        for (int gun = (int)Gun.Shotgun; gun <= (int)Gun.Pistol; gun++)
        {
            s.BaseAmmo(gun);
            TestAssert.Equal(ItemStats.ForGun((Gun)gun).Add * 2, s.Ammo[gun], $"baseammo {gun}");
        }

        s.BaseAmmo((int)Gun.Shotgun, k: 1);
        TestAssert.Equal(ItemStats.ForGun(Gun.Shotgun).Add, s.Ammo[(int)Gun.Shotgun], "baseammo k=1");

        s.BaseAmmo((int)Gun.Shotgun, scale: 2);
        TestAssert.Equal(ItemStats.ForGun(Gun.Shotgun).Add * 2 / 2, s.Ammo[(int)Gun.Shotgun], "baseammo scale=2");
    }

    [Test]
    public void AddAmmoClampsToMax()
    {
        var s = Fresh();
        var sg = ItemStats.ForGun(Gun.Shotgun);
        s.Ammo[(int)Gun.Shotgun] = sg.Max - 1;
        s.AddAmmo((int)Gun.Shotgun);
        TestAssert.Equal(sg.Max, s.Ammo[(int)Gun.Shotgun], "clamped at max");
    }

    [Test]
    public void HasAmmoHonoursExcludeAndEmpty()
    {
        var s = Fresh();
        s.Ammo[(int)Gun.Rifle] = 0;
        s.Ammo[(int)Gun.Pistol] = 5;

        TestAssert.True(!s.HasAmmo((int)Gun.Rifle), "empty gun");
        TestAssert.True(s.HasAmmo((int)Gun.Pistol), "loaded gun");
        TestAssert.True(!s.HasAmmo((int)Gun.Pistol, exclude: (int)Gun.Pistol), "excluded gun");
        TestAssert.True(!s.HasAmmo(-1), "below range");
        TestAssert.True(!s.HasAmmo(Guns.Count), "above range");
    }

    [Test]
    public void CatalogExposesAllModes()
    {
        TestAssert.Equal(20, GameModeCatalog.All.Length, "All 20 mode count");
        string[] expected =
        {
            "ffa", "coop_edit", "teamplay", "insta", "instateam", "effic", "efficteam",
            "tactics", "tactics_team", "capture", "regen_capture", "ctf", "insta_ctf",
            "effic_ctf", "protect", "insta_protect", "effic_protect", "hold", "insta_hold", "effic_hold"
        };
        foreach (string id in expected)
            TestAssert.True(GameModeCatalog.TryGet(id) != null, $"mode '{id}' present");

        // Flexible lookups
        TestAssert.True(GameModeCatalog.TryGet("instaCTF") != null, "instaCTF lookup");
        TestAssert.True(GameModeCatalog.TryGet("efficCTF") != null, "efficCTF lookup");
        TestAssert.True(GameModeCatalog.TryGet("instateam") != null, "instateam lookup");
        TestAssert.True(GameModeCatalog.TryGet("efficteam") != null, "efficteam lookup");
        TestAssert.True(GameModeCatalog.TryGet("tactics team") != null, "tactics team lookup");
        TestAssert.True(GameModeCatalog.TryGet("tac team") != null, "tac team lookup");
        TestAssert.True(GameModeCatalog.TryGet("regen capture") != null, "regen capture lookup");
        TestAssert.True(GameModeCatalog.TryGet("instagib") != null, "instagib alias");
        TestAssert.True(GameModeCatalog.TryGet("efficiency") != null, "efficiency alias");
        TestAssert.True(GameModeCatalog.TryGet("nope") == null, "unknown mode is null");
    }

    [Test]
    public void TacticsAndCaptureLoadouts()
    {
        var s = Fresh();
        // SequenceRandom returning 1 (Shotgun) then 4 (Rifle)
        s.Spawn(GameModeCatalog.Get("tactics"), new SequenceRandom(1, 4));

        TestAssert.Equal(100, s.Health, "tactics health");
        TestAssert.Equal(100, s.Armour, "tactics armour");
        TestAssert.Equal(ArmourType.Green, s.ArmourType, "tactics armourtype");
        TestAssert.Equal((int)Gun.Shotgun, s.GunSelect, "tactics gunselect");
        TestAssert.Equal(20, s.Ammo[(int)Gun.Shotgun], "SG baseammo*2");
        TestAssert.Equal(10, s.Ammo[(int)Gun.Rifle], "Rifle baseammo*2");
        TestAssert.Equal(1, s.Ammo[(int)Gun.GrenadeLauncher], "GL +1 for noitems");
        TestAssert.Equal(40, s.Ammo[(int)Gun.Pistol], "Pistol 40");

        // Capture (tactics without NoItems): baseammo is scaled by 1, and no GL bonus
        s = Fresh();
        s.Spawn(GameModeCatalog.Get("capture"), new SequenceRandom(2, 3));
        TestAssert.Equal((int)Gun.Chaingun, s.GunSelect, "capture gunselect");
        TestAssert.Equal(20, s.Ammo[(int)Gun.Chaingun], "CG baseammo*1");
        TestAssert.Equal(5, s.Ammo[(int)Gun.RocketLauncher], "RL baseammo*1");
        TestAssert.Equal(0, s.Ammo[(int)Gun.GrenadeLauncher], "GL 0 (no bonus without noitems)");
    }

    [Test]
    public void RegenCaptureAndCtfFamilyLoadouts()
    {
        var s = Fresh();
        s.Spawn(GameModeCatalog.Get("regen_capture"));
        TestAssert.Equal(25, s.Armour, "regen capture armour");
        TestAssert.Equal(ArmourType.Blue, s.ArmourType, "regen capture blue armour");
        TestAssert.Equal(40, s.Ammo[(int)Gun.Pistol], "regen capture pistol");
        TestAssert.Equal(1, s.Ammo[(int)Gun.GrenadeLauncher], "regen capture gl");

        foreach (string id in new[] { "ctf", "protect", "hold" })
        {
            s = Fresh();
            s.Spawn(GameModeCatalog.Get(id));
            TestAssert.Equal(50, s.Armour, $"{id} armour");
            TestAssert.Equal(ArmourType.Blue, s.ArmourType, $"{id} blue armour");
            TestAssert.Equal(40, s.Ammo[(int)Gun.Pistol], $"{id} pistol");
            TestAssert.Equal(1, s.Ammo[(int)Gun.GrenadeLauncher], $"{id} gl");
        }
    }

    [Test]
    public void TeamTrackingAndHelpers()
    {
        var s = Fresh();
        s.Team = "good";
        TestAssert.Equal(1, PlayerState.TeamFlag("good"), "teamflag good");
        TestAssert.Equal(2, PlayerState.TeamFlag("evil"), "teamflag evil");
        TestAssert.Equal(0, PlayerState.TeamFlag("neutral"), "teamflag neutral");
        TestAssert.Equal("good", PlayerState.FlagTeam(1), "flagteam 1");
        TestAssert.Equal("evil", PlayerState.FlagTeam(2), "flagteam 2");

        var ctfMode = GameModeCatalog.Get("ctf");
        var ffaMode = GameModeCatalog.Get("ffa");
        TestAssert.True(s.IsTeam("good", ctfMode), "is team good in ctf");
        TestAssert.True(!s.IsTeam("evil", ctfMode), "is not evil in ctf");
        TestAssert.True(!s.IsTeam("good", ffaMode), "isTeam is false in non-team mode");
    }

    private sealed class SequenceRandom : Random
    {
        private readonly int[] _seq;
        private int _idx;
        public SequenceRandom(params int[] seq) => _seq = seq;
        public override int Next(int minValue, int maxValue) => _seq[_idx++];
    }

    [Test]
    public void LoadedModesMatchDefaults()
    {
        // Two representations exist (code defaults + authored .tres); keep them from
        // drifting. The flag test below locks the active catalogue to the engine.
        var loaded = GameModeCatalog.Load();
        TestAssert.Equal(GameModeCatalog.Defaults.Length, loaded.Length, "row count");
        for (int i = 0; i < loaded.Length; i++)
        {
            var a = GameModeCatalog.Defaults[i];
            var b = loaded[i];
            TestAssert.Equal(a.Id, b.Id, $"row {i} Id");
            TestAssert.Equal(a.Name, b.Name, $"row {i} Name");
            TestAssert.Equal(a.Flags, b.Flags, $"row {i} Flags");
        }
    }

    [Test]
    public void LoadAddsModDefinedMode()
    {
        // A mod ships an extra .tres; the catalog must pick it up rather than only
        // resolving the built-in ids. (The engine-index representation could not do this.)
        const string dir = "user://gamemodes_test";
        DirAccess.MakeDirRecursiveAbsolute(dir);
        string path = $"{dir}/test_mode.tres";
        using (var file = FileAccess.Open(path, FileAccess.ModeFlags.Write))
        {
            file.StoreString(
                "[gd_resource type=\"Resource\" script_class=\"GameModeInfo\" format=3]\n\n" +
                "[ext_resource type=\"Script\" path=\"res://World/Game/GameModeInfo.cs\" id=\"1_mod\"]\n\n" +
                "[resource]\n" +
                "script = ExtResource(\"1_mod\")\n" +
                "Id = \"test_mode\"\n" +
                "Name = \"test mode\"\n" +
                "Flags = 8\n");
        }

        var modes = GameModeCatalog.Load(dir);
        GameModeInfo? modMode = null;
        foreach (var mode in modes)
            if (mode.Id == "test_mode") modMode = mode;

        TestAssert.True(modMode != null, "mod-defined mode is loaded");
        TestAssert.Equal("test mode", modMode!.Name, "mod mode name");
        TestAssert.True(modMode.Has(ModeFlags.Insta), "mod mode flags");
        TestAssert.Equal(GameModeCatalog.Defaults.Length + 1, modes.Length, "defaults plus the mod mode");

        DirAccess.RemoveAbsolute(path);
        DirAccess.RemoveAbsolute(dir);
    }

    [Test]
    public void ModeFlagsMatchEngineTable()
    {
        TestAssert.Equal(ModeFlags.Lobby, GameModeCatalog.Get("ffa").Flags, "ffa flags");
        TestAssert.Equal(ModeFlags.Edit, GameModeCatalog.Get("coop_edit").Flags, "coop edit flags");
        TestAssert.Equal(ModeFlags.Team, GameModeCatalog.Get("teamplay").Flags, "teamplay flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Insta, GameModeCatalog.Get("insta").Flags, "insta flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Insta | ModeFlags.Team, GameModeCatalog.Get("instateam").Flags, "instateam flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Efficiency, GameModeCatalog.Get("effic").Flags, "effic flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Efficiency | ModeFlags.Team, GameModeCatalog.Get("efficteam").Flags, "efficteam flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Tactics, GameModeCatalog.Get("tactics").Flags, "tactics flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Tactics | ModeFlags.Team, GameModeCatalog.Get("tactics_team").Flags, "tactics_team flags");
        TestAssert.Equal(ModeFlags.NoAmmo | ModeFlags.Tactics | ModeFlags.Capture | ModeFlags.Team, GameModeCatalog.Get("capture").Flags, "capture flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Capture | ModeFlags.Regen | ModeFlags.Team, GameModeCatalog.Get("regen_capture").Flags, "regen_capture flags");
        TestAssert.Equal(ModeFlags.Ctf | ModeFlags.Team, GameModeCatalog.Get("ctf").Flags, "ctf flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Insta | ModeFlags.Ctf | ModeFlags.Team,
            GameModeCatalog.Get("insta_ctf").Flags, "insta ctf flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Efficiency | ModeFlags.Ctf | ModeFlags.Team,
            GameModeCatalog.Get("effic_ctf").Flags, "effic ctf flags");
        TestAssert.Equal(ModeFlags.Ctf | ModeFlags.Protect | ModeFlags.Team, GameModeCatalog.Get("protect").Flags, "protect flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Insta | ModeFlags.Ctf | ModeFlags.Protect | ModeFlags.Team,
            GameModeCatalog.Get("insta_protect").Flags, "insta protect flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Efficiency | ModeFlags.Ctf | ModeFlags.Protect | ModeFlags.Team,
            GameModeCatalog.Get("effic_protect").Flags, "effic protect flags");
        TestAssert.Equal(ModeFlags.Ctf | ModeFlags.Hold | ModeFlags.Team, GameModeCatalog.Get("hold").Flags, "hold flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Insta | ModeFlags.Ctf | ModeFlags.Hold | ModeFlags.Team,
            GameModeCatalog.Get("insta_hold").Flags, "insta hold flags");
        TestAssert.Equal(ModeFlags.NoItems | ModeFlags.Efficiency | ModeFlags.Ctf | ModeFlags.Hold | ModeFlags.Team,
            GameModeCatalog.Get("effic_hold").Flags, "effic hold flags");

        // Bit positions are the engine's; bit 11 is unused.
        TestAssert.Equal(1 << 0, (int)ModeFlags.Team, "M_TEAM");
        TestAssert.Equal(1 << 3, (int)ModeFlags.Insta, "M_INSTA");
        TestAssert.Equal(1 << 4, (int)ModeFlags.Efficiency, "M_EFFICIENCY");
        TestAssert.Equal(1 << 5, (int)ModeFlags.Tactics, "M_TACTICS");
        TestAssert.Equal(1 << 6, (int)ModeFlags.Capture, "M_CAPTURE");
        TestAssert.Equal(1 << 7, (int)ModeFlags.Regen, "M_REGEN");
        TestAssert.Equal(1 << 8, (int)ModeFlags.Ctf, "M_CTF");
        TestAssert.Equal(1 << 9, (int)ModeFlags.Protect, "M_PROTECT");
        TestAssert.Equal(1 << 10, (int)ModeFlags.Hold, "M_HOLD");
        TestAssert.Equal(1 << 12, (int)ModeFlags.Edit, "M_EDIT");
        TestAssert.Equal(1 << 15, (int)ModeFlags.Lobby, "M_LOBBY");
    }

    [Test]
    public void ItemStatsMatchEngineTable()
    {
        TestAssert.Equal(11, ItemStats.All.Length, "itemstats count");
        TestAssert.Equal(10, ItemStats.ForGun(Gun.Shotgun).Add, "SG add");
        TestAssert.Equal(30, ItemStats.ForGun(Gun.Shotgun).Max, "SG max");
        TestAssert.Equal(20, ItemStats.ForGun(Gun.Chaingun).Add, "CG add");
        TestAssert.Equal(60, ItemStats.ForGun(Gun.Chaingun).Max, "CG max");
        TestAssert.Equal(5, ItemStats.ForGun(Gun.RocketLauncher).Add, "RL add");
        TestAssert.Equal(15, ItemStats.ForGun(Gun.RocketLauncher).Max, "RL max");
        TestAssert.Equal(5, ItemStats.ForGun(Gun.Rifle).Add, "Rifle add");
        TestAssert.Equal(15, ItemStats.ForGun(Gun.Rifle).Max, "Rifle max");
        TestAssert.Equal(10, ItemStats.ForGun(Gun.GrenadeLauncher).Add, "GL add");
        TestAssert.Equal(30, ItemStats.ForGun(Gun.GrenadeLauncher).Max, "GL max");
        TestAssert.Equal(30, ItemStats.ForGun(Gun.Pistol).Add, "PI add");
        TestAssert.Equal(120, ItemStats.ForGun(Gun.Pistol).Max, "PI max");
        TestAssert.Equal(20000, ItemStats.Get(18).Add, "quad add");
        TestAssert.Equal(30000, ItemStats.Get(18).Max, "quad max");
    }

    [Test]
    public void SignalsEmitDirectlyFromPlayerState()
    {
        var s = Fresh();
        int healthChangedCount = 0;
        int lastOldHealth = -1, lastNewHealth = -1;
        int armourChangedCount = 0;
        int lastOldArmour = -1, lastNewArmour = -1;
        int weaponChangedCount = 0;
        int lastWeapon = -1;

        s.HealthChanged += (oldH, newH) =>
        {
            healthChangedCount++;
            lastOldHealth = oldH;
            lastNewHealth = newH;
        };
        s.ArmourChanged += (oldA, newA) =>
        {
            armourChangedCount++;
            lastOldArmour = oldA;
            lastNewArmour = newA;
        };
        s.WeaponChanged += gunId =>
        {
            weaponChangedCount++;
            lastWeapon = gunId;
        };

        // 1. Direct property sets emit
        s.Health = 100;
        TestAssert.Equal(1, healthChangedCount, "HealthChanged emitted on set");
        TestAssert.Equal(0, lastOldHealth, "old health 0");
        TestAssert.Equal(100, lastNewHealth, "new health 100");

        s.Armour = 50;
        TestAssert.Equal(1, armourChangedCount, "ArmourChanged emitted on set");
        TestAssert.Equal(0, lastOldArmour, "old armour 0");
        TestAssert.Equal(50, lastNewArmour, "new armour 50");

        s.GunSelect = (int)Gun.Rifle;
        TestAssert.Equal(1, weaponChangedCount, "WeaponChanged emitted on set");
        TestAssert.Equal((int)Gun.Rifle, lastWeapon, "weapon id Rifle");

        // 2. Setting same value does not emit duplicate
        s.Health = 100;
        TestAssert.Equal(1, healthChangedCount, "no duplicate on same Health");

        // 3. DoDamage emits both ArmourChanged and HealthChanged
        s.ArmourType = ArmourType.Blue; // absorbs 25%
        s.DoDamage(40); // 10 absorbed by armour, 30 damage to health
        TestAssert.Equal(2, armourChangedCount, "ArmourChanged emitted on DoDamage");
        TestAssert.Equal(40, s.Armour, "armour reduced to 40");
        TestAssert.Equal(2, healthChangedCount, "HealthChanged emitted on DoDamage");
        TestAssert.Equal(70, s.Health, "health reduced to 70");

        // 4. Spawn emits state transition
        s.Spawn(GameModeCatalog.Get("insta_ctf"));
        TestAssert.Equal(1, s.Health, "health is 1 in insta");
        TestAssert.Equal((int)Gun.Rifle, s.GunSelect, "selected rifle in insta");
    }
}
