// rayprobe — dumps ground truth from the ORIGINAL physics.cpp raycube so the C#
// port (World/Physics/OctaRaycast.cs) can be asserted against the engine's exact
// output rather than hand-derived.
//
// raycube / raycubeintersect / getclipplanes are file-local `static` in
// physics.cpp, so they are copied here verbatim (the same pattern as
// tools/octaprobe/ops.cpp for octaedit.cpp). Entities are stubbed out: the MVP
// has no entity octree (ext->ents), and the editor's RAY_ENTS branch is out of
// scope for this port.
//
// Build: ./build.sh   Run: ./rayprobe
#include "engine/engine.h"
#include <cstdio>
#include <cstring>

// ---- globals physics.cpp normally owns ----
const int MAXCLIPPLANES = 1024;
static clipplanes clipcache[MAXCLIPPLANES];
static int clipcacheversion = -2;
vec hitsurface;
float hitentdist;
int hitent, hitorient;

// The A2 trace hook is only reached when getclipplanes(collide=true) is called;
// raycube uses collide=false, so this is never invoked by the probe.
static void a2traceclip(const cube &c, const ivec &o, int size, const clipplanes &p) {}

static inline clipplanes &getclipplanes(const cube &c, const ivec &o, int size, bool collide = true, int offset = 0)
{
    clipplanes &p = clipcache[int(&c - worldroot)&(MAXCLIPPLANES-1)];
    if(p.owner != &c || p.version != clipcacheversion+offset) 
    {
        p.owner = &c;
        p.version = clipcacheversion+offset;
        genclipplanes(c, o, size, p, collide);
        if(collide) a2traceclip(c, o, size, p);
    }
    return p;
}

void resetclipplanes()
{
    clipcacheversion += 2;
    if(!clipcacheversion)
    {
        memset(clipcache, 0, sizeof(clipcache));
        clipcacheversion = 2;
    }
}

/////////////////////////  ray - cube collision ///////////////////////////////////////////////

#define INTERSECTPLANES(setentry, exit) \
    float enterdist = -1e16f, exitdist = 1e16f; \
    loopi(p.size) \
    { \
        float pdist = p.p[i].dist(v), facing = ray.dot(p.p[i]); \
        if(facing < 0) \
        { \
            pdist /= -facing; \
            if(pdist > enterdist) \
            { \
                if(pdist > exitdist) exit; \
                enterdist = pdist; \
                setentry; \
            } \
        } \
        else if(facing > 0) \
        { \
            pdist /= -facing; \
            if(pdist < exitdist) \
            { \
                if(pdist < enterdist) exit; \
                exitdist = pdist; \
            } \
        } \
        else if(pdist > 0) exit; \
    }

#define INTERSECTBOX(setentry, exit) \
    loop(i, 3) \
    { \
        if(ray[i]) \
        { \
            float prad = fabs(p.r[i] * invray[i]), pdist = (p.o[i] - v[i]) * invray[i], pmin = pdist - prad, pmax = pdist + prad; \
            if(pmin > enterdist) \
            { \
                if(pmin > exitdist) exit; \
                enterdist = pmin; \
                setentry; \
            } \
            if(pmax < exitdist) \
            { \
                if(pmax < enterdist) exit; \
                exitdist = pmax; \
            } \
         } \
         else if(v[i] < p.o[i]-p.r[i] || v[i] > p.o[i]+p.r[i]) exit; \
    }

static inline bool raycubeintersect(const clipplanes &p, const cube &c, const vec &v, const vec &ray, const vec &invray, float &dist)
{
    int entry = -1, bbentry = -1;
    INTERSECTPLANES(entry = i, return false);
    INTERSECTBOX(bbentry = i, return false);
    if(exitdist < 0) return false;
    dist = max(enterdist+0.1f, 0.0f);
    if(bbentry>=0) { hitsurface = vec(0, 0, 0); hitsurface[bbentry] = ray[bbentry]>0 ? -1 : 1; }
    else hitsurface = p.p[entry];
    return true;
}

// Entity stubs: the MVP has no entity octree. raycube calls these only through
// the DOWNOCTREE macro when lc->ext && lc->ext->ents, which never happens here.
static float disttoent(octaentities *oc, const vec &o, const vec &ray, float radius, int mode, extentity *t)
{
    return radius;
}

static float disttooutsideent(const vec &o, const vec &ray, float radius, int mode, extentity *t)
{
    return radius;
}

#define INITRAYCUBE \
    float dist = 0, dent = radius > 0 ? radius : 1e16f; \
    vec v(o), invray(ray.x ? 1/ray.x : 1e16f, ray.y ? 1/ray.y : 1e16f, ray.z ? 1/ray.z : 1e16f); \
    cube *levels[20]; \
    levels[worldscale] = worldroot; \
    int lshift = worldscale, elvl = mode&RAY_BB ? worldscale : 0; \
    ivec lsizemask(invray.x>0 ? 1 : 0, invray.y>0 ? 1 : 0, invray.z>0 ? 1 : 0); \

