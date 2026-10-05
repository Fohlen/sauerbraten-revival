namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Entity type ids, matching shared/ents.h + fpsgame/game.h, plus the
/// <c>entities::entname</c> display names.
/// </summary>
public static class EntityType
{
    // Engine types (shared/ents.h).
    public const byte Empty = 0;
    public const byte Light = 1;
    public const byte MapModel = 2;
    public const byte PlayerStart = 3;
    public const byte EnvMap = 4;
    public const byte Particles = 5;
    public const byte Sound = 6;
    public const byte SpotLight = 7;

    // fpsgame-specific types (fpsgame/game.h), starting at ET_GAMESPECIFIC == 8.
    public const byte Shells = 8;
    public const byte Bullets = 9;
    public const byte Rockets = 10;
    public const byte RifleRounds = 11;
    public const byte Grenades = 12;
    public const byte Cartridges = 13;
    public const byte Health = 14;
    public const byte HealthBoost = 15;
    public const byte GreenArmour = 16;
    public const byte YellowArmour = 17;
    public const byte QuadDamage = 18;
    public const byte Teleport = 19;
    public const byte TeleDest = 20;
    public const byte Monster = 21;
    public const byte Carrot = 22;
    public const byte JumpPad = 23;
    public const byte Base = 24;
    public const byte RespawnPoint = 25;
    public const byte Box = 26;
    public const byte Barrel = 27;
    public const byte Platform = 28;
    public const byte Elevator = 29;
    public const byte Flag = 30;

    /// <summary>fpsgame/entities.cpp <c>entname</c>.</summary>
    public static string Name(byte type) => type switch
    {
        0 => "none?",
        1 => "light",
        2 => "mapmodel",
        3 => "playerstart",
        4 => "envmap",
        5 => "particles",
        6 => "sound",
        7 => "spotlight",
        8 => "shells",
        9 => "bullets",
        10 => "rockets",
        11 => "riflerounds",
        12 => "grenades",
        13 => "cartridges",
        14 => "health",
        15 => "healthboost",
        16 => "greenarmour",
        17 => "yellowarmour",
        18 => "quaddamage",
        19 => "teleport",
        20 => "teledest",
        21 => "monster",
        22 => "carrot",
        23 => "jumppad",
        24 => "base",
        25 => "respawnpoint",
        26 => "box",
        27 => "barrel",
        28 => "platform",
        29 => "elevator",
        30 => "flag",
        _ => "",
    };
}
