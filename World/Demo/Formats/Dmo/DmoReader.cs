using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using SauerbratenRevival.Format;

namespace SauerbratenRevival.World.Demo.Formats.Dmo;

/// <summary>
/// Parsed representation of a Sauerbraten (.dmo) demo file.
/// Contains all metadata, players, frags, positions, and events.
/// </summary>
public sealed class DmoFile
{
    public const string MagicHeader = "SAUERBRATEN_DEMO";
    public const int SupportedDemoVersion = 1;
    public const int SupportedProtocolVersion = 260;

    public string Magic { get; set; } = MagicHeader;
    public int Version { get; set; } = SupportedDemoVersion;
    public int Protocol { get; set; } = SupportedProtocolVersion;

    public string MapName { get; set; } = "";
    public int GameMode { get; set; } = -1;
    public string GameModeName { get; set; } = "unknown";
    public int DurationMillis { get; set; }
    public int TotalRecords { get; set; }

    public Dictionary<int, DmoPlayer> Players { get; } = new();
    public List<DmoPosition> Positions { get; } = new();
    public List<DmoShot> Shots { get; } = new();
    public List<DmoDamage> DamageEvents { get; } = new();
    public List<DmoDeath> Deaths { get; } = new();
    public List<DmoSpawn> Spawns { get; } = new();
    public List<DmoTeleport> Teleports { get; } = new();
    public List<DmoJumpPad> JumpPads { get; } = new();

    public DmoPlayer EnsurePlayer(int clientNum)
    {
        if (!Players.TryGetValue(clientNum, out var player))
        {
            player = new DmoPlayer { ClientNum = clientNum };
            Players[clientNum] = player;
        }
        return player;
    }
}

public sealed class DmoPlayer
{
    public int ClientNum { get; set; }
    public string Name { get; set; } = "unnamed";
    public string Team { get; set; } = "none";
    public int Playermodel { get; set; }
    public int Frags { get; set; }
    public int Deaths { get; set; }
    public int Shots { get; set; }
    public int PositionCount { get; set; }
    public int LifeSequence { get; set; } = -1;
    public int State { get; set; } = 0; // 0 = CS_ALIVE, 1 = CS_DEAD
    public int CurrentGun { get; set; } = 4; // GunRifle default

    public override string ToString() =>
        $"Player[CN={ClientNum}, Name='{Name}', Team='{Team}', Frags={Frags}, Deaths={Deaths}, Shots={Shots}, Pos={PositionCount}]";
}

public sealed class DmoPosition
{
    public int Millis { get; set; }
    public int ClientNum { get; set; }
    /// <summary>Eye position (Z includes +EyeHeight = 14.0f, matching Entity.O in engine).</summary>
    public Vec3f Pos { get; set; } = new();
    /// <summary>Feet position from raw N_POS packet.</summary>
    public Vec3f FeetPos { get; set; } = new();
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }
    public Vec3f Vel { get; set; } = new();
    public Vec3f Falling { get; set; } = new();
    public int Move { get; set; }
    public int Strafe { get; set; }
    public byte PhysState { get; set; }
    public int LifeSequence { get; set; }
    public int SelectedGun { get; set; } = 4;

    public override string ToString() =>
        $"Pos[T={Millis}ms, CN={ClientNum}, Eye=({Pos.X:F1},{Pos.Y:F1},{Pos.Z:F1}), Yaw={Yaw:F1}, Pitch={Pitch:F1}, Move={Move}, Strafe={Strafe}]";
}

public sealed class DmoShot
{
    public int Millis { get; set; }
    public int ClientNum { get; set; }
    public int Gun { get; set; }
    public int ShotId { get; set; }
    public Vec3f From { get; set; } = new();
    public Vec3f To { get; set; } = new();

    public override string ToString() =>
        $"Shot[T={Millis}ms, CN={ClientNum}, Gun={Gun}, ID={ShotId}, From=({From.X:F1},{From.Y:F1},{From.Z:F1}) -> To=({To.X:F1},{To.Y:F1},{To.Z:F1})]";
}

public sealed class DmoDamage
{
    public int Millis { get; set; }
    public int TargetCn { get; set; }
    public int ActorCn { get; set; }
    public int Damage { get; set; }
    public int Armour { get; set; }
    public int Health { get; set; }
}

public sealed class DmoDeath
{
    public int Millis { get; set; }
    public int VictimCn { get; set; }
    public int ActorCn { get; set; }
    public int Frags { get; set; }
    public int TeamFrags { get; set; }

    public override string ToString() =>
        $"Death[T={Millis}ms, Victim={VictimCn}, Killer={ActorCn}, Frags={Frags}]";
}

