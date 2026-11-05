#include <zlib.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <string>
#include <vector>
#include <map>
#include <iostream>
#include <sstream>
#include <iomanip>

#define DEMO_MAGIC "SAUERBRATEN_DEMO"
#define DEMO_VERSION 1
#define PROTOCOL_VERSION 260
#define DMF 16.0f
#define DVELF 1.0f
#define RAD (3.14159265358979323846f / 180.0f)

struct demoheader {
    char magic[16];
    int version, protocol;
};

struct vec {
    float x, y, z;
    vec() : x(0), y(0), z(0) {}
    vec(float x, float y, float z) : x(x), y(y), z(z) {}
};

static void vecfromyawpitch(float yaw, float pitch, int move, int strafe, vec &m) {
    if (move) {
        m.x = move * -sinf(RAD * yaw);
        m.y = move * cosf(RAD * yaw);
    } else {
        m.x = m.y = 0;
    }
    if (pitch) {
        m.x *= cosf(RAD * pitch);
        m.y *= cosf(RAD * pitch);
        m.z = move * sinf(RAD * pitch);
    } else {
        m.z = 0;
    }
    if (strafe) {
        m.x += strafe * cosf(RAD * yaw);
        m.y += strafe * sinf(RAD * yaw);
    }
}

static const char *gamemodename(int mode) {
    static const char *names[] = {
        "SP", "DMSP", "demo", "ffa", "coop edit", "teamplay", "instagib",
        "insta team", "efficiency", "effic team", "tactics", "tac team",
        "capture", "regen capture", "ctf", "insta ctf", "protect",
        "insta protect", "hold", "insta hold", "effic ctf", "effic protect",
        "effic hold", "collect", "insta collect", "effic collect"
    };
    int idx = mode + 3;
    if (idx >= 0 && idx < (int)(sizeof(names) / sizeof(names[0]))) {
        return names[idx];
    }
    return "unknown";
}

struct ucharbuf {
    const unsigned char *buf;
    int len;
    int offset;

    ucharbuf(const unsigned char *b, int l) : buf(b), len(l), offset(0) {}
    int remaining() const { return len - offset; }
    unsigned char get() {
        if (offset >= len) return 0;
        return buf[offset++];
    }
    ucharbuf subbuf(int sublen) {
        if (sublen > remaining()) sublen = remaining();
        ucharbuf res(buf + offset, sublen);
        offset += sublen;
        return res;
    }
};

static int getint(ucharbuf &p) {
    signed char c = (signed char)p.get();
    if (c == -128) {
        int n = p.get();
        n |= ((signed char)p.get()) << 8;
        return n;
    } else if (c == -127) {
        int n = p.get();
        n |= p.get() << 8;
        n |= p.get() << 16;
        return n | (((signed char)p.get()) << 24);
    } else {
        return c;
    }
}

static int getuint(ucharbuf &p) {
    int n = p.get();
    if (n & 0x80) {
        n += (p.get() << 7) - 0x80;
        if (n & (1 << 14)) n += (p.get() << 14) - (1 << 14);
        if (n & (1 << 21)) n += (p.get() << 21) - (1 << 21);
        if (n & (1 << 28)) n |= ~0U << 28;
    }
    return n;
}

static void getstring(char *text, ucharbuf &p, size_t maxlen) {
    size_t i = 0;
    for (;;) {
        int c = getint(p);
        if (c == 0 || !p.remaining()) {
            if (i < maxlen) text[i] = 0;
            break;
        }
        if (i + 1 < maxlen) text[i++] = (char)c;
    }
    if (maxlen > 0) text[maxlen - 1] = 0;
}

struct PlayerInfo {
    int clientnum;
    std::string name;
    std::string team;
    int playermodel;
    int frags;
    int deaths;
    int positions;
    int shots;
};

struct SamplePos {
    int millis;
    int cn;
    float x, y, z;
    float yaw, pitch, roll;
    float vx, vy, vz;
    int move, strafe;
    int physstate;
};

static std::string escape_json(const std::string &s) {
    std::ostringstream o;
    for (char c : s) {
        if (c == '"') o << "\\\"";
        else if (c == '\\') o << "\\\\";
        else if (c == '\b') o << "\\b";
        else if (c == '\f') o << "\\f";
        else if (c == '\n') o << "\\n";
        else if (c == '\r') o << "\\r";
        else if (c == '\t') o << "\\t";
        else if ((unsigned char)c < ' ') o << "\\u" << std::hex << std::setw(4) << std::setfill('0') << (int)(unsigned char)c;
        else o << c;
    }
    return o.str();
}

