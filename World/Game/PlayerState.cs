using System;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// The per-player combat state (port of the engine's <c>fpsstate</c>,
/// fpsgame/game.h:367, r6889). This is the <b>weapon half</b> plus the health and
/// armour that <c>spawnstate</c> touches: <see cref="Ammo"/>, <see cref="GunSelect"/>,
/// <see cref="GunWait"/>, <see cref="Health"/>/<see cref="MaxHealth"/>,
/// <see cref="Armour"/>/<see cref="ArmourType"/> and <see cref="QuadMillis"/>.
///
/// Firing, hit resolution and the rest of the item system are separate stages; this
/// class only owns the numbers. Like the engine, <see cref="SpawnState"/> assumes a
/// fresh <see cref="Respawn"/> already ran — use <see cref="Spawn"/> (the
/// <c>fps.cpp spawnstate()</c> wrapper) to get both.
/// </summary>
public sealed class PlayerState
{
    /// <summary>Engine <c>fpsstate::maxhealth</c> initial value.</summary>
    public const int DefaultMaxHealth = 100;

    public int Health;
    public int MaxHealth = DefaultMaxHealth;
    public int Armour;
    public ArmourType ArmourType = ArmourType.Blue;
    public int QuadMillis;

    /// <summary>Currently selected gun id (engine <c>gunselect</c>, a <see cref="Gun"/> value).</summary>
    public int GunSelect;

    /// <summary>Milliseconds left before the next shot (engine <c>gunwait</c>).</summary>
    public int GunWait;

    /// <summary>Timestamp (ms) of the last attack (engine <c>fpsent::lastaction</c>).</summary>
    public int LastAction;

    /// <summary>Gun id used during the last attack (engine <c>fpsent::lastattackgun</c>).</summary>
    public int LastAttackGun = (int)Gun.Pistol;

    /// <summary>Timestamp (ms) of last damage taken or death (engine <c>fpsent::lastpain</c>).</summary>
    public int LastPain;

    /// <summary>Timestamp (ms) of the last successful hit on an enemy (engine <c>lasthit</c> in fps.cpp).</summary>
    public int LastHit;

    /// <summary>Last entity type picked up or triggered (fpsgame/game.h:549 <c>fpsent::lastpickup</c>).</summary>
    public int LastPickup = -1;

    /// <summary>Timestamp (ms) of the last entity pickup or trigger (fpsgame/game.h:549 <c>fpsent::lastpickupmillis</c>).</summary>
    public int LastPickupMillis;

    /// <summary>Bitmask of flag pickups currently debounced for this player (ctf.h <c>flagpickup</c>).</summary>
    public int FlagPickup;

    /// <summary>Player team name (engine <c>team</c>, e.g. "good" or "evil").</summary>
    public string Team = "good";

    /// <summary>Engine <c>ctfteamflag(s)</c>: "good" -> 1, "evil" -> 2, otherwise 0.</summary>
    public static int TeamFlag(string? team) => team?.ToLowerInvariant() switch
    {
        "good" => 1,
        "evil" => 2,
        _ => 0,
    };

    /// <summary>Engine <c>ctfflagteam(i)</c>: 1 -> "good", 2 -> "evil", otherwise "".</summary>
    public static string FlagTeam(int team) => team switch
    {
        1 => "good",
        2 => "evil",
        _ => "",
    };