public sealed class DmoSpawn
{
    public int Millis { get; set; }
    public int ClientNum { get; set; }
    public int LifeSequence { get; set; }
    public int SelectedGun { get; set; }
    public int Health { get; set; }
    public int Armour { get; set; }
    public int[]? Ammo { get; set; }
}

public sealed class DmoTeleport
{
    public int Millis { get; set; }
    public int ClientNum { get; set; }
    public int TeleportId { get; set; }
    public int DestId { get; set; }
}

public sealed class DmoJumpPad
{
    public int Millis { get; set; }
    public int ClientNum { get; set; }
    public int JumpPadId { get; set; }
}

/// <summary>
/// Parser for Sauerbraten .dmo files, ported from src/fpsgame/client.cpp and server.cpp (r6889).
/// Also translates parsed demos into revival <see cref="DemoPackage"/>s.
/// </summary>
public static class DmoReader
{
    public const float Dmf = 16.0f;
    public const float DvelF = 1.0f;
    public const float Rad = (float)(Math.PI / 180.0);
    /// <summary>Player eye height above feet in Sauerbraten and SauerbratenRevival.</summary>
    public const float EyeHeight = 14.0f;

    public const int GunFist = 0;
    public const int GunShotgun = 1;
    public const int GunChaingun = 2;
    public const int GunRocketLauncher = 3;
    public const int GunRifle = 4;
    public const int GunGrenadeLauncher = 5;
    public const int GunPistol = 6;

    private static readonly string[] GameModeNames =
    {
        "SP", "DMSP", "demo", "ffa", "coop edit", "teamplay", "instagib",
        "insta team", "efficiency", "effic team", "tactics", "tac team",
        "capture", "regen capture", "ctf", "insta ctf", "protect",
        "insta protect", "hold", "insta hold", "effic ctf", "effic protect",
        "effic hold", "collect", "insta collect", "effic collect"
    };

    public static string GetGameModeName(int mode)
    {
        int idx = mode + 3;
        if (idx >= 0 && idx < GameModeNames.Length)
        {
            return GameModeNames[idx];
        }
        return "unknown";
    }

    /// <summary>
    /// Reads and parses a .dmo file from disk.
    /// </summary>
    public static DmoFile ReadFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    /// <summary>
    /// Reads and parses a .dmo stream (must be gzip-compressed).
    /// </summary>
    public static DmoFile Read(Stream gzipStream)
    {
        using var gz = new GZipStream(gzipStream, CompressionMode.Decompress, leaveOpen: true);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        return ParseUncompressed(bytes);
    }

