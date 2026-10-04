// main.cpp — A1 driver: steps the ORIGINAL movement model at fixed 5 ms and
// emits golden CSV traces. Flat-floor collision is a stand-in for move() while
// we isolate the velocity model; real geometry is exercised in A3.
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

static void run(const std::string &name, int steps, const std::function<void(physent*, int)> &input)
{
    physent pl;
    pl.o = vec(0, 0, pl.eyeheight);
    pl.physstate = PHYS_FLOOR;
    pl.floor = vec(0, 0, 1);

    FILE *csv = fopen((std::string("out/") + name + ".csv").c_str(), "w");
    fprintf(csv, "t,move,strafe,jumping,yaw,ox,oy,oz,vx,vy,vz,fx,fy,fz,physstate\n");

    float apex = 0, topSpeed = 0;
    int t_leave = -1, t_land = -1;
    for(int s = 0; s < steps; s++)
    {
        input(&pl, s);
        step(&pl, 5);

        float hspeed = sqrtf(pl.vel.x*pl.vel.x + pl.vel.y*pl.vel.y);
        topSpeed = std::max(topSpeed, hspeed);
        apex = std::max(apex, pl.o.z - pl.eyeheight);
        if(pl.physstate == PHYS_FALL && t_leave < 0) t_leave = s;
        if(t_leave >= 0 && t_land < 0 && pl.physstate == PHYS_FLOOR && s > t_leave) t_land = s;

        fprintf(csv, "%d,%d,%d,%d,%.3f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%d\n",
            s, (int)pl.move, (int)pl.strafe, pl.jumping?1:0, pl.yaw,
            pl.o.x, pl.o.y, pl.o.z, pl.vel.x, pl.vel.y, pl.vel.z,
            pl.falling.x, pl.falling.y, pl.falling.z, pl.physstate);
    }
    fclose(csv);

    std::string air = (t_leave >= 0 && t_land >= 0) ? std::to_string((t_land - t_leave) * 5) + "ms" : "-";
    printf("%-16s topHspeed=%8.3f  apex=%8.3f  airtime=%s\n", name.c_str(), topSpeed, apex, air.c_str());
}

int main()
{
    system("mkdir -p out");
    printf("A1 reference harness (original r6889 movement, 5 ms fixed step)\n");
    run("ground_forward", 200, [](physent *p, int){ p->move = 1; });
    run("ground_diag",    200, [](physent *p, int){ p->move = 1; p->strafe = 1; });
    run("ground_strafe",  200, [](physent *p, int){ p->strafe = 1; });
    run("jump_only",      400, [](physent *p, int s){ if(s == 0) p->jumping = true; });
    run("jump_forward",   400, [](physent *p, int s){ if(s == 0) p->jumping = true; p->move = 1; });
    return 0;
}
