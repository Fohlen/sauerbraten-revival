// ops.cpp — verbatim copies of the octaedit.cpp op functions the probe exercises.
// They are file-local (static) in the original and not exported by any header, so
// we copy them here (like refphysics.cpp copies movement functions) to keep the
// ground truth faithful. Nothing here is our own logic — it is copied from
// octaedit.cpp (r6889).
#include "engine/engine.h"

#define OPP_STATIC static
static bool crushededge(uchar e, int dc);

OPP_STATIC int bounded(int n) { return n<0 ? 0 : (n>8 ? 8 : n); }

OPP_STATIC void pushedge(uchar &edge, int dir, int dc)
{
    int ne = bounded(edgeget(edge, dc)+dir);
    edgeset(edge, dc, ne);
    int oe = edgeget(edge, 1-dc);
    if((dir<0 && dc && oe>ne) || (dir>0 && dc==0 && oe<ne)) edgeset(edge, 1-dc, ne);
}

OPP_STATIC void linkedpush(cube &c, int d, int x, int y, int dc, int dir)
{
    ivec v, p;
    getcubevector(c, d, x, y, dc, v);
    loopi(2) loopj(2)
    {
        getcubevector(c, d, i, j, dc, p);
        if(v==p) pushedge(cubeedge(c, d, i, j), dir, dc);
    }
}

OPP_STATIC ushort getmaterial(cube &c)
{
    if(c.children)
    {
        ushort mat = getmaterial(c.children[7]);
        loopi(7) if(mat != getmaterial(c.children[i])) return MAT_AIR;
        return mat;
    }
    return c.material;
}

OPP_STATIC void setmat(cube &c, ushort mat, ushort matmask, ushort filtermat, ushort filtermask, int filtergeom)
{
    if(c.children) loopi(8) setmat(c.children[i], mat, matmask, filtermat, filtermask, filtergeom);
    else if((c.material&filtermask) == filtermat)
    {
        switch(filtergeom)
        {
            case 1: if(isempty(c)) break; return;
            case 2: if(!isempty(c)) break; return;
            case 3: if(isentirelysolid(c)) break; return;
            case 4: if(!isentirelysolid(c)) break; return;
        }
        if(mat!=MAT_AIR) { c.material &= matmask; c.material |= mat; }
        else c.material = MAT_AIR;
    }
}

OPP_STATIC int probe_vo(const cube &c, int orient)
{
    loopi(2)
    {
        int a = faceedgesidx[orient][i*2 + 0];
        int b = faceedgesidx[orient][i*2 + 1];
        loopj(2)
        {
            if(crushededge(c.edges[a],j) && crushededge(c.edges[b],j) && touchingface(c, orient))
                return ((a>>2)<<1) + j;
        }
    }
    return orient;
}

OPP_STATIC void edittexcube(cube &c, int tex, int orient, bool &findrep)
{
    if(orient<0) loopi(6) c.texture[i] = tex;
    else { int i = probe_vo(c, orient); c.texture[i] = tex; }
    if(c.children) loopi(8) edittexcube(c.children[i], tex, orient, findrep);
}

OPP_STATIC void replacetexcube(cube &c, int oldtex, int newtex)
{
    loopi(6) if(c.texture[i] == oldtex) c.texture[i] = newtex;
    if(c.children) loopi(8) replacetexcube(c.children[i], oldtex, newtex);
}

OPP_STATIC uint dflip(uint face) { return face==F_EMPTY ? face : 0x88888888 - (((face&0xF0F0F0F0)>>4) | ((face&0x0F0F0F0F)<<4)); }
OPP_STATIC uint cflip(uint face) { return ((face&0xFF00FF00)>>8) | ((face&0x00FF00FF)<<8); }
OPP_STATIC uint rflip(uint face) { return ((face&0xFFFF0000)>>16)| ((face&0x0000FFFF)<<16); }
OPP_STATIC uint mflip(uint face) { return (face&0xFF0000FF) | ((face&0x00FF0000)>>8) | ((face&0x0000FF00)<<8); }

OPP_STATIC void flipcube(cube &c, int d)
{
    swap(c.texture[d*2], c.texture[d*2+1]);
    c.faces[D[d]] = dflip(c.faces[D[d]]);
    c.faces[C[d]] = cflip(c.faces[C[d]]);
    c.faces[R[d]] = rflip(c.faces[R[d]]);
    if(c.children)
    {
        loopi(8) if(i&octadim(d)) swap(c.children[i], c.children[i-octadim(d)]);
        loopi(8) flipcube(c.children[i], d);
    }
}

OPP_STATIC void rotatequad(cube &a, cube &b, cube &c, cube &d) { cube t = a; a = b; b = c; c = d; d = t; }

OPP_STATIC void rotatecube(cube &c, int d)
{
    c.faces[D[d]] = cflip (mflip(c.faces[D[d]]));
    c.faces[C[d]] = dflip (mflip(c.faces[C[d]]));
    c.faces[R[d]] = rflip (mflip(c.faces[R[d]]));
    swap(c.faces[R[d]], c.faces[C[d]]);
    swap(c.texture[2*R[d]], c.texture[2*C[d]+1]);
    swap(c.texture[2*C[d]], c.texture[2*R[d]+1]);
    swap(c.texture[2*C[d]], c.texture[2*C[d]+1]);
    if(c.children)
    {
        int row = octadim(R[d]);
        int col = octadim(C[d]);
        for(int i=0; i<=octadim(d); i+=octadim(d)) rotatequad
        ( c.children[i+row], c.children[i], c.children[i+col], c.children[i+col+row] );
        loopi(8) rotatecube(c.children[i], d);
    }
}

