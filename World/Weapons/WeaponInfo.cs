namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// One row of the engine's <c>guns[NUMGUNS]</c> table (fpsgame/game.h:348),
/// covering both the seven player weapons and the monster/projectile pseudo-guns.
/// Static definition data only — the firing/hitscan code that consumes it
/// (<c>weapon.cpp</c>, <c>server.cpp</c>) is game logic and is not ported yet.
/// </summary>
public sealed class WeaponInfo
{
    /// <summary>Engine <c>GUN_*</c> id (also the index into <see cref="WeaponCatalog.All"/>).</summary>
    public Gun Id;

    /// <summary>Sound played on attack (engine <c>S_*</c>), or null for the barrel (engine <c>-1</c>).</summary>
    public GameSound? Sound;

    /// <summary>Milliseconds between attacks (<c>guns[].attackdelay</c>, copied to <c>gunwait</c>).</summary>
    public int AttackDelay;

    public int Damage;

    /// <summary>Spread in engine units per 1024 units of range (0 = perfectly accurate).</summary>
    public int Spread;

    /// <summary>Projectile speed (0 = hitscan).</summary>
    public int ProjSpeed;

    /// <summary>View kick per shot (<c>kickamount</c>).</summary>
    public int KickAmount;

    /// <summary>Maximum hitscan range (<c>raycubepos</c>); 0 = unbounded/unused.</summary>
    public int Range;

    /// <summary>Rays cast per shot (<c>createrays</c>, capped by <c>MAXRAYS</c> = 20).</summary>
    public int Rays;

    /// <summary>Push applied to the victim (<c>hitpush</c>).</summary>
    public int HitPush;

    /// <summary>Explosion radius (0 = none).</summary>
    public int ExpRad;

    /// <summary>Projectile time-to-live in ms (0 = none).</summary>
    public int Ttl;

    /// <summary>Display name (e.g. "rifle"); used by the HUD/particles.</summary>
    public string Name = "";

    /// <summary>
    /// First-person hudgun submodel directory (e.g. "shotg"), or null for the
    /// monster/projectile pseudo-guns that have no player model.
    /// </summary>
    public string? File;

    /// <summary>Projectile particle type (<c>PART_*</c>); 0 = none.</summary>
    public int Part;

    /// <summary>True for the seven player weapons (<c>GUN_FIST</c>..<c>GUN_PISTOL</c>).</summary>
    public bool PlayerSelectable => Id >= Gun.Fist && Id <= Gun.Pistol;

    /// <summary>True when the weapon is hitscan (no projectile speed).</summary>
    public bool Hitscan => ProjSpeed == 0;
}
