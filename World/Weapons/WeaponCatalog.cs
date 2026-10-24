using Godot;
using SauerbratenRevival.World.Audio;

namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// The engine's <c>guns[NUMGUNS]</c> table (fpsgame/game.h, r6889), plus the
/// third-person view-weapon names from render.cpp <c>vweps[]</c>. Static definition
/// data; the mode loadouts and firing logic that consume it land in later stages.
///
/// The active catalogue (<see cref="All"/>) is loaded from one <c>.tres</c> per gun
/// under <see cref="ResourceDir"/> — authored content, editable in the Godot
/// inspector — and falls back per-weapon to the built-in <see cref="Defaults"/> when
/// a file is missing or malformed. The two are kept in sync by
/// <c>test/WeaponCatalogTest.cs</c>, which locks both to the engine table.
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

    /// <summary>Directory holding the authored per-gun <c>.tres</c> files (one per <see cref="Defaults"/> row).</summary>
    public const string ResourceDir = "res://resources/weapons";

    /// <summary>
    /// Built-in defaults: the ported <c>guns[]</c> rows, in engine order (index =
    /// <see cref="Gun"/>). Used as the fallback when a <c>.tres</c> is absent.
    /// </summary>
    public static readonly WeaponInfo[] Defaults =
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
        new() { Id = Gun.Fireball,        Sound = GameSound.FLaunch,  AttackDelay = 200,  Damage = 20,  Spread = 0,   ProjSpeed = 200, KickAmount = 1,  Range = 1024, Rays = 1,  HitPush = 80,  ExpRad = 40, Ttl = 0,    Name = "fireball",        File = "",       Part = 1 }, // PART_FIREBALL1
        new() { Id = Gun.Iceball,         Sound = GameSound.Iceball,  AttackDelay = 200,  Damage = 40,  Spread = 0,   ProjSpeed = 120, KickAmount = 1,  Range = 1024, Rays = 1,  HitPush = 80,  ExpRad = 40, Ttl = 0,    Name = "iceball",         File = "",       Part = 2 }, // PART_FIREBALL2
        new() { Id = Gun.Slimeball,       Sound = GameSound.Slimeball,AttackDelay = 200,  Damage = 30,  Spread = 0,   ProjSpeed = 640, KickAmount = 1,  Range = 1024, Rays = 1,  HitPush = 80,  ExpRad = 40, Ttl = 0,    Name = "slimeball",       File = "",       Part = 3 }, // PART_FIREBALL3
        new() { Id = Gun.Bite,            Sound = GameSound.PigR1,    AttackDelay = 250,  Damage = 50,  Spread = 0,   ProjSpeed = 0,   KickAmount = 1,  Range = 12,   Rays = 1,  HitPush = 80,  ExpRad = 0,  Ttl = 0,    Name = "bite",            File = "",       Part = 0 },
        new() { Id = Gun.Barrel,          Sound = GameSound.None,     AttackDelay = 0,    Damage = 120, Spread = 0,   ProjSpeed = 0,   KickAmount = 0,  Range = 0,    Rays = 1,  HitPush = 80,  ExpRad = 40, Ttl = 0,    Name = "barrel",          File = "",       Part = 0 },
    };

    private static WeaponInfo[]? _all;

    /// <summary>
    /// The active catalogue: one <see cref="WeaponInfo"/> per gun, loaded from
    /// <see cref="ResourceDir"/> (falling back to <see cref="Defaults"/>). Cached after
    /// the first access; use <see cref="Override"/> to replace it (mods/tests).
    /// </summary>
    public static WeaponInfo[] All => _all ??= Load();

    /// <summary>The number of guns (engine <c>NUMGUNS</c>).</summary>
    public static int Count => All.Length;

    /// <summary>Look up a gun by id (<see cref="Gun"/> is the index).</summary>
    public static WeaponInfo Get(Gun gun) => All[(int)gun];

    /// <summary>Look up a gun by engine id, or null when out of range.</summary>
    public static WeaponInfo? TryGet(int gun) => gun >= 0 && gun < All.Length ? All[gun] : null;

    /// <summary>Replace the cached catalogue (test/mod seam).</summary>
    public static void Override(WeaponInfo[] weapons) => _all = weapons;

    /// <summary>
    /// Load one <c>&lt;name&gt;.tres</c> per row from <paramref name="dir"/>, falling back
    /// to <see cref="Defaults"/> for any file that is missing or does not deserialize to
    /// a <see cref="WeaponInfo"/>. Pure function of the filesystem, so a mod can point at
    /// its own directory.
    /// </summary>
    public static WeaponInfo[] Load(string dir = ResourceDir)
    {
        var result = new WeaponInfo[Defaults.Length];
        for (int i = 0; i < Defaults.Length; i++)
        {
            string path = $"{dir.TrimEnd('/')}/{Defaults[i].Name}.tres";
            var loaded = ResourceLoader.Exists(path) ? ResourceLoader.Load(path) as WeaponInfo : null;
            result[i] = loaded ?? Defaults[i];
        }
        return result;
    }

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