int main(int argc, char **argv) {
    std::string demofile = "";
    bool json_output = false;
    int sample_limit_per_player = 5;

    for (int i = 1; i < argc; ++i) {
        std::string arg = argv[i];
        if (arg == "--json") {
            json_output = true;
        } else if (arg == "--sample-limit" && i + 1 < argc) {
            sample_limit_per_player = std::atoi(argv[++i]);
        } else if (arg[0] != '-') {
            demofile = arg;
        }
    }

    if (demofile.empty()) {
        std::cerr << "Usage: dmoprobe <demofile.dmo> [--json] [--sample-limit N]\n";
        return 1;
    }

    gzFile gz = gzopen(demofile.c_str(), "rb");
    if (!gz) {
        std::cerr << "Cannot open demo file: " << demofile << "\n";
        return 1;
    }

    demoheader hdr;
    if (gzread(gz, &hdr, sizeof(hdr)) != sizeof(hdr)) {
        std::cerr << "Failed to read demo header\n";
        gzclose(gz);
        return 1;
    }

    char magic[17];
    memcpy(magic, hdr.magic, 16);
    magic[16] = 0;
    while (strlen(magic) > 0 && (magic[strlen(magic)-1] == ' ' || magic[strlen(magic)-1] == '\0')) {
        magic[strlen(magic)-1] = '\0';
    }

    if (strcmp(magic, DEMO_MAGIC) != 0 && strncmp(magic, DEMO_MAGIC, 15) != 0) {
        std::cerr << "Invalid demo magic: " << magic << "\n";
        gzclose(gz);
        return 1;
    }

    std::string mapname = "";
    int gamemode = -1;
    std::map<int, PlayerInfo> players;
    std::vector<SamplePos> sample_positions;
    int total_positions = 0;
    int total_shots = 0;
    int total_deaths = 0;
    int total_damage = 0;
    int total_spawns = 0;

    auto ensure_player = [&](int cn) -> PlayerInfo& {
        auto it = players.find(cn);
        if (it == players.end()) {
            PlayerInfo pi;
            pi.clientnum = cn;
            pi.name = "unnamed";
            pi.team = "none";
            pi.playermodel = 0;
            pi.frags = 0;
            pi.deaths = 0;
            pi.positions = 0;
            pi.shots = 0;
            players[cn] = pi;
        }
        return players[cn];
    };

    auto parse_positions = [&](ucharbuf &p, int millis) {
        while (p.remaining() > 0) {
            int type = getint(p);
            if (type == 114) continue; // N_DEMOPACKET
            if (type == 4) { // N_POS
                int cn = getuint(p);
                int physstate = p.get();
                int flags = getuint(p);
                vec o, vel, falling;
                float yaw, pitch, roll;
                for (int k = 0; k < 3; ++k) {
                    int n = p.get();
                    n |= p.get() << 8;
                    if (flags & (1 << k)) {
                        n |= p.get() << 16;
                        if (n & 0x800000) n |= ~0U << 24;
                    }
                    if (k == 0) o.x = n / DMF;
                    else if (k == 1) o.y = n / DMF;
                    else o.z = n / DMF;
                }
                int dir = p.get(); dir |= p.get() << 8;
                yaw = dir % 360;
                pitch = std::max(0, std::min(180, dir / 360)) - 90;
                roll = std::max(0, std::min(180, (int)p.get())) - 90;
                int mag = p.get();
                if (flags & (1 << 3)) mag |= p.get() << 8;
                dir = p.get(); dir |= p.get() << 8;
                vecfromyawpitch(dir % 360, std::max(0, std::min(180, dir / 360)) - 90, 1, 0, vel);
                vel.x *= mag / DVELF; vel.y *= mag / DVELF; vel.z *= mag / DVELF;
                if (flags & (1 << 4)) {
                    mag = p.get();
                    if (flags & (1 << 5)) mag |= p.get() << 8;
                    if (flags & (1 << 6)) {
                        dir = p.get(); dir |= p.get() << 8;
                        vecfromyawpitch(dir % 360, std::max(0, std::min(180, dir / 360)) - 90, 1, 0, falling);
                    } else {
                        falling = vec(0, 0, -1);
                    }
                    falling.x *= mag / DVELF; falling.y *= mag / DVELF; falling.z *= mag / DVELF;
                } else {
                    falling = vec(0, 0, 0);
                }

                int move = (physstate >> 4) & 2 ? -1 : ((physstate >> 4) & 1);
                int strafe = (physstate >> 6) & 2 ? -1 : ((physstate >> 6) & 1);
                int phys = physstate & 7;

                total_positions++;
                PlayerInfo &pi = ensure_player(cn);
                pi.positions++;

                if (pi.positions <= sample_limit_per_player) {
                    SamplePos sp;
                    sp.millis = millis;
                    sp.cn = cn;
                    sp.x = o.x; sp.y = o.y; sp.z = o.z;
                    sp.yaw = yaw; sp.pitch = pitch; sp.roll = roll;
                    sp.vx = vel.x; sp.vy = vel.y; sp.vz = vel.z;
                    sp.move = move; sp.strafe = strafe;
                    sp.physstate = phys;
                    sample_positions.push_back(sp);
                }
            } else if (type == 28) { // N_TELEPORT
                getint(p); getint(p); getint(p);
            } else if (type == 29) { // N_JUMPPAD
                getint(p); getint(p);
            } else {
                break;
            }
        }
    };

    auto parse_messages_func = [&](auto &self, ucharbuf &p, int millis, int cn_ctx) -> void {
        char text[1024];
        while (p.remaining() > 0) {
            int type = getint(p);
            switch (type) {
                case 114: break; // N_DEMOPACKET
                case 1: { // N_SERVINFO
                    getint(p); getint(p); getint(p); getint(p);
                    getstring(text, p, sizeof(text));
                    getstring(text, p, sizeof(text));
                    break;
                }
                case 2: break; // N_WELCOME
                case 3: { // N_INITCLIENT
                    int cn = getint(p);
                    getstring(text, p, sizeof(text));
                    std::string name = text;
                    getstring(text, p, sizeof(text));
                    std::string team = text;
                    int model = getint(p);
                    PlayerInfo &pi = ensure_player(cn);
                    pi.name = name;
                    pi.team = team;
                    pi.playermodel = model;
                    break;
                }
                case 4: { // N_POS (in chan 1)
                    parse_positions(p, millis);
                    return;
                }
                case 5: { // N_TEXT
                    getstring(text, p, sizeof(text));
                    break;
                }
                case 6: getint(p); break; // N_SOUND
                case 7: getint(p); break; // N_CDIS
                case 11: { // N_DIED
                    int vcn = getint(p), acn = getint(p), frags = getint(p), tfrags = getint(p);
                    total_deaths++;
                    PlayerInfo &act = ensure_player(acn);
                    act.frags = frags;
                    PlayerInfo &vic = ensure_player(vcn);
                    vic.deaths++;
                    break;
                }
                case 12: { // N_DAMAGE
                    int tcn = getint(p), acn = getint(p), dmg = getint(p), arm = getint(p), hp = getint(p);
                    total_damage++;
                    break;
                }
                case 13: { // N_HITPUSH
                    getint(p); getint(p); getint(p);
                    getint(p); getint(p); getint(p);
                    break;
                }
                case 14: { // N_SHOTFX
                    int scn = getint(p), gun = getint(p), sid = getint(p);
                    for (int k = 0; k < 6; ++k) getint(p);
                    total_shots++;
                    ensure_player(scn).shots++;
                    break;
                }
                case 17: // N_SPAWNSTATE
                case 18: { // N_SPAWN
                    total_spawns++;
                    if (type == 17) getint(p); // scn
                    getint(p); getint(p); getint(p); getint(p); getint(p); getint(p); // lifeseq, hp, maxhp, arm, armtype, gun
                    for (int k = 0; k < 6; ++k) getint(p); // ammo
                    break;
                }
                case 19: getint(p); break; // N_FORCEDEATH
                case 20: getint(p); break; // N_GUNSELECT
                case 21: break; // N_TAUNT
                case 22: { // N_MAPCHANGE
                    getstring(text, p, sizeof(text));
                    mapname = text;
                    gamemode = getint(p);
                    getint(p); // hasitems
                    break;
                }
                case 24: { // N_TEAMINFO
                    for (;;) {
                        getstring(text, p, sizeof(text));
                        if (p.remaining() == 0 || !text[0]) break;
                        getint(p); // frags
                    }
                    break;
                }
                case 28: getint(p); getint(p); getint(p); break; // N_TELEPORT
                case 29: getint(p); getint(p); break; // N_JUMPPAD
                case 30: getint(p); break; // N_PING
                case 31: getint(p); break; // N_PONG
                case 32: getint(p); break; // N_CLIENTPING
                case 33: getint(p); break; // N_TIMEUP
                case 35: getstring(text, p, sizeof(text)); break; // N_SERVMSG
                case 36: { // N_ITEMLIST
                    while (getint(p) >= 0) getint(p);
                    break;
                }
                case 37: { // N_RESUME
                    while (p.remaining() > 0) {
                        int cn = getint(p);
                        if (cn < 0) break;
                        for (int k = 0; k < 17; ++k) getint(p);
                    }
                    break;
                }
                case 58: getint(p); break; // N_MASTERMODE
                case 61: { // N_CURRENTMASTER
                    getint(p);
                    while (getint(p) >= 0) getint(p);
                    break;
                }
                case 62: getint(p); getint(p); break; // N_SPECTATOR
                case 64: getint(p); getstring(text, p, sizeof(text)); getint(p); break; // N_SETTEAM
                case 70: getint(p); break; // N_ANNOUNCE
                case 72: { // N_SENDDEMOLIST
                    int count = getint(p);
                    for (int i = 0; i < count; ++i) getstring(text, p, sizeof(text));
                    break;
                }
                case 75: getint(p); getint(p); break; // N_DEMOPLAYBACK
                case 77: break; // N_STOPDEMO
                case 79: getint(p); getint(p); getint(p); break; // N_TAKEFLAG
                case 80: getint(p); getint(p); getint(p); break; // N_RETURNFLAG
                case 81: getint(p); getint(p); getint(p); getint(p); getint(p); break; // N_RESETFLAG
                case 84: getint(p); getint(p); getint(p); getint(p); getint(p); getint(p); break; // N_DROPFLAG
                case 85: for (int k = 0; k < 9; ++k) getint(p); break; // N_SCOREFLAG
                case 86: { // N_INITFLAGS
                    int flags = getint(p);
                    for (int k = 0; k < flags; ++k) {
                        getint(p); getint(p); getint(p); int dropped = getint(p);
                        if (dropped) { getint(p); getint(p); getint(p); }
                    }
                    break;
                }
                case 87: getstring(text, p, sizeof(text)); break; // N_SAYTEAM
                case 88: { // N_CLIENT
                    int cn = getint(p);
                    int sublen = getuint(p);
                    ucharbuf sub = p.subbuf(sublen);
                    self(self, sub, millis, cn);
                    break;
                }
                case 94: getint(p); getint(p); break; // N_PAUSEGAME
                case 95: getint(p); getint(p); break; // N_GAMESPEED
                case 104: getstring(text, p, sizeof(text)); break; // N_SWITCHNAME
                case 105: getint(p); break; // N_SWITCHMODEL
                case 106: getstring(text, p, sizeof(text)); break; // N_SWITCHTEAM
                default:
                    std::cerr << "Unhandled msg type " << type << " at " << millis << "\n";
                    return;
            }
        }
    };

    int records = 0;
    int last_millis = 0;
    while (true) {
        int stamp[3];
        if (gzread(gz, stamp, sizeof(stamp)) != sizeof(stamp)) break;
        int millis = stamp[0], chan = stamp[1], length = stamp[2];
        last_millis = millis;
        std::vector<unsigned char> data(length);
        if (gzread(gz, data.data(), length) != length) break;
        records++;
        ucharbuf b(data.data(), length);
        if (chan == 0) {
            parse_positions(b, millis);
        } else if (chan == 1) {
            parse_messages_func(parse_messages_func, b, millis, -1);
        }
    }
    gzclose(gz);

    if (json_output) {
        std::cout << "{\n";
        std::cout << "  \"magic\": \"" << escape_json(magic) << "\",\n";
        std::cout << "  \"version\": " << hdr.version << ",\n";
        std::cout << "  \"protocol\": " << hdr.protocol << ",\n";
        std::cout << "  \"map\": \"" << escape_json(mapname) << "\",\n";
        std::cout << "  \"gamemode\": " << gamemode << ",\n";
        std::cout << "  \"gamemode_name\": \"" << gamemodename(gamemode) << "\",\n";
        std::cout << "  \"duration_millis\": " << last_millis << ",\n";
        std::cout << "  \"total_records\": " << records << ",\n";
        std::cout << "  \"total_positions\": " << total_positions << ",\n";
        std::cout << "  \"total_shots\": " << total_shots << ",\n";
        std::cout << "  \"total_deaths\": " << total_deaths << ",\n";
        std::cout << "  \"total_spawns\": " << total_spawns << ",\n";
        std::cout << "  \"players\": [\n";
        bool first_p = true;
        for (auto &kv : players) {
            PlayerInfo &pi = kv.second;
            if (!first_p) std::cout << ",\n";
            first_p = false;
            std::cout << "    {\n";
            std::cout << "      \"cn\": " << pi.clientnum << ",\n";
            std::cout << "      \"name\": \"" << escape_json(pi.name) << "\",\n";
            std::cout << "      \"team\": \"" << escape_json(pi.team) << "\",\n";
            std::cout << "      \"playermodel\": " << pi.playermodel << ",\n";
            std::cout << "      \"frags\": " << pi.frags << ",\n";
            std::cout << "      \"deaths\": " << pi.deaths << ",\n";
            std::cout << "      \"shots\": " << pi.shots << ",\n";
            std::cout << "      \"positions\": " << pi.positions << "\n";
            std::cout << "    }";
        }
        std::cout << "\n  ],\n";
        std::cout << "  \"sample_positions\": [\n";
        bool first_sp = true;
        for (auto &sp : sample_positions) {
            if (!first_sp) std::cout << ",\n";
            first_sp = false;
            std::cout << "    {\n";
            std::cout << "      \"millis\": " << sp.millis << ",\n";
            std::cout << "      \"cn\": " << sp.cn << ",\n";
            std::cout << "      \"x\": " << sp.x << ",\n";
            std::cout << "      \"y\": " << sp.y << ",\n";
            std::cout << "      \"z\": " << sp.z << ",\n";
            std::cout << "      \"yaw\": " << sp.yaw << ",\n";
            std::cout << "      \"pitch\": " << sp.pitch << ",\n";
            std::cout << "      \"roll\": " << sp.roll << ",\n";
            std::cout << "      \"vx\": " << sp.vx << ",\n";
            std::cout << "      \"vy\": " << sp.vy << ",\n";
            std::cout << "      \"vz\": " << sp.vz << ",\n";
            std::cout << "      \"move\": " << sp.move << ",\n";
            std::cout << "      \"strafe\": " << sp.strafe << ",\n";
            std::cout << "      \"physstate\": " << sp.physstate << "\n";
            std::cout << "    }";
        }
        std::cout << "\n  ]\n";
        std::cout << "}\n";
    } else {
        std::cout << "Demo magic: " << magic << ", version: " << hdr.version << ", protocol: " << hdr.protocol << "\n";
        std::cout << "Map: " << mapname << ", mode: " << gamemodename(gamemode) << " (" << gamemode << ")\n";
        std::cout << "Duration: " << last_millis / 1000 << "s (" << last_millis << "ms), records: " << records << "\n";
        std::cout << "Positions: " << total_positions << ", Shots: " << total_shots << ", Deaths: " << total_deaths << ", Spawns: " << total_spawns << "\n";
        std::cout << "\nPlayers:\n";
        for (auto &kv : players) {
            PlayerInfo &pi = kv.second;
            if (pi.positions > 0 || pi.shots > 0 || pi.frags > 0 || pi.deaths > 0) {
                std::cout << "  Client " << pi.clientnum << ": " << pi.name << " (team: " << pi.team << ") "
                          << "Frags: " << pi.frags << " Deaths: " << pi.deaths << " Shots: " << pi.shots
                          << " Positions: " << pi.positions << "\n";
            }
        }
        std::cout << "\nSample positions (first " << sample_limit_per_player << " per player):\n";
        for (auto &sp : sample_positions) {
            std::cout << "  [millis=" << sp.millis << ", cn=" << sp.cn << "] pos=(" << sp.x << ", " << sp.y << ", " << sp.z << ") "
                      << "yaw=" << sp.yaw << " pitch=" << sp.pitch << " vel=(" << sp.vx << ", " << sp.vy << ", " << sp.vz << ") "
                      << "move=" << sp.move << " strafe=" << sp.strafe << " phys=" << sp.physstate << "\n";
        }
    }

    return 0;
}
