// main.cpp — A1/A4 driver: steps the ORIGINAL movement model at fixed 5 ms,
// emits golden CSV traces (A1), and verifies the model is deterministic (A4).
// Flat-floor collision is a stand-in for move() while we isolate the velocity
// model; real geometry is exercised in A3.
#include "refmath.h"
#include <functional>

// minimal flat-floor stand-in for move()/findfloor()/landing()
static void move_flat(physent *pl, const vec &dir)
{
    pl->o.add(dir);
    const float groundz = 0.0f;
    float feet = pl->o.z - pl->eyeheight;
    if(feet < groundz)
    {
        pl->o.z = groundz + pl->eyeheight;
        if(pl->vel.z < 0) pl->vel.z = 0;
        pl->falling = vec(0, 0, 0);
        pl->physstate = PHYS_FLOOR;
        pl->floor = vec(0, 0, 1);
        pl->timeinair = 0;
        pl->blocked = true;
    }
    else if(pl->physstate >= PHYS_SLOPE && feet > groundz + 0.1f)
        pl->physstate = PHYS_FALL;
}

// mirrors moveplayer() (src/engine/physics.cpp:1681) minus real collision
static void step(physent *pl, int curtime)
{
    float secs = curtime/1000.0f;
    modifygravity(pl, false, curtime);
    modifyvelocity(pl, true, false, false, curtime);

    vec d(pl->vel);
    d.add(pl->falling);
    d.mul(secs);
    pl->blocked = false;

    const int moveres = 10;
    float f = 1.0f/moveres;
    d.mul(f);
    for(int i = 0; i < moveres; i++) move_flat(pl, d);
}

struct Summary { float topSpeed = 0, apex = 0; int airtimeMs = -1; };

static std::vector<std::string> simulate(int steps, const std::function<void(physent*, int)> &input, Summary &out)
{
    physent pl;
    pl.o = vec(0, 0, pl.eyeheight);
    pl.physstate = PHYS_FLOOR;
    pl.floor = vec(0, 0, 1);

    std::vector<std::string> lines;
    lines.push_back("t,move,strafe,jumping,yaw,ox,oy,oz,vx,vy,vz,fx,fy,fz,physstate");

    float apex = 0, topSpeed = 0;
    int t_leave = -1, t_land = -1;
    char buf[256];
    for(int s = 0; s < steps; s++)
    {
        input(&pl, s);
        step(&pl, 5);

        float hspeed = sqrtf(pl.vel.x*pl.vel.x + pl.vel.y*pl.vel.y);
        topSpeed = std::max(topSpeed, hspeed);
        apex = std::max(apex, pl.o.z - pl.eyeheight);
        if(pl.physstate == PHYS_FALL && t_leave < 0) t_leave = s;
        if(t_leave >= 0 && t_land < 0 && pl.physstate == PHYS_FLOOR && s > t_leave) t_land = s;

        snprintf(buf, sizeof(buf), "%d,%d,%d,%d,%.3f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%d",
            s, (int)pl.move, (int)pl.strafe, pl.jumping?1:0, pl.yaw,
            pl.o.x, pl.o.y, pl.o.z, pl.vel.x, pl.vel.y, pl.vel.z,
            pl.falling.x, pl.falling.y, pl.falling.z, pl.physstate);
        lines.push_back(buf);
    }
    out.topSpeed = topSpeed;
    out.apex = apex;
    out.airtimeMs = (t_leave >= 0 && t_land >= 0) ? (t_land - t_leave) * 5 : -1;
    return lines;
}

static void writeLines(const std::string &name, const std::vector<std::string> &lines)
{
    FILE *f = fopen((std::string("out/") + name + ".csv").c_str(), "w");
    for(const auto &l : lines) fprintf(f, "%s\n", l.c_str());
    fclose(f);
}

// A4: same inputs must produce byte-identical output
static bool checkDeterminism()
{
    auto input = [](physent *p, int s)
    {
        if(s == 0 || s == 120) p->jumping = true;
        p->move = 1; p->strafe = 1; p->yaw = 37.5f;
    };
    Summary s1, s2;
    std::vector<std::string> a = simulate(600, input, s1);
    std::vector<std::string> b = simulate(600, input, s2);
    if(a.size() != b.size()) return false;
    for(size_t i = 0; i < a.size(); i++) if(a[i] != b[i]) return false;
    return true;
}

int main()
{
    system("mkdir -p out");
    printf("A1 reference harness (original r6889 movement, 5 ms fixed step)\n");

    auto report = [](const char *name, int steps, const std::function<void(physent*, int)> &input)
    {
        Summary s;
        std::vector<std::string> lines = simulate(steps, input, s);
        writeLines(name, lines);
        std::string air = s.airtimeMs >= 0 ? std::to_string(s.airtimeMs) + "ms" : "-";
        printf("%-16s topHspeed=%8.3f  apex=%8.3f  airtime=%s\n", name, s.topSpeed, s.apex, air.c_str());
    };

    report("ground_forward", 200, [](physent *p, int){ p->move = 1; });
    report("ground_diag",    200, [](physent *p, int){ p->move = 1; p->strafe = 1; });
    report("ground_strafe",  200, [](physent *p, int){ p->strafe = 1; });
    report("jump_only",      400, [](physent *p, int s){ if(s == 0) p->jumping = true; });
    report("jump_forward",   400, [](physent *p, int s){ if(s == 0) p->jumping = true; p->move = 1; });

    bool determinism = checkDeterminism();
    printf("A4 determinism: %s\n", determinism ? "PASS" : "FAIL");
    return determinism ? 0 : 1;
}