    /// <summary>
    /// Parses decompressed demo bytes containing demoheader and records.
    /// </summary>
    public static DmoFile ParseUncompressed(byte[] bytes)
    {
        if (bytes.Length < 24)
        {
            throw new InvalidDataException("Demo data too short for header (minimum 24 bytes)");
        }

        var dmo = new DmoFile();
        int offset = 0;

        // Header: char magic[16], int version, int protocol
        string magic = Encoding.ASCII.GetString(bytes, 0, 16).TrimEnd('\0', ' ');
        offset += 16;
        int version = BitConverter.ToInt32(bytes, offset); offset += 4;
        int protocol = BitConverter.ToInt32(bytes, offset); offset += 4;

        dmo.Magic = magic;
        dmo.Version = version;
        dmo.Protocol = protocol;

        if (!magic.StartsWith("SAUERBRATEN_DEMO", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid demo magic header: '{magic}'");
        }

        int lastMillis = 0;
        int totalRecords = 0;

        while (offset + 12 <= bytes.Length)
        {
            int millis = BitConverter.ToInt32(bytes, offset); offset += 4;
            int chan = BitConverter.ToInt32(bytes, offset); offset += 4;
            int len = BitConverter.ToInt32(bytes, offset); offset += 4;

            if (offset + len > bytes.Length)
            {
                break;
            }

            lastMillis = millis;
            totalRecords++;

            var p = new DmoBuffer(bytes, offset, len);
            offset += len;

            if (chan == 0)
            {
                ParsePositions(p, millis, dmo);
            }
            else if (chan == 1)
            {
                ParseMessages(p, millis, -1, dmo);
            }
        }

        dmo.DurationMillis = lastMillis;
        dmo.TotalRecords = totalRecords;
        return dmo;
    }

    private static void ParsePositions(DmoBuffer p, int millis, DmoFile dmo)
    {
        while (p.Remaining > 0)
        {
            int type = p.GetInt();
            if (type == 114) continue; // N_DEMOPACKET
            if (type == 4) // N_POS
            {
                int cn = p.GetUInt();
                byte physstate = (byte)p.GetByte();
                int flags = p.GetUInt();

                float ox = 0, oy = 0, oz = 0;
                for (int k = 0; k < 3; ++k)
                {
                    int n = p.GetByte() | (p.GetByte() << 8);
                    if ((flags & (1 << k)) != 0)
                    {
                        n |= p.GetByte() << 16;
                        if ((n & 0x800000) != 0) n |= unchecked((int)(~0U << 24));
                    }
                    if (k == 0) ox = n / Dmf;
                    else if (k == 1) oy = n / Dmf;
                    else oz = n / Dmf;
                }

                int dir = p.GetByte() | (p.GetByte() << 8);
                float yaw = dir % 360;
                float pitch = Math.Clamp(dir / 360, 0, 180) - 90;
                float roll = Math.Clamp((int)p.GetByte(), 0, 180) - 90;

                int mag = p.GetByte();
                if ((flags & (1 << 3)) != 0) mag |= p.GetByte() << 8;
                dir = p.GetByte() | (p.GetByte() << 8);
                var vel = VecFromYawPitch(dir % 360, Math.Clamp(dir / 360, 0, 180) - 90, 1, 0);
                vel.X *= mag / DvelF; vel.Y *= mag / DvelF; vel.Z *= mag / DvelF;

                var fall = new Vec3f { X = 0, Y = 0, Z = 0 };
                if ((flags & (1 << 4)) != 0)
                {
                    mag = p.GetByte();
                    if ((flags & (1 << 5)) != 0) mag |= p.GetByte() << 8;
                    if ((flags & (1 << 6)) != 0)
                    {
                        dir = p.GetByte() | (p.GetByte() << 8);
                        fall = VecFromYawPitch(dir % 360, Math.Clamp(dir / 360, 0, 180) - 90, 1, 0);
                    }
                    else
                    {
                        fall = new Vec3f { X = 0, Y = 0, Z = -1 };
                    }
                    fall.X *= mag / DvelF; fall.Y *= mag / DvelF; fall.Z *= mag / DvelF;
                }

                int move = ((physstate >> 4) & 2) != 0 ? -1 : ((physstate >> 4) & 1);
                int strafe = ((physstate >> 6) & 2) != 0 ? -1 : ((physstate >> 6) & 1);

                var player = dmo.EnsurePlayer(cn);
                player.PositionCount++;

                // Store both feet position (from raw packet) and eye position (Z + EyeHeight)
                var feet = new Vec3f { X = ox, Y = oy, Z = oz };
                var eye = new Vec3f { X = ox, Y = oy, Z = oz + EyeHeight };

                dmo.Positions.Add(new DmoPosition
                {
                    Millis = millis,
                    ClientNum = cn,
                    FeetPos = feet,
                    Pos = eye,
                    Yaw = yaw,
                    Pitch = pitch,
                    Roll = roll,
                    Vel = vel,
                    Falling = fall,
                    Move = move,
                    Strafe = strafe,
                    PhysState = physstate,
                    LifeSequence = player.LifeSequence,
                    SelectedGun = player.CurrentGun
                });
            }
            else if (type == 28) // N_TELEPORT
            {
                int tpCn = p.GetInt();
                int tp = p.GetInt();
                int td = p.GetInt();
                dmo.Teleports.Add(new DmoTeleport { Millis = millis, ClientNum = tpCn, TeleportId = tp, DestId = td });
            }
            else if (type == 29) // N_JUMPPAD
            {
                int jpCn = p.GetInt();
                int jp = p.GetInt();
                dmo.JumpPads.Add(new DmoJumpPad { Millis = millis, ClientNum = jpCn, JumpPadId = jp });
            }
            else
            {
                break;
            }
        }
    }

    private static void ParseMessages(DmoBuffer p, int millis, int cnContext, DmoFile dmo)
    {
        while (p.Remaining > 0)
        {
            int type = p.GetInt();
            switch (type)
            {
                case 114: break; // N_DEMOPACKET
                case 1: // N_SERVINFO
                    p.GetInt(); p.GetInt(); p.GetInt(); p.GetInt();
                    p.GetString(); p.GetString();
                    break;
                case 2: break; // N_WELCOME
                case 3: // N_INITCLIENT
                    {
                        int cn = p.GetInt();
                        string name = p.GetString();
                        string team = p.GetString();
                        int model = p.GetInt();
                        var player = dmo.EnsurePlayer(cn);
                        player.Name = name;
                        player.Team = team;
                        player.Playermodel = model;
                        break;
                    }
                case 4: // N_POS (can be in chan 1)
                    ParsePositions(p, millis, dmo);
                    return;
                case 5: // N_TEXT
                    p.GetString();
                    break;
                case 6: // N_SOUND
                    p.GetInt();
                    break;
                case 7: // N_CDIS
                    p.GetInt();
                    break;
                case 11: // N_DIED
                    {
                        int vcn = p.GetInt();
                        int acn = p.GetInt();
                        int frags = p.GetInt();
                        int tfrags = p.GetInt();
                        var killer = dmo.EnsurePlayer(acn);
                        killer.Frags = frags;
                        var victim = dmo.EnsurePlayer(vcn);
                        victim.Deaths++;
                        victim.State = 1; // CS_DEAD
                        dmo.Deaths.Add(new DmoDeath
                        {
                            Millis = millis,
                            VictimCn = vcn,
                            ActorCn = acn,
                            Frags = frags,
                            TeamFrags = tfrags
                        });
                        break;
                    }
                case 12: // N_DAMAGE
                    {
                        int tcn = p.GetInt();
                        int acn = p.GetInt();
                        int dmg = p.GetInt();
                        int arm = p.GetInt();
                        int hp = p.GetInt();
                        dmo.DamageEvents.Add(new DmoDamage
                        {
                            Millis = millis,
                            TargetCn = tcn,
                            ActorCn = acn,
                            Damage = dmg,
                            Armour = arm,
                            Health = hp
                        });
                        break;
                    }
                case 13: // N_HITPUSH
                    p.GetInt(); p.GetInt(); p.GetInt();
                    p.GetInt(); p.GetInt(); p.GetInt();
                    break;
                case 14: // N_SHOTFX
                    {
                        int scn = p.GetInt();
                        int gun = p.GetInt();
                        int sid = p.GetInt();
                        float fx = p.GetInt() / Dmf;
                        float fy = p.GetInt() / Dmf;
                        float fz = p.GetInt() / Dmf;
                        float tx = p.GetInt() / Dmf;
                        float ty = p.GetInt() / Dmf;
                        float tz = p.GetInt() / Dmf;
                        var shooter = dmo.EnsurePlayer(scn);
                        shooter.Shots++;
                        shooter.CurrentGun = gun;
                        dmo.Shots.Add(new DmoShot
                        {
                            Millis = millis,
                            ClientNum = scn,
                            Gun = gun,
                            ShotId = sid,
                            From = new Vec3f { X = fx, Y = fy, Z = fz },
                            To = new Vec3f { X = tx, Y = ty, Z = tz }
                        });
                        break;
                    }
                case 15: // N_EXPLODEFX
                    p.GetInt(); p.GetInt(); p.GetInt();
                    break;
                case 16: // N_ITEMSPAWN
                    p.GetInt();
                    break;
                case 17: // N_SPAWNSTATE
                case 18: // N_SPAWN
                    {
                        int scn = (type == 17) ? p.GetInt() : cnContext;
                        int lifeseq = p.GetInt();
                        int hp = p.GetInt();
                        int maxhp = p.GetInt();
                        int arm = p.GetInt();
                        int armtype = p.GetInt();
                        int gun = p.GetInt();
                        int[] ammo = new int[7];
                        ammo[0] = 1; // Fist
                        for (int k = 1; k <= 6; ++k) ammo[k] = p.GetInt(); // SG, CG, RL, Rifle, GL, Pistol
                        var sp = dmo.EnsurePlayer(scn);
                        sp.LifeSequence = lifeseq;
                        sp.State = 0; // CS_ALIVE
                        sp.CurrentGun = gun;
                        dmo.Spawns.Add(new DmoSpawn
                        {
                            Millis = millis,
                            ClientNum = scn,
                            LifeSequence = lifeseq,
                            SelectedGun = gun,
                            Health = hp,
                            Armour = arm,
                            Ammo = ammo
                        });
                        break;
                    }
                case 19: p.GetInt(); break; // N_FORCEDEATH
                case 20: // N_GUNSELECT
                    {
                        int gun = p.GetInt();
                        if (cnContext >= 0) dmo.EnsurePlayer(cnContext).CurrentGun = gun;
                        break;
                    }
                case 21: break; // N_TAUNT
                case 22: // N_MAPCHANGE
                    {
                        dmo.MapName = p.GetString();
                        dmo.GameMode = p.GetInt();
                        dmo.GameModeName = GetGameModeName(dmo.GameMode);
                        p.GetInt(); // hasitems
                        break;
                    }
                case 23: p.GetString(); p.GetInt(); break; // N_MAPVOTE
                case 24: // N_TEAMINFO
                    for (; ; )
                    {
                        string team = p.GetString();
                        if (p.Remaining == 0 || string.IsNullOrEmpty(team)) break;
                        p.GetInt(); // frags
                    }
                    break;
                case 28: // N_TELEPORT
                    {
                        int tpCn = p.GetInt(); int tp = p.GetInt(); int td = p.GetInt();
                        dmo.Teleports.Add(new DmoTeleport { Millis = millis, ClientNum = tpCn, TeleportId = tp, DestId = td });
                        break;
                    }
                case 29: // N_JUMPPAD
                    {
                        int jpCn = p.GetInt(); int jp = p.GetInt();
                        dmo.JumpPads.Add(new DmoJumpPad { Millis = millis, ClientNum = jpCn, JumpPadId = jp });
                        break;
                    }
                case 30: p.GetInt(); break; // N_PING
                case 31: p.GetInt(); break; // N_PONG
                case 32: p.GetInt(); break; // N_CLIENTPING
                case 33: p.GetInt(); break; // N_TIMEUP
                case 35: p.GetString(); break; // N_SERVMSG
                case 36: // N_ITEMLIST
                    while (p.GetInt() >= 0) p.GetInt();
                    break;
                case 37: // N_RESUME
                    while (p.Remaining > 0)
                    {
                        int cn = p.GetInt();
                        if (cn < 0) break;
                        for (int k = 0; k < 17; ++k) p.GetInt();
                    }
                    break;
                case 38: p.GetInt(); p.GetInt(); break; // N_ITEMACC
                case 48: p.GetInt(); p.GetInt(); break; // N_ITEMPICKUP
                case 58: p.GetInt(); break; // N_MASTERMODE
                case 59: p.GetInt(); break; // N_EDITMODE
                case 61: // N_CURRENTMASTER
                    p.GetInt();
                    while (p.GetInt() >= 0) p.GetInt();
                    break;
                case 62: p.GetInt(); p.GetInt(); break; // N_SPECTATOR
                case 64: p.GetInt(); p.GetString(); p.GetInt(); break; // N_SETTEAM
                case 70: p.GetInt(); break; // N_ANNOUNCE
                case 72: // N_SENDDEMOLIST
                    {
                        int count = p.GetInt();
                        for (int i = 0; i < count; ++i) p.GetString();
                        break;
                    }
                case 75: p.GetInt(); p.GetInt(); break; // N_DEMOPLAYBACK
                case 77: break; // N_STOPDEMO
                case 79: p.GetInt(); p.GetInt(); p.GetInt(); break; // N_TAKEFLAG
                case 80: p.GetInt(); p.GetInt(); p.GetInt(); break; // N_RETURNFLAG
                case 81: p.GetInt(); p.GetInt(); p.GetInt(); p.GetInt(); p.GetInt(); break; // N_RESETFLAG
                case 82: p.GetInt(); p.GetInt(); break; // N_INVISFLAG
                case 84: p.GetInt(); p.GetInt(); p.GetInt(); p.GetInt(); p.GetInt(); p.GetInt(); break; // N_DROPFLAG
                case 85: for (int k = 0; k < 9; ++k) p.GetInt(); break; // N_SCOREFLAG
                case 86: // N_INITFLAGS
                    {
                        int flags = p.GetInt();
                        for (int k = 0; k < flags; ++k)
                        {
                            p.GetInt(); p.GetInt(); p.GetInt(); int dropped = p.GetInt();
                            if (dropped != 0) { p.GetInt(); p.GetInt(); p.GetInt(); }
                        }
                        break;
                    }
                case 87: p.GetString(); break; // N_SAYTEAM
                case 88: // N_CLIENT
                    {
                        int cn = p.GetInt();
                        int sublen = p.GetUInt();
                        var sub = p.SubBuffer(sublen);
                        ParseMessages(sub, millis, cn, dmo);
                        break;
                    }
                case 94: p.GetInt(); p.GetInt(); break; // N_PAUSEGAME
                case 95: p.GetInt(); p.GetInt(); break; // N_GAMESPEED
                case 104: p.GetString(); break; // N_SWITCHNAME
                case 105: p.GetInt(); break; // N_SWITCHMODEL
                case 106: p.GetString(); break; // N_SWITCHTEAM
                case 113: p.GetString(); break; // N_SERVCMD
                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Translates a parsed <see cref="DmoFile"/> into a revival <see cref="DemoPackage"/>.
    /// Maps timestamps to 5 ms fixed simulation ticks, resolves authentic spawn locations,
    /// and ensures players are placed accurately in the map geometry.
    /// </summary>
    public static DemoPackage ToDemoPackage(DmoFile dmo, bool remapActivePlayers = true, int? primaryCn = null)
    {
        var commands = new List<Command>();

        // Group positions by player
        var posByPlayer = new Dictionary<int, List<DmoPosition>>();
        foreach (var pos in dmo.Positions)
        {
            if (!posByPlayer.TryGetValue(pos.ClientNum, out var list))
            {
                list = new List<DmoPosition>();
                posByPlayer[pos.ClientNum] = list;
            }
            list.Add(pos);
        }

        // Identify active players
        var activeCns = new List<int>();
        if (primaryCn.HasValue && posByPlayer.ContainsKey(primaryCn.Value))
        {
            activeCns.Add(primaryCn.Value);
        }
        foreach (var (cn, list) in posByPlayer)
        {
            if (list.Count > 0 && !activeCns.Contains(cn))
            {
                activeCns.Add(cn);
            }
        }

        // Mapping from original clientnum to remapped peerId
        var cnToPeer = new Dictionary<int, int>();
        if (remapActivePlayers)
        {
            for (int i = 0; i < activeCns.Count; i++)
            {
                cnToPeer[activeCns[i]] = i; // Primary player -> 0, secondary -> 1, etc.
            }
        }

        int MapCn(int cn)
        {
            if (cnToPeer.TryGetValue(cn, out int peer)) return peer;
            return cn;
        }

        // Helper to resolve player position at or immediately following a given timestamp
        DmoPosition? ResolvePlayerPosAtOrAfter(int cn, int millis)
        {
            if (!posByPlayer.TryGetValue(cn, out var list) || list.Count == 0) return null;
            // Linear scan (or binary search)
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Millis >= millis) return list[i];
            }
            return list[^1];
        }

        // 1. Convert Spawns to PlayerSpawn commands with authentic coordinates
        var firstSpawnSeen = new HashSet<int>();
        foreach (var s in dmo.Spawns)
        {
            int mappedCn = MapCn(s.ClientNum);
            var spawnPos = ResolvePlayerPosAtOrAfter(s.ClientNum, s.Millis);

            Vec3f pos = spawnPos != null ? spawnPos.Pos : new Vec3f { X = 0, Y = 0, Z = EyeHeight };
            float yaw = spawnPos != null ? spawnPos.Yaw : 0f;

            bool isInitialSpawn = firstSpawnSeen.Add(s.ClientNum);
            uint spawnTimestamp = (isInitialSpawn && s.Millis < 500) ? 0u : (uint)s.Millis;

            commands.Add(new Command
            {
                Op = WorldOp.PlayerSpawn,
                PeerId = mappedCn,
                Timestamp = spawnTimestamp,
                Sequence = spawnTimestamp,
                Payload = new(new PlayerSpawnCmd
                {
                    Pos = pos,
                    Yaw = yaw,
                    LifeSequence = s.LifeSequence,
                    SelectedGun = s.SelectedGun >= 0 ? s.SelectedGun : GunRifle,
                    Health = s.Health > 0 ? s.Health : 100,
                    Armour = s.Armour,
                    Ammo = s.Ammo != null ? new List<int>(s.Ammo) : null
                })
            });
        }

        // 2. Convert Deaths to PlayerDeath commands
        foreach (var d in dmo.Deaths)
        {
            int mappedVic = MapCn(d.VictimCn);
            int mappedAct = MapCn(d.ActorCn);
            commands.Add(new Command
            {
                Op = WorldOp.PlayerDeath,
                PeerId = mappedVic,
                Timestamp = (uint)d.Millis,
                Sequence = (ulong)d.Millis,
                Payload = new(new PlayerDeathCmd
                {
                    VictimPeerId = mappedVic,
                    KillerPeerId = mappedAct,
                    Gun = GunRifle,
                    IsSuicide = (mappedVic == mappedAct)
                })
            });
        }

        // 3. Convert Teleports to PlayerTeleport commands
        foreach (var tp in dmo.Teleports)
        {
            int mappedCn = MapCn(tp.ClientNum);
            var destPos = ResolvePlayerPosAtOrAfter(tp.ClientNum, tp.Millis);
            commands.Add(new Command
            {
                Op = WorldOp.PlayerTeleport,
                PeerId = mappedCn,
                Timestamp = (uint)tp.Millis,
                Sequence = (ulong)tp.Millis,
                Payload = new(new PlayerTeleportCmd
                {
                    TeleportId = tp.TeleportId,
                    DestPos = destPos?.Pos ?? new Vec3f(),
                    DestYaw = destPos?.Yaw ?? 0f
                })
            });
        }

        // 4. Convert JumpPads to PlayerJumpPad commands
        foreach (var jp in dmo.JumpPads)
        {
            int mappedCn = MapCn(jp.ClientNum);
            commands.Add(new Command
            {
                Op = WorldOp.PlayerJumpPad,
                PeerId = mappedCn,
                Timestamp = (uint)jp.Millis,
                Sequence = (ulong)jp.Millis,
                Payload = new(new PlayerJumpPadCmd
                {
                    PadId = jp.JumpPadId,
                    Impulse = new Vec3f()
                })
            });
        }

        // Group shots by client and tick (5 ms)
        var shotsByClientTick = new HashSet<(int cn, int tick)>();
        foreach (var shot in dmo.Shots)
        {
            int tick = shot.Millis / 5;
            shotsByClientTick.Add((shot.ClientNum, tick));
        }

        // 5. For each active player, generate 5ms updates between recorded positions
        foreach (var (cn, posList) in posByPlayer)
        {
            if (posList.Count == 0) continue;
            int mappedCn = MapCn(cn);

            var firstPos = posList[0];
            int firstTick = firstPos.Millis / 5;

            // Populate stationary updates from tick 0 to firstTick
            for (int t = 0; t < firstTick; ++t)
            {
                bool attacking = shotsByClientTick.Contains((cn, t));
                var initUpdate = new PlayerUpdateCmd
                {
                    Tick = t,
                    Move = 0,
                    Strafe = 0,
                    Jumping = false,
                    Yaw = firstPos.Yaw,
                    Pitch = firstPos.Pitch,
                    Attacking = attacking,
                    SelectedGun = firstPos.SelectedGun,
                    Pos = (t == 0) ? firstPos.Pos : null,
                    Vel = (t == 0) ? new Vec3f { X = 0, Y = 0, Z = 0 } : null,
                    PhysState = firstPos.PhysState,
                    LifeSequence = firstPos.LifeSequence
                };

                commands.Add(new Command
                {
                    Op = WorldOp.PlayerUpdate,
                    PeerId = mappedCn,
                    Timestamp = (uint)(t * 5),
                    Sequence = (ulong)(t * 5),
                    Payload = new(initUpdate)
                });
            }

            int lastEmittedTick = firstTick - 1;

            for (int i = 0; i < posList.Count; ++i)
            {
                var cur = posList[i];
                var next = (i + 1 < posList.Count) ? posList[i + 1] : null;

                int startTick = Math.Max(cur.Millis / 5, lastEmittedTick + 1);

                // Gap handling: If there is a large gap between positions (e.g. > 250ms),
                // the player was dead or lagged. DO NOT interpolate across map geometry!
                int endTick;
                bool largeGap = (next != null && (next.Millis - cur.Millis > 250));

                if (next == null)
                {
                    endTick = startTick + 1;
                }
                else if (largeGap)
                {
                    // Hold current position for up to 100 ms after death/pause, then stop updating until respawn
                    endTick = Math.Max(startTick + 1, (cur.Millis + 100) / 5);
                }
                else
                {
                    endTick = Math.Max(startTick + 1, next.Millis / 5);
                }

                bool jumping = (cur.PhysState & 7) == 0 && cur.Vel.Z > 1.0f;

                for (int t = startTick; t < endTick; ++t)
                {
                    lastEmittedTick = t;
                    bool attacking = shotsByClientTick.Contains((cn, t));
                    bool isPacketTick = (t == startTick);

                    var update = new PlayerUpdateCmd
                    {
                        Tick = t,
                        Move = cur.Move,
                        Strafe = cur.Strafe,
                        Jumping = jumping,
                        Yaw = cur.Yaw,
                        Pitch = cur.Pitch,
                        Attacking = attacking,
                        SelectedGun = cur.SelectedGun,
                        Pos = isPacketTick ? cur.Pos : null,
                        Vel = isPacketTick ? cur.Vel : null,
                        PhysState = cur.PhysState,
                        LifeSequence = cur.LifeSequence
                    };

                    commands.Add(new Command
                    {
                        Op = WorldOp.PlayerUpdate,
                        PeerId = mappedCn,
                        Timestamp = (uint)(t * 5),
                        Sequence = (ulong)(t * 5),
                        Payload = new(update)
                    });
                }
            }
        }

        // Sort all commands chronologically by timestamp
        commands.Sort((a, b) =>
        {
            int cmp = a.Timestamp.CompareTo(b.Timestamp);
            if (cmp != 0) return cmp;
            return GetOpOrder(a.Op).CompareTo(GetOpOrder(b.Op));
        });

        // Initial spawn pos/yaw from the primary player's first valid position
        Vec3f initPos = new Vec3f { X = 0, Y = 0, Z = EyeHeight };
        float initYaw = 0f;
        int primary = activeCns.Count > 0 ? activeCns[0] : 0;
        if (posByPlayer.TryGetValue(primary, out var firstPosList) && firstPosList.Count > 0)
        {
            initPos = firstPosList[0].Pos;
            initYaw = firstPosList[0].Yaw;
        }

        var peers = new List<DemoPeerInfo>();
        foreach (var cn in activeCns)
        {
            int mappedPeer = MapCn(cn);
            var p = dmo.EnsurePlayer(cn);
            peers.Add(new DemoPeerInfo
            {
                PeerId = mappedPeer,
                Name = string.IsNullOrWhiteSpace(p.Name) ? $"Player {mappedPeer}" : p.Name,
                Team = string.IsNullOrWhiteSpace(p.Team) ? "good" : p.Team,
                Playermodel = p.Playermodel
            });
        }

        return new DemoPackage
        {
            FormatVersion = 1,
            MapName = dmo.MapName,
            GameMode = dmo.GameModeName,
            Peers = peers,
            InitialSpawnPos = initPos,
            InitialSpawnYaw = initYaw,
            Commands = commands
        };
    }

    private static int GetOpOrder(WorldOp op) => op switch
    {
        WorldOp.PlayerSpawn => 0,
        WorldOp.PlayerTeleport => 1,
        WorldOp.PlayerJumpPad => 2,
        WorldOp.PlayerDeath => 3,
        WorldOp.PlayerUpdate => 4,
        _ => 5
    };

    public static Vec3f VecFromYawPitch(float yaw, float pitch, int move, int strafe)
    {
        float mx = 0, my = 0, mz = 0;
        if (move != 0)
        {
            mx = move * -MathF.Sin(yaw * Rad);
            my = move * MathF.Cos(yaw * Rad);
        }
        if (pitch != 0)
        {
            mx *= MathF.Cos(pitch * Rad);
            my *= MathF.Cos(pitch * Rad);
            mz = move * MathF.Sin(pitch * Rad);
        }
        if (strafe != 0)
        {
            mx += strafe * MathF.Cos(yaw * Rad);
            my += strafe * MathF.Sin(yaw * Rad);
        }
        return new Vec3f { X = mx, Y = my, Z = mz };
    }
}

/// <summary>
/// Lightweight binary reader for Sauerbraten variable-length integers and strings.
/// </summary>
public ref struct DmoBuffer
{
    private readonly ReadOnlySpan<byte> _bytes;
    private int _offset;

    public DmoBuffer(byte[] buffer, int offset, int length)
    {
        _bytes = buffer.AsSpan(offset, length);
        _offset = 0;
    }

    public DmoBuffer(ReadOnlySpan<byte> span)
    {
        _bytes = span;
        _offset = 0;
    }

    public int Remaining => _bytes.Length - _offset;

    public byte GetByte()
    {
        if (_offset >= _bytes.Length) return 0;
        return _bytes[_offset++];
    }

    public int GetInt()
    {
        sbyte c = (sbyte)GetByte();
        if (c == -128)
        {
            int n = GetByte();
            n |= ((sbyte)GetByte()) << 8;
            return n;
        }
        else if (c == -127)
        {
            int n = GetByte();
            n |= GetByte() << 8;
            n |= GetByte() << 16;
            return n | (((sbyte)GetByte()) << 24);
        }
        else
        {
            return c;
        }
    }

    public int GetUInt()
    {
        int n = GetByte();
        if ((n & 0x80) != 0)
        {
            n += (GetByte() << 7) - 0x80;
            if ((n & (1 << 14)) != 0) n += (GetByte() << 14) - (1 << 14);
            if ((n & (1 << 21)) != 0) n += (GetByte() << 21) - (1 << 21);
            if ((n & (1 << 28)) != 0) n |= unchecked((int)(~0U << 28));
        }
        return n;
    }

    public string GetString()
    {
        var sb = new StringBuilder();
        for (; ; )
        {
            int c = GetInt();
            if (c == 0 || Remaining == 0) break;
            sb.Append((char)(c & 0xFF));
        }
        return sb.ToString();
    }

    public DmoBuffer SubBuffer(int length)
    {
        if (length > Remaining) length = Remaining;
        var sub = new DmoBuffer(_bytes.Slice(_offset, length));
        _offset += length;
        return sub;
    }
}
