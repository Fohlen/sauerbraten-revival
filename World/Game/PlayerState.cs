using System;
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
        Attacking = false;
        for (int i = 0; i < Ammo.Length; i++) Ammo[i] = 0;
        Ammo[(int)Gun.Fist] = 1;
    }

    /// <summary>
    /// Engine <c>fpsstate::spawnstate(gamemode)</c>: apply the mode's loadout on top
    /// of a fresh <see cref="Respawn"/>. Branches are tested in the engine's order
    /// (insta before ctf, efficiency before ctf), so "insta ctf"/"effic ctf" resolve
    /// to the insta/effic loadouts. Behaviour is keyed on <see cref="GameModeInfo.Flags"/>,
    /// not on a mode identity, so a modded mode that sets the same flags gets the same
    /// loadout. Only the MVP branches are ported; the SP/demo/tactics/capture branches
    /// are out of scope and deliberately absent.
    /// </summary>
    public void SpawnState(GameModeInfo mode)
    {
        if (mode.Has(ModeFlags.Insta))
        {
            // Instagib: full rifle, one-shot kills.
            Armour = 0;
            Health = 1;
            GunSelect = (int)Gun.Rifle;
            Ammo[(int)Gun.Rifle] = 100;
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
        else
        {
            // Engine else branch: ffa (and the modes we route here).
            ArmourType = ArmourType.Blue;
            Armour = 25;
            Ammo[(int)Gun.Pistol] = 40;
            Ammo[(int)Gun.GrenadeLauncher] = 1;
        }
    }

    /// <summary>fps.cpp <c>spawnstate()</c> wrapper: <see cref="Respawn"/> then <see cref="SpawnState"/>.</summary>
    public void Spawn(GameModeInfo mode)
    {
        Respawn();
        SpawnState(mode);
    }

    /// <summary>Convenience read of the current gun's ammo.</summary>
    public int SelectedAmmo => HasAmmo(GunSelect) ? Ammo[GunSelect] : 0;
}
