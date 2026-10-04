// refphysics.cpp — VERBATIM movement functions from Sauerbraten SVN r6889.
// Only difference from the original: the game:: stubs and globals the engine
// would normally provide. Keep this file byte-faithful to src/engine/physics.cpp.
#include "refmath.h"

const float JUMPVEL = 125.0f;   // src/engine/physics.cpp:477
const float GRAVITY = 200.0f;   // src/engine/physics.cpp:478

// engine globals referenced by modifyvelocity
physent *player = nullptr;
static float floatspeed = 100.0f;   // default, src/engine/physics.cpp:1587

namespace game
{
    bool allowmove(physent *) { return true; }
    void physicstrigger(physent *, bool, int, int, int) {}
}

// src/engine/physics.cpp:1548
void vecfromyawpitch(float yaw, float pitch, int move, int strafe, vec &m)
{
    if(move)
    {
        m.x = move*-sinf(RAD*yaw);
        m.y = move*cosf(RAD*yaw);
    }
    else m.x = m.y = 0;

    if(pitch)
    {
        m.x *= cosf(RAD*pitch);
        m.y *= cosf(RAD*pitch);
        m.z = move*sinf(RAD*pitch);
    }
    else m.z = 0;

    if(strafe)
    {
        m.x += strafe*cosf(RAD*yaw);
        m.y += strafe*sinf(RAD*yaw);
    }
}

// src/engine/physics.cpp:1589
void modifyvelocity(physent *pl, bool local, bool water, bool floating, int curtime)
{
    bool allowmove = game::allowmove(pl);
    if(floating)
    {
        if(pl->jumping && allowmove)
        {
            pl->jumping = false;
            pl->vel.z = std::max(pl->vel.z, JUMPVEL);
        }
    }
    else if(pl->physstate >= PHYS_SLOPE || water)
    {
        if(water && !pl->inwater) pl->vel.div(8);
        if(pl->jumping && allowmove)
        {
            pl->jumping = false;

            pl->vel.z = std::max(pl->vel.z, JUMPVEL); // physics impulse upwards
            if(water) { pl->vel.x /= 8.0f; pl->vel.y /= 8.0f; } // dampen velocity change even harder, gives correct water feel

            game::physicstrigger(pl, local, 1, 0);
        }
    }
    if(!floating && pl->physstate == PHYS_FALL) pl->timeinair = std::min<int>(pl->timeinair + curtime, 1000);

    vec m(0.0f, 0.0f, 0.0f);
    if((pl->move || pl->strafe) && allowmove)
    {
        vecfromyawpitch(pl->yaw, floating || water || pl->type==ENT_CAMERA ? pl->pitch : 0, pl->move, pl->strafe, m);

        if(!floating && pl->physstate >= PHYS_SLOPE)
        {
            /* move up or down slopes in air
             * but only move up slopes in water
             */
            float dz = -(m.x*pl->floor.x + m.y*pl->floor.y)/pl->floor.z;
            m.z = water ? std::max(m.z, dz) : dz;
        }

        m.normalize();
    }

    vec d(m);
    d.mul(pl->maxspeed);
    if(pl->type==ENT_PLAYER)
    {
        if(floating)
        {
            if(pl==player) d.mul(floatspeed/100.0f);
        }
        else if(!water && allowmove) d.mul((pl->move && !pl->strafe ? 1.3f : 1.0f) * (pl->physstate < PHYS_SLOPE ? 1.3f : 1.0f));
    }
    float fric = water && !floating ? 20.0f : (pl->physstate >= PHYS_SLOPE || floating ? 6.0f : 30.0f);
    pl->vel.lerp(d, pl->vel, (float)pow((double)(1 - 1/fric), (double)(curtime/20.0f)));
}

// src/engine/physics.cpp:1650
void modifygravity(physent *pl, bool water, int curtime)
{
    float secs = curtime/1000.0f;
    vec g(0, 0, 0);
    if(pl->physstate == PHYS_FALL) g.z -= GRAVITY*secs;
    else if(pl->floor.z > 0 && pl->floor.z < FLOORZ)
    {
        g.z = -1;
        g.project(pl->floor);
        g.normalize();
        g.mul(GRAVITY*secs);
    }
    if(!water || !game::allowmove(pl) || (!pl->move && !pl->strafe)) pl->falling.add(g);

    if(water || pl->physstate >= PHYS_SLOPE)
    {
        float fric = water ? 2.0f : 6.0f,
              c = water ? 1.0f : std::min(std::max((pl->floor.z - SLOPEZ)/(FLOORZ-SLOPEZ), 0.0f), 1.0f);
        pl->falling.mul((float)pow((double)(1 - c/fric), (double)(curtime/20.0f)));
    }
}
