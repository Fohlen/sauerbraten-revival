// refmath.h — minimal, faithful shim so the ORIGINAL Sauerbraten movement
// functions (SVN r6889) can be compiled standalone. Vector semantics mirror
// src/shared/geom.h; entity fields mirror src/shared/ents.h.
#pragma once
#include <cmath>
#include <algorithm>
#include <cstdio>
#include <vector>
#include <string>

struct vec
{
    float x, y, z;
    vec() : x(0), y(0), z(0) {}
    vec(float x, float y, float z) : x(x), y(y), z(z) {}

    float dot(const vec &o) const { return x*o.x + y*o.y + z*o.z; }
    float squaredlen() const { return x*x + y*y + z*z; }
    float magnitude() const { return sqrtf(squaredlen()); }
    bool iszero() const { return x == 0 && y == 0 && z == 0; }

    vec &set(float a, float b, float c) { x = a; y = b; z = c; return *this; }
    vec &mul(float f) { x *= f; y *= f; z *= f; return *this; }
    vec &div(float f) { x /= f; y /= f; z /= f; return *this; }
    vec &add(const vec &o) { x += o.x; y += o.y; z += o.z; return *this; }
    vec &sub(const vec &o) { x -= o.x; y -= o.y; z -= o.z; return *this; }
    vec &normalize() { div(magnitude()); return *this; }
    vec &project(const vec &n) { float k = dot(n); x -= k*n.x; y -= k*n.y; z -= k*n.z; return *this; }

    vec &projectxydir(const vec &n) { if(n.z) z = -(x*n.x/n.z + y*n.y/n.z); return *this; }
    vec &rescale(float k)
    {
        float mag = magnitude();
        if(mag > 1e-6f) mul(k / mag);
        return *this;
    }
    vec &projectxy(const vec &n, float threshold)
    {
        float m = squaredlen(), k = std::min(dot(n), threshold);
        projectxydir(n);
        rescale(sqrtf(std::max(m - k*k, 0.0f)));
        return *this;
    }
    vec &lerp(const vec &a, const vec &b, float t)
    {
        x = a.x + (b.x-a.x)*t; y = a.y + (b.y-a.y)*t; z = a.z + (b.z-a.z)*t;
        return *this;
    }
};

// physstate / entity types (src/shared/ents.h:62)
enum { PHYS_FLOAT = 0, PHYS_FALL, PHYS_SLIDE, PHYS_SLOPE, PHYS_FLOOR, PHYS_STEP_UP, PHYS_STEP_DOWN, PHYS_BOUNCE };
enum { ENT_PLAYER = 0, ENT_AI, ENT_INANIMATE, ENT_CAMERA, ENT_BOUNCE };

// physics constants (src/engine/physics.cpp:473-478)
static const float STAIRHEIGHT = 4.1f;
static const float FLOORZ = 0.867f;
static const float SLOPEZ = 0.5f;
static const float WALLZ = 0.2f;
extern const float JUMPVEL;   // = 125.0f
extern const float GRAVITY;   // = 200.0f
static const float RAD = float(M_PI) / 180.0f;

// physent fields used by the movement model (src/shared/ents.h:68, defaults :91)
struct physent
{
    vec o, vel, falling;
    float yaw, pitch, roll;
    float maxspeed;
    float radius, eyeheight, aboveeye;
    vec floor;
    unsigned short timeinair;
    unsigned char inwater;
    bool jumping;
    signed char move, strafe;
    unsigned char physstate, state, type;
    bool blocked;

    physent()
      : o(0,0,0), vel(0,0,0), falling(0,0,0), yaw(0), pitch(0), roll(0),
        maxspeed(100), radius(4.1f), eyeheight(14), aboveeye(1), floor(0,0,1),
        timeinair(0), inwater(0), jumping(false), move(0), strafe(0),
        physstate(PHYS_FALL), state(0), type(ENT_PLAYER), blocked(false) {}
};

// game:: stubs used by the extracted functions
namespace game
{
    bool allowmove(physent *d);
    void physicstrigger(physent *d, bool local, int floorlevel, int waterlevel, int material = -1);
}

// extracted originals
void vecfromyawpitch(float yaw, float pitch, int move, int strafe, vec &m);
void modifyvelocity(physent *pl, bool local, bool water, bool floating, int curtime);
void modifygravity(physent *pl, bool water, int curtime);