#define CHECKINSIDEWORLD \
    if(!insideworld(o)) \
    { \
        float disttoworld = 0, exitworld = 1e16f; \
        loopi(3) \
        { \
            float c = v[i]; \
            if(c<0 || c>=worldsize) \
            { \
                float d = ((invray[i]>0?0:worldsize)-c)*invray[i]; \
                if(d<0) return (radius>0?radius:-1); \
                disttoworld = max(disttoworld, 0.1f + d); \
            } \
            float e = ((invray[i]>0?worldsize:0)-c)*invray[i]; \
            exitworld = min(exitworld, e); \
        } \
        if(disttoworld > exitworld) return (radius>0?radius:-1); \
        v.add(vec(ray).mul(disttoworld)); \
        dist += disttoworld; \
    }

#define DOWNOCTREE(disttoent, earlyexit) \
        cube *lc = levels[lshift]; \
        for(;;) \
        { \
            lshift--; \
            lc += octastep(x, y, z, lshift); \
            if(lc->ext && lc->ext->ents && lshift < elvl) \
            { \
                float edist = disttoent(lc->ext->ents, o, ray, dent, mode, t); \
                if(edist < dent) \
                { \
                    earlyexit return min(edist, dist); \
                    elvl = lshift; \
                    dent = min(dent, edist); \
                } \
            } \
            if(lc->children==NULL) break; \
            lc = lc->children; \
            levels[lshift] = lc; \
        }

#define FINDCLOSEST(xclosest, yclosest, zclosest) \
        float dx = (lo.x+(lsizemask.x<<lshift)-v.x)*invray.x, \
              dy = (lo.y+(lsizemask.y<<lshift)-v.y)*invray.y, \
              dz = (lo.z+(lsizemask.z<<lshift)-v.z)*invray.z; \
        float disttonext = dx; \
        xclosest; \
        if(dy < disttonext) { disttonext = dy; yclosest; } \
        if(dz < disttonext) { disttonext = dz; zclosest; } \
        disttonext += 0.1f; \
        v.add(vec(ray).mul(disttonext)); \
        dist += disttonext;

#define UPOCTREE(exitworld) \
        x = int(v.x); \
        y = int(v.y); \
        z = int(v.z); \
        uint diff = uint(lo.x^x)|uint(lo.y^y)|uint(lo.z^z); \
        if(diff >= uint(worldsize)) exitworld; \
        diff >>= lshift; \
        if(!diff) exitworld; \
        do \
        { \
            lshift++; \
            diff >>= 1; \
        } while(diff);

float raycube(const vec &o, const vec &ray, float radius, int mode, int size, extentity *t)
{
    if(ray.iszero()) return 0;

    INITRAYCUBE;
    CHECKINSIDEWORLD;

    int closest = -1, x = int(v.x), y = int(v.y), z = int(v.z);
    for(;;)
    {
        DOWNOCTREE(disttoent, if(mode&RAY_SHADOW));

        int lsize = 1<<lshift;

        cube &c = *lc;
        if((dist>0 || !(mode&RAY_SKIPFIRST)) &&
           (((mode&RAY_CLIPMAT) && isclipped(c.material&MATF_VOLUME)) ||
            ((mode&RAY_EDITMAT) && c.material != MAT_AIR) ||
            (!(mode&RAY_PASS) && lsize==size && !isempty(c)) ||
            isentirelysolid(c) ||
            dent < dist))
        {
            if(closest >= 0) { hitsurface = vec(0, 0, 0); hitsurface[closest] = ray[closest]>0 ? -1 : 1; }
            return min(dent, dist);
        }

        ivec lo(x&(~0U<<lshift), y&(~0U<<lshift), z&(~0U<<lshift));

        if(!isempty(c))
        {
            const clipplanes &p = getclipplanes(c, lo, lsize, false, 1);
            float f = 0;
            if(raycubeintersect(p, c, v, ray, invray, f) && (dist+f>0 || !(mode&RAY_SKIPFIRST)))
                return min(dent, dist+f);
        }

        FINDCLOSEST(closest = 0, closest = 1, closest = 2);

        if(radius>0 && dist>=radius) return min(dent, dist);

        UPOCTREE(return min(dent, radius>0 ? radius : dist));
    }
}

float rayent(const vec &o, const vec &ray, float radius, int mode, int size, int &orient, int &ent)
{
    hitent = -1;
    hitentdist = radius;
    hitorient = -1;
    float dist = raycube(o, ray, radius, mode, size);
    if((mode&RAY_ENTS) == RAY_ENTS)
    {
        float dent = disttooutsideent(o, ray, dist < 0 ? 1e16f : dist, mode, NULL);
        if(dent < 1e15f && (dist < 0 || dent < dist)) dist = dent;
    }
    orient = hitorient;
    ent = hitentdist == dist ? hitent : -1;
    return dist;
}

