namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// The engine's <c>guns[NUMGUNS]</c> table (fpsgame/game.h, r6889), plus the
/// third-person view-weapon names from render.cpp <c>vweps[]</c>. This is static
/// definition data; the mode loadouts and firing logic that consume it land in
/// later stages.
/// </summary>
public static class WeaponCatalog
{
    /// <summary>
    /// render.cpp <c>vweps[]</c>: the third-person view weapon for each player gun
    /// (<c>GUN_FIST</c>..<c>GUN_PISTOL</c>). Attached to the body's <c>tag_weapon</c>
    /// bone by renderer <c>renderplayer</c>.
    /// </summary>
    public static readonly string[] Vweps =
    {
        "vwep/fist",   // GUN_FIST
        "vwep/shotg",  // GUN_SG
        "vwep/chaing", // GUN_CG
        "vwep/rocket", // GUN_RL
        "vwep/rifle",  // GUN_RIFLE
        "vwep/gl",     // GUN_GL
        "vwep/pistol", // GUN_PISTOL
    };

    /// <summary>fpsgame/game.h <c>guns[]</c>, in engine order (index = <see cref="Gun"/>).</summary>
    public static readonly WeaponInfo[] All =
    {
        // gun                sound                  delay  dmg  spread  spd  kick  range  rays  push  exprad  ttl   name                file      part
        new() { Id = Gun.Fist,            Sound = GameSound.Punch1,   AttackDelay = 250,  Damage = 50,  Spread = 0,   ProjSpeed = 0,   KickAmount = 0,  Range = 14,   Rays = 1,  HitPush = 80,  ExpRad = 0,  Ttl = 0,    Name = "fist",            File = "fist",   Part = 0 },
        new() { Id = Gun.Shotgun,         Sound = GameSound.Sg,       AttackDelay = 1400, Damage = 10,  Spread = 400, ProjSpeed = 0,   KickAmount = 20, Range = 1024, Rays = 20, HitPush = 80,  ExpRad = 0,  Ttl = 0,    Name = "shotgun",         File = "shotg",  Part = 0 },
        new() { Id = Gun.Chaingun,        Sound = GameSound.Cg,       AttackDelay = 100,  Damage = 30,  Spread = 100, ProjSpeed = 0,   KickAmount = 7,  Range = 1024, Rays = 1,  HitPush = 80,  ExpRad = 0,  Ttl = 0,    Name = "chaingun",        File = "chaing", Part = 0 },
        new() { Id = Gun.RocketLauncher,  Sound = GameSound.RlFire,   AttackDelay = 800,  Damage = 120, Spread = 0,   ProjSpeed = 320, KickAmount = 10, Range = 1024, Rays = 1,  HitPush = 160, ExpRad = 40, Ttl = 0,    Name = "rocketlauncher",  File = "rocket", Part = 0 },
        new() { Id = Gun.Rifle,           Sound = GameSound.Rifle,    AttackDelay = 1500, Damage = 100, Spread = 0,   ProjSpeed = 0,   KickAmount = 30, Range = 2048, Rays = 1,  HitPush = 80,  ExpRad = 0,  Ttl = 0,    Name = "rifle",           File = "rifle",  Part = 0 },
        new() { Id = Gun.GrenadeLauncher, Sound = GameSound.FLaunch,  AttackDelay = 600,  Damage = 90,  Spread = 0,   ProjSpeed = 200, KickAmount = 10, Range = 1024, Rays = 1,  HitPush = 250, ExpRad = 45, Ttl = 1500, Name = "grenadelauncher", File = "gl",     Part = 0 },
        new() { Id = Gun.Pistol,          Sound = GameSound.Pistol,   AttackDelay = 500,  Damage = 35,  Spread = 50,  ProjSpeed = 0,   KickAmount = 7,  Range = 1024, Rays = 1,  HitPush = 80,  ExpRad = 0,  Ttl = 0,    Name = "pistol",          File = "pistol", Part = 0 },
        // The five below are monster/projectile pseudo-guns; they have no player model.
        new() { Id = Gun.Fireball,        Sound = GameSound.FLaunch,  AttackDelay = 200,  Damage = 20,  Spread = 0,   ProjSpeed = 200, KickAmount = 1,  Range = 1024, Rays = 1,  HitPush = 80,  ExpRad = 40, Ttl = 0,    Name = "fireball",        File = null,     Part = 1 }, // PART_FIREBALL1
        new() { Id = Gun.Iceball,         Sound = GameSound.Iceball,  AttackDelay = 200,  Damage = 40,  Spread = 0,   ProjSpeed = 120, KickAmount = 1,  Range = 1024, Rays = 1,  HitPush = 80,  ExpRad = 40, Ttl = 0,    Name = "iceball",         File = null,     Part = 2 }, // PART_FIREBALL2
        new() { Id = Gun.Slimeball,       Sound = GameSound.Slimeball,AttackDelay = 200,  Damage = 30,  Spread = 0,   ProjSpeed = 640, KickAmount = 1,  Range = 1024, Rays = 1,  HitPush = 80,  ExpRad = 40, Ttl = 0,    Name = "slimeball",       File = null,     Part = 3 }, // PART_FIREBALL3
        new() { Id = Gun.Bite,            Sound = GameSound.PigR1,    AttackDelay = 250,  Damage = 50,  Spread = 0,   ProjSpeed = 0,   KickAmount = 1,  Range = 12,   Rays = 1,  HitPush = 80,  ExpRad = 0,  Ttl = 0,    Name = "bite",            File = null,     Part = 0 },
        new() { Id = Gun.Barrel,          Sound = null,               AttackDelay = 0,    Damage = 120, Spread = 0,   ProjSpeed = 0,   KickAmount = 0,  Range = 0,    Rays = 1,  HitPush = 80,  ExpRad = 40, Ttl = 0,    Name = "barrel",          File = null,     Part = 0 },
    };

    /// <summary>The number of guns (engine <c>NUMGUNS</c>).</summary>
    public static int Count => All.Length;

    /// <summary>Look up a gun by id (<see cref="Gun"/> is the index).</summary>
    public static WeaponInfo Get(Gun gun) => All[(int)gun];

    /// <summary>Look up a gun by engine id, or null when out of range.</summary>
    public static WeaponInfo? TryGet(int gun) => gun >= 0 && gun < All.Length ? All[gun] : null;

    /// <summary>
    /// Third-person view-weapon model for a player gun (render.cpp <c>vweps[]</c>),
    /// or null for the pseudo-guns that have no player model.
    /// </summary>
    public static string? Vwep(Gun gun)
    {
        int g = (int)gun;
        return g >= 0 && g < Vweps.Length ? Vweps[g] : null;
    }
}
