namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// Engine <c>GUN_*</c> ids (fpsgame/game.h). <see cref="Fist"/>..<see cref="Pistol"/>
/// are the seven player-selectable weapons; the rest are the monster/projectile
/// pseudo-guns the same <c>guns[]</c> table carries (never selected by a player).
/// Values are the engine indices, so they double as table lookups.
/// </summary>
public enum Gun
{
    Fist = 0,
    Shotgun,
    Chaingun,
    RocketLauncher,
    Rifle,
    GrenadeLauncher,
    Pistol,
    Fireball,
    Iceball,
    Slimeball,
    Bite,
    Barrel,
}

/// <summary>Engine gun-table constants (fpsgame/game.h).</summary>
public static class Guns
{
    /// <summary>fpsgame/game.h <c>NUMGUNS</c>.</summary>
    public const int Count = 12;

    /// <summary>First player-selectable gun (<c>GUN_FIST</c>).</summary>
    public const int First = (int)Gun.Fist;

    /// <summary>Last player-selectable gun (<c>GUN_PISTOL</c>).</summary>
    public const int Last = (int)Gun.Pistol;

    /// <summary>True for <c>GUN_FIST</c>..<c>GUN_PISTOL</c> (the weapons <c>gunselect</c> accepts).</summary>
    public static bool IsPlayerSelectable(int gun) => gun >= First && gun <= Last;
}
