namespace SauerbratenRevival.World.Entities;

/// <summary>
/// One row of the engine's <c>monstertypes</c> table (monster.cpp). Static
/// definition data only — the AI behaviour that consumes it
/// (<c>monster.cpp</c>, <c>ai.cpp</c>) is not ported.
/// </summary>
public sealed class MonsterType
{
    /// <summary>monster.cpp <c>monstertypes</c> index (matches entity attr2).</summary>
    public int Index;

    /// <summary>The weapon the monster fires (GUN_* in fpsgame/game.h).</summary>
    public int Gun;

    /// <summary>Base movement speed; the engine sets <c>maxspeed = speed*4</c>.</summary>
    public int Speed;

    public int Health;

    /// <summary>Spawning frequency weight under DMSP (<c>TOTMFREQ</c> = 14).</summary>
    public int Freq;

    /// <summary>Delay before firing.</summary>
    public int Lag;

    /// <summary>Reaction rate.</summary>
    public int Rate;

    public int Pain;

    /// <summary>Hits by fellow monsters before it fights back.</summary>
    public int Loyalty;

    /// <summary>Body scale ×10: radius ×= bscale/10, eyeheight = 8·bscale/10, aboveeye = 7·bscale/10.</summary>
    public int BScale;

    public int Weight;

    /// <summary>Display name (e.g. "an ogro").</summary>
    public string Name = "";

    /// <summary>Model name (e.g. "ogro", "monster/rhino").</summary>
    public string ModelName = "";

    /// <summary>View-weapon model, or null.</summary>
    public string? ViewWeapon;
}

/// <summary>
/// The nine monster types (monster.cpp <c>monstertypes</c>, r6889). The model
/// names are the ones <c>rendermonsters</c> loads; behaviour is not ported.
/// </summary>
public static class MonsterTypes
{
    public static readonly MonsterType[] All =
    {
        new() { Index = 0, Gun = GunFireball, Speed = 15, Health = 100, Freq = 3, Lag = 0,   Rate = 100, Pain = 800, Loyalty = 1, BScale = 10, Weight = 90,  Name = "an ogro",     ModelName = "ogro",           ViewWeapon = "ogro/vwep" },
        new() { Index = 1, Gun = GunCg,       Speed = 18, Health = 70,  Freq = 2, Lag = 70,  Rate = 10,  Pain = 400, Loyalty = 2, BScale = 10, Weight = 50,  Name = "a rhino",     ModelName = "monster/rhino",  ViewWeapon = null },
        new() { Index = 2, Gun = GunSg,       Speed = 13, Health = 120, Freq = 1, Lag = 100, Rate = 300, Pain = 400, Loyalty = 4, BScale = 14, Weight = 115, Name = "ratamahatta", ModelName = "monster/rat",    ViewWeapon = "monster/rat/vwep" },
        new() { Index = 3, Gun = GunRifle,    Speed = 14, Health = 200, Freq = 1, Lag = 80,  Rate = 400, Pain = 300, Loyalty = 4, BScale = 18, Weight = 145, Name = "a slith",     ModelName = "monster/slith",  ViewWeapon = "monster/slith/vwep" },
        new() { Index = 4, Gun = GunRl,       Speed = 12, Health = 500, Freq = 1, Lag = 0,   Rate = 200, Pain = 200, Loyalty = 6, BScale = 24, Weight = 210, Name = "bauul",       ModelName = "monster/bauul",  ViewWeapon = "monster/bauul/vwep" },
        new() { Index = 5, Gun = GunBite,     Speed = 24, Health = 50,  Freq = 3, Lag = 0,   Rate = 100, Pain = 400, Loyalty = 1, BScale = 15, Weight = 75,  Name = "a hellpig",   ModelName = "monster/hellpig", ViewWeapon = null },
        new() { Index = 6, Gun = GunIceball,  Speed = 11, Health = 250, Freq = 1, Lag = 0,   Rate = 10,  Pain = 400, Loyalty = 6, BScale = 18, Weight = 160, Name = "a knight",    ModelName = "monster/knight", ViewWeapon = "monster/knight/vwep" },
        new() { Index = 7, Gun = GunSlimeball,Speed = 15, Health = 100, Freq = 1, Lag = 0,   Rate = 200, Pain = 400, Loyalty = 2, BScale = 10, Weight = 60,  Name = "a goblin",    ModelName = "monster/goblin", ViewWeapon = "monster/goblin/vwep" },
        new() { Index = 8, Gun = GunGl,       Speed = 22, Health = 50,  Freq = 1, Lag = 0,   Rate = 200, Pain = 400, Loyalty = 1, BScale = 10, Weight = 40,  Name = "a spider",    ModelName = "monster/spider", ViewWeapon = null },
    };

    // GUN_* ids (fpsgame/game.h). Kept local so the table reads against the engine.
    private const int GunFist = 0, GunSg = 1, GunCg = 2, GunRl = 3, GunRifle = 4, GunGl = 5,
                      GunPistol = 6, GunFireball = 7, GunIceball = 8, GunSlimeball = 9,
                      GunBite = 10, GunBarrel = 11;

    /// <summary>The number of monster types (<c>NUMMONSTERTYPES</c>).</summary>
    public static int Count => All.Length;

    /// <summary>Look up a type, or null for an out-of-range index.</summary>
    public static MonsterType? Get(int type) => type >= 0 && type < All.Length ? All[type] : null;
}