// --- probe entry points (unused-in-engine functions kept reachable) ---
void probe_edittex_all(cube &c, int tex)    { bool fr=false; edittexcube(c, tex, -1, fr); }
void probe_edittex_orient(cube &c, int tex, int o) { bool fr=false; edittexcube(c, tex, o, fr); }
void probe_replace(cube &c, int o, int n)   { replacetexcube(c, o, n); }
void probe_setmat(cube &c, ushort mat)      { setmat(c, mat, 0, 0, 0, 0); }
void probe_flip(cube &c, int d)             { flipcube(c, d); }
void probe_rotate(cube &c, int d)           { rotatecube(c, d); }

OPP_STATIC bool crushededge(uchar e, int dc) { return dc ? e==0 : e==0x88; }

static void vget(cube &c, int i, ivec &p)
{
    p.x = edgeget(cubeedge(c, 0, (i>>R[0])&1, (i>>C[0])&1), (i>>D[0])&1);
    p.y = edgeget(cubeedge(c, 1, (i>>R[1])&1, (i>>C[1])&1), (i>>D[1])&1);
    p.z = edgeget(cubeedge(c, 2, (i>>R[2])&1, (i>>C[2])&1), (i>>D[2])&1);
}
static void vset(cube &c, int i, const ivec &p)
{
    edgeset(cubeedge(c, 0, (i>>R[0])&1, (i>>C[0])&1), (i>>D[0])&1, p.x);
    edgeset(cubeedge(c, 1, (i>>R[1])&1, (i>>C[1])&1), (i>>D[1])&1, p.y);
    edgeset(cubeedge(c, 2, (i>>R[2])&1, (i>>C[2])&1), (i>>D[2])&1, p.z);
}
static void probe_forcemip_impl(cube &c, bool fixtex)
{
    cube *ch = c.children;
    emptyfaces(c);
    loopi(8) loopj(8)
    {
        int n = i^(j==3 ? 4 : (j==4 ? 3 : j));
        if(!isempty(ch[n]))
        {
            ivec v;
            vget(ch[n], i, v);
            vset(c, i, ivec(n, v, 8).shr(1));
            break;
        }
    }
    if(fixtex) loopj(6) c.texture[j] = getmippedtexture(c, j);
}
void probe_forcemip(cube &c, bool fixtex) { probe_forcemip_impl(c, fixtex); }

// Cursor geometry (geom.cpp rayboxintersect + octaedit.cpp normalizelookupcube).
bool probe_raybox(const vec &b, const vec &s, const vec &o, const vec &ray, float &dist, int &orient)
{
    loop(d, 3) if(ray[d])
    {
        int dc = ray[d]<0 ? 1 : 0;
        float pdist = (b[d]+s[d]*dc - o[d]) / ray[d];
        vec v(ray);
        v.mul(pdist).add(o);
        if(v[R[d]] >= b[R[d]] && v[R[d]] <= b[R[d]]+s[R[d]]
        && v[C[d]] >= b[C[d]] && v[C[d]] <= b[C[d]]+s[C[d]])
        {
            dist = pdist;
            orient = 2*d+dc;
            return true;
        }
    }
    return false;
}

// normalizelookupcube, made pure: returns the normalized (lu, lusize).
void probe_normalize(ivec &lu, int &lusize, const ivec &o, int gridsize)
{
    if(lusize>gridsize)
    {
        lu.x += (o.x-lu.x)/gridsize*gridsize;
        lu.y += (o.y-lu.y)/gridsize*gridsize;
        lu.z += (o.z-lu.z)/gridsize*gridsize;
    }
    else if(gridsize>lusize)
    {
        lu.x &= ~(gridsize-1);
        lu.y &= ~(gridsize-1);
        lu.z &= ~(gridsize-1);
    }
    lusize = gridsize;
}

// mpeditface modes 0/2 entry points.
void probe_linkedpush(cube &c, int d, int x, int y, int dc, int dir) { linkedpush(c, d, x, y, dc, dir); }
void probe_pushedge(cube &c, int d, int x, int y, int dir, int dc)
{
    uchar e = cubeedge(c, d, x, y);
    pushedge(e, dir, dc);
    c.edges[d*4 + y*2 + x] = e;
}

// world.cpp:1277 splitocta + a familysize/depth summary for the probe.
void probe_splitocta(cube *c, int size)
{
    if(size <= 0x1000) return;
    loopi(8)
    {
        if(!c[i].children) c[i].children = newcubes(isempty(c[i]) ? F_EMPTY : F_SOLID);
        probe_splitocta(c[i].children, size>>1);
    }
}
static int probe_familysize(const cube &c)
{
    int n = 1;
    if(c.children) loopi(8) n += probe_familysize(c.children[i]);
    return n;
}
static int probe_maxleaf(const cube &c, int size)
{
    if(!c.children) return size;
    int m = 0; loopi(8) m = max(m, probe_maxleaf(c.children[i], size>>1));
    return m;
}
void probe_emptymap_summary(int scale, int &nodes, int &maxLeaf)
{
    int worldsize = 1<<scale;
    cube *root = newcubes(F_EMPTY);
    loopi(4) solidfaces(root[i]);
    if(worldsize > 0x1000) probe_splitocta(root, worldsize>>1);
    nodes = 0; loopi(8) nodes += probe_familysize(root[i]);
    maxLeaf = 0; loopi(8) maxLeaf = max(maxLeaf, probe_maxleaf(root[i], worldsize>>1));
}

// mprotate / mpflip need sel/lookupcube/changed/makeundo. Reimplement the cube-level
// effect faithfully against a hand-built block, without the engine's globals:
// selcube(x,y,z) maps a block grid cell to a cube in an array we own.
// We test the *cube permutation + face rotation* on a 2x2x1 selection.