/////////////////////////  probe scenarios  ///////////////////////////////////////////////

// Axis-aligned hit surfaces map to the O_* orient enum; a diagonal (deformed-face
// plane) normal has no single axis, so report -1 and rely on the raw hit vector.
static int orient_of(const vec &n)
{
    int axes = 0, idx = -1;
    loopi(3) if(n[i] != 0) { axes++; idx = i; }
    return axes == 1 ? idx*2 + (n[idx] > 0 ? 1 : 0) : -1;
}

static void newworld(int scale, uint face = F_EMPTY)
{
    worldscale = scale;
    worldsize = 1 << worldscale;
    worldroot = newcubes(face);
    resetclipplanes();
}

static void dump_raycube(const char *tag, const vec &o, const vec &ray, float radius, int mode, int size)
{
    hitsurface = vec(0, 0, 0);
    float d = raycube(o, ray, radius, mode, size);
    printf("R %-22s dist=%.4f orient=%d hit=(%.6f,%.6f,%.6f)\n",
           tag, d, orient_of(hitsurface), hitsurface.x, hitsurface.y, hitsurface.z);
}

static void dump_rayent(const char *tag, const vec &o, const vec &ray, float radius, int mode, int size)
{
    int orient = -2, ent = -2;
    hitsurface = vec(0, 0, 0);
    float d = rayent(o, ray, radius, mode, size, orient, ent);
    printf("E %-22s dist=%.4f orient=%d ent=%d hit=(%.6f,%.6f,%.6f)\n",
           tag, d, orient, ent, hitsurface.x, hitsurface.y, hitsurface.z);
}

int main()
{
    // 1. Solid world, ray from above: CHECKINSIDEWORLD + isentirelysolid quick hit.
    newworld(4, F_SOLID);
    dump_raycube("solid-above", vec(4,4,20), vec(0,0,-1), 1000, 0, 0);

    // 2. Carved cavity, ray +X at the far wall: DDA (FINDCLOSEST/UPOCTREE) + hitsurface.
    {
        newworld(6, F_SOLID);
        ivec ro; int rs;
        cube &c = lookupcube(ivec(32,32,32), 8, ro, rs);
        discardchildren(c, true);
        emptyfaces(c);
        resetclipplanes();
        dump_raycube("cavity-far", vec(36,36,36), vec(1,0,0), 1000, 0, 0);
        dump_raycube("cavity-near", vec(36,36,36), vec(-1,0,0), 1000, 0, 0);
        dump_raycube("cavity-up", vec(36,36,36), vec(0,0,1), 1000, 0, 0);
    }

    // 3. Deformed cube (edge 0 nibble 4), all neighbours empty: render-hull clip planes.
    {
        newworld(4, F_EMPTY);
        cube &c = worldroot[0];
        solidfaces(c);
        c.edges[0] = 4;
        c.visible = 0x80;
        resetclipplanes();
        dump_raycube("deformed-top", vec(2,2,20), vec(0,0,-1), 1000, 0, 0);
        dump_raycube("deformed-side", vec(-5,4,4), vec(1,0,0), 1000, 0, 0);
        dump_raycube("deformed-slope", vec(12,4,4), vec(-1,0,0), 1000, 0, 0);
    }

    // 4. RAY_SKIPFIRST: eye inside a solid world; the first cube is skipped and
    //    the ray exits the world -> radius (no hit).
    newworld(4, F_SOLID);
    dump_raycube("skipfirst-solid", vec(4,4,8), vec(0,0,-1), 1000, RAY_SKIPFIRST, 0);

    // 5. RAY_EDITMAT / RAY_CLIPMAT material quick hits (empty cube with a material).
    {
        newworld(4, F_EMPTY);
        worldroot[0].material = MAT_WATER;
        dump_raycube("editmat-water", vec(-5,4,4), vec(1,0,0), 1000, RAY_EDITMAT, 0);

        worldroot[0].material = MAT_GLASS;
        dump_raycube("clipmat-glass", vec(-5,4,4), vec(1,0,0), 1000, RAY_CLIPMAT, 0);
    }

    // 6. The editor's actual entry point (rayent), editor mode flags, gridsize 8.
    {
        newworld(6, F_SOLID);
        ivec ro; int rs;
        cube &c = lookupcube(ivec(32,32,32), 8, ro, rs);
        discardchildren(c, true);
        emptyfaces(c);
        resetclipplanes();
        dump_rayent("editor-cavity", vec(36,36,36), vec(1,0,0), 1e16f, RAY_SKIPFIRST, 8);
        dump_rayent("editor-skipfirst", vec(36,36,36), vec(-1,0,0), 1e16f, RAY_SKIPFIRST, 8);
    }

    return 0;
}