    /// <summary>Engine <c>isteam(a, b)</c>: true if mode is team mode and teams match.</summary>
    public bool IsTeam(string? otherTeam, GameModeInfo? mode = null)
    {
        if (mode != null && !mode.Has(ModeFlags.Team)) return false;
        return string.Equals(Team, otherTeam, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True while the attack button is held down (engine <c>fpsent::attacking</c>).</summary>
    public bool Attacking;

    /// <summary>Ammo per gun, indexed by <see cref="Gun"/> (engine <c>ammo[NUMGUNS]</c>).</summary>
    public readonly int[] Ammo = new int[Guns.Count];

    /// <summary>Engine <c>baseammo(gun, k=2, scale=1)</c>: <c>itemstats[gun-GUN_SG].add*k/scale</c>.</summary>
    public void BaseAmmo(int gun, int k = 2, int scale = 1)
    {
        ItemStat stat = ItemStats.ForGun((Gun)gun);
        Ammo[gun] = (stat.Add * k) / scale;
    }

    /// <summary>Engine <c>addammo(gun, k=1, scale=1)</c>: add, clamped to <c>itemstats[].max</c>.</summary>
    public void AddAmmo(int gun, int k = 1, int scale = 1)
    {
        ItemStat stat = ItemStats.ForGun((Gun)gun);
        Ammo[gun] = Math.Min(Ammo[gun] + (stat.Add * k) / scale, stat.Max);
    }

    /// <summary>Engine <c>hasammo(gun, exclude=-1)</c>.</summary>
    public bool HasAmmo(int gun, int exclude = -1)
        => gun >= 0 && gun < Guns.Count && gun != exclude && Ammo[gun] > 0;

    /// <summary>Engine <c>hasmaxammo(type)</c> (fpsgame/game.h:390).</summary>
    public bool HasMaxAmmo(int type)
    {
        if (type < ItemStats.FirstItemType || type >= ItemStats.FirstItemType + ItemStats.Count) return false;
        ItemStat isStat = ItemStats.Get(type);
        if (isStat.Info < 0) return false;
        return Ammo[isStat.Info] >= isStat.Max;
    }

    /// <summary>
    /// Engine <c>canpickup(type)</c> (fpsgame/game.h:395). Returns true if this player
    /// can pick up the specified item type based on current health, armour or ammo.
    /// </summary>
    public bool CanPickup(int type)
    {
        if (type < ItemStats.FirstItemType || type >= ItemStats.FirstItemType + ItemStats.Count) return false;
        ItemStat stat = ItemStats.Get(type);
        switch (type)
        {
            case EntityType.HealthBoost:
                return MaxHealth < stat.Max || Health < MaxHealth;
            case EntityType.Health:
                return Health < MaxHealth;
            case EntityType.GreenArmour:
                // (100h/100g only absorbs 200 damage)
                if (ArmourType == ArmourType.Yellow && Armour >= 100) return false;
                goto case EntityType.YellowArmour;
            case EntityType.YellowArmour:
                return ArmourType == ArmourType.Blue || Armour < stat.Max;
            case EntityType.QuadDamage:
                return QuadMillis < stat.Max;
            default:
                return Ammo[stat.Info] < stat.Max;
        }
    }

    /// <summary>
    /// Engine <c>pickup(type)</c> (fpsgame/game.h:412). Updates player state (health,
    /// armour, quad duration or weapon ammo) from an item pickup.
    /// </summary>
    public void Pickup(int type)
    {
        if (type < ItemStats.FirstItemType || type >= ItemStats.FirstItemType + ItemStats.Count) return;
        ItemStat stat = ItemStats.Get(type);
        switch (type)
        {
            case EntityType.HealthBoost:
                MaxHealth = Math.Min(MaxHealth + stat.Info, stat.Max);
                Health = Math.Min(Health + stat.Add, MaxHealth);
                break;
            case EntityType.Health:
                Health = Math.Min(Health + stat.Add, MaxHealth);
                break;
            case EntityType.GreenArmour:
            case EntityType.YellowArmour:
                Armour = Math.Min(Armour + stat.Add, stat.Max);
                ArmourType = (ArmourType)stat.Info;
                break;
            case EntityType.QuadDamage:
                QuadMillis = Math.Min(QuadMillis + stat.Add, stat.Max);
                break;
            default:
                Ammo[stat.Info] = Math.Min(Ammo[stat.Info] + stat.Add, stat.Max);
                break;
        }
    }

    /// <summary>
    /// Engine <c>checkquad(time, d)</c> (fpsgame/entities.cpp:348). Decrements active
    /// quad damage countdown and returns true if quad damage expired this tick.
    /// </summary>
    public bool CheckQuad(int timeMillis)
    {
        if (QuadMillis > 0)
        {
            QuadMillis -= timeMillis;
            if (QuadMillis <= 0)
            {
                QuadMillis = 0;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Engine <c>fpsstate::dodamage(damage)</c>: armour absorbs <c>(damage*(armourtype+1)*25)/100</c>,
    /// remainder reduces health. Returns the health damage actually dealt.
    /// </summary>
    public int DoDamage(int damage)
    {
        int ad = (damage * ((int)ArmourType + 1) * 25) / 100;
        if (ad > Armour) ad = Armour;
        Armour -= ad;
        damage -= ad;
        Health -= damage;
        return damage;
    }

    /// <summary>
    /// Engine <c>fpsstate::respawn()</c>: reset everything not persistent across
    /// spawns. Note the <c>GUN_FIST</c> ammo of 1 — the engine always keeps the fist
    /// "loaded" so it is selectable when everything else is dry.
    /// </summary>
    public void Respawn()
    {
        MaxHealth = DefaultMaxHealth;
        Health = MaxHealth;
        Armour = 0;
        ArmourType = ArmourType.Blue;
        QuadMillis = 0;
        GunSelect = (int)Gun.Pistol;
        GunWait = 0;
        LastAction = 0;
        LastAttackGun = (int)Gun.Pistol;
        LastPain = 0;
        LastHit = 0;
        LastPickup = -1;
        LastPickupMillis = 0;
        FlagPickup = 0;
        Attacking = false;
        for (int i = 0; i < Ammo.Length; i++) Ammo[i] = 0;
        Ammo[(int)Gun.Fist] = 1;
    }

    /// <summary>
    /// Engine <c>fpsstate::spawnstate(gamemode)</c>: apply the mode's loadout on top
    /// of a fresh <see cref="Respawn"/>. Branches are tested in the engine's order
    /// (insta before regen before tactics before efficiency before ctf before else). Behaviour is keyed on <see cref="GameModeInfo.Flags"/>,
    /// not on a mode identity, so a modded mode that sets the same flags gets the same loadout.
    /// </summary>
    public void SpawnState(GameModeInfo mode, Random? rng = null)
    {
        if (mode.Has(ModeFlags.Insta))
        {
            // Instagib: full rifle, one-shot kills.
            Armour = 0;
            Health = 1;
            GunSelect = (int)Gun.Rifle;
            Ammo[(int)Gun.Rifle] = 100;
        }
        else if (mode.Has(ModeFlags.Regen) && mode.Has(ModeFlags.Capture))
        {
            // Regen capture: blue armour 25, pistol 40, grenade 1.
            ArmourType = ArmourType.Blue;
            Armour = 25;
            GunSelect = (int)Gun.Pistol;
            Ammo[(int)Gun.Pistol] = 40;
            Ammo[(int)Gun.GrenadeLauncher] = 1;
        }
        else if (mode.Has(ModeFlags.Tactics))
        {
            // Tactics: green armour 100, pistol 40, two random guns with baseammo, grenade+1 if noitems.
            ArmourType = ArmourType.Green;
            Armour = 100;
            Ammo[(int)Gun.Pistol] = 40;

            rng ??= Random.Shared;
            int spawngun1 = rng.Next(1, 6);
            GunSelect = spawngun1;
            BaseAmmo(spawngun1, mode.Has(ModeFlags.NoItems) ? 2 : 1);

            int spawngun2;
            do
            {
                spawngun2 = rng.Next(1, 6);
            } while (spawngun1 == spawngun2);
            BaseAmmo(spawngun2, mode.Has(ModeFlags.NoItems) ? 2 : 1);

            if (mode.Has(ModeFlags.NoItems))
            {
                Ammo[(int)Gun.GrenadeLauncher] += 1;
            }
        }
        else if (mode.Has(ModeFlags.Efficiency))
        {
            // Efficiency: armour + a base load for SG..GL, then halve the chaingun.
            ArmourType = ArmourType.Green;
            Armour = 100;
            for (int gun = (int)Gun.Shotgun; gun <= (int)Gun.GrenadeLauncher; gun++) BaseAmmo(gun);
            GunSelect = (int)Gun.Chaingun;
            Ammo[(int)Gun.Chaingun] /= 2;
        }
        else if (mode.Has(ModeFlags.Ctf) || mode.Has(ModeFlags.Collect))
        {
            // CTF / Protect / Hold: blue armour 50, pistol 40, grenade 1.
            ArmourType = ArmourType.Blue;
            Armour = 50;
            GunSelect = (int)Gun.Pistol;
            Ammo[(int)Gun.Pistol] = 40;
            Ammo[(int)Gun.GrenadeLauncher] = 1;
        }
        else
        {
            // Engine else branch: ffa, teamplay, and coop edit.
            ArmourType = ArmourType.Blue;
            Armour = 25;
            GunSelect = (int)Gun.Pistol;
            Ammo[(int)Gun.Pistol] = 40;
            Ammo[(int)Gun.GrenadeLauncher] = 1;
        }
    }

    /// <summary>fps.cpp <c>spawnstate()</c> wrapper: <see cref="Respawn"/> then <see cref="SpawnState"/>.</summary>
    public void Spawn(GameModeInfo mode, Random? rng = null)
    {
        Respawn();
        SpawnState(mode, rng);
    }

    /// <summary>Convenience read of the current gun's ammo.</summary>
    public int SelectedAmmo => HasAmmo(GunSelect) ? Ammo[GunSelect] : 0;
}
