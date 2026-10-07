// octaprobe — dumps ground truth from the ORIGINAL octa.cpp / octaedit.cpp so the
// C# port (World/OctaEdit.cs) can be verified against it, not reasoned about.
//
// Build: ./build.sh   Run: ./octaprobe
// Requires the instrumented reference source (tools/refharness/instrumented/build),
// which is a copy of the r6889 tree.
#include "engine/engine.h"
#include <cstdio>

// vertex i (0..7) of a cube — mirrors the static-inline helper in octa.cpp.
static void vert(cube &c, int i, ivec &p)
{
    p.x = edgeget(cubeedge(c, 0, (i>>R[0])&1, (i>>C[0])&1), (i>>D[0])&1);
    p.y = edgeget(cubeedge(c, 1, (i>>R[1])&1, (i>>C[1])&1), (i>>D[1])&1);
    p.z = edgeget(cubeedge(c, 2, (i>>R[2])&1, (i>>C[2])&1), (i>>D[2])&1);
}

static void dumpEdges(const char *tag, cube &c)
{
    printf("%s:", tag);
    for (int d = 0; d < 3; d++)
        for (int b = 0; b < 4; b++)
            printf(" %02x", (unsigned char)((c.faces[d] >> (b*8)) & 0xFF));
    printf("\n");
}

int main()
{
    worldscale = 4; worldsize = 1 << worldscale;

    // ---- lookupcube ----
    {
        ivec ro; int rsize;
        cube &c = lookupcube(ivec(5,2,7), 0, ro, rsize);
        printf("LOOKUP (5,2,7) tsize=0 -> ro=(%d,%d,%d) rsize=%d solid=%d\n",
               ro.x, ro.y, ro.z, rsize, isentirelysolid(c));
        ivec ro2; int rs2;
        lookupcube(ivec(1,1,1), 2, ro2, rs2);
        printf("LOOKUP (1,1,1) tsize=2 -> ro=(%d,%d,%d) rsize=%d\n",
               ro2.x, ro2.y, ro2.z, rs2);
    }

    // ---- subdividecube on a deformed cube (edges[0]=4) ----
    // NOTE: the lookupcube section above may have subdivided worldroot[0], so
    // rebuild a fresh world to isolate the subdivision test.
    {
        worldroot = newcubes(F_SOLID);
        cube &c = worldroot[0];
        c.edges[0] = 4;
        printf("SUBDIV parent verts:");
        for (int i = 0; i < 8; i++) { ivec v; vert(c, i, v); printf(" (%d,%d,%d)", v.x, v.y, v.z); }
        printf("\n");
        subdividecube(c, false, false);
        for (int i = 0; i < 8; i++)
        {
            char tag[32]; snprintf(tag, sizeof(tag), "GT child %d", i);
            dumpEdges(tag, c.children[i]);
        }
    }

    // ---- blockcube coordinate mapping (octaedit.cpp) ----
    {
        int dim = 0, dc = 0, grid = 8;
        ivec O(8,8,8), S(2,3,4);
        // first cell
        { ivec s(0,0,0); s[dim]=dc*(S[dim]-1)*grid; s[R[dim]]=0; s[C[dim]]=0; s.add(O);
          if(dc)s[dim]-=0; else s[dim]+=0;
          printf("BLOCK orient0 cell(0,0,0) -> s=(%d,%d,%d)\n",s.x,s.y,s.z); }
        // valid far cell (z < S[dim] == 2)
        { int x=1,y=2,z=1;
          ivec s(0,0,0); s[dim]=dc*(S[dim]-1)*grid; s[R[dim]]=x*grid; s[C[dim]]=y*grid; s.add(O);
          if(dc)s[dim]-=z*grid; else s[dim]+=z*grid;
          printf("BLOCK orient0 cell(1,2,1) -> s=(%d,%d,%d)\n",s.x,s.y,s.z); }
        // orient 1 (+X)
        dim=0; dc=1; O=ivec(8,8,8); S=ivec(2,1,1);
        { ivec s(0,0,0); s[dim]=dc*(S[dim]-1)*grid; s[R[dim]]=0; s[C[dim]]=0; s.add(O);
          if(dc)s[dim]-=0; else s[dim]+=0;
          printf("BLOCK orient1 cell(0,0,0) -> s=(%d,%d,%d)\n",s.x,s.y,s.z); }
    }

    // ---- face/texture/material/flip/rotate (ops.cpp: verbatim engine copies) ----
    extern void probe_edittex_all(cube &c, int tex);
    extern void probe_edittex_orient(cube &c, int tex, int o);
    extern void probe_replace(cube &c, int o, int n);
    extern void probe_setmat(cube &c, ushort mat);
    extern void probe_flip(cube &c, int d);
    extern void probe_rotate(cube &c, int d);
    {
        cube c; c.children=NULL; c.ext=NULL; solidfaces(c); loopl(6) c.texture[l]=1;
        probe_edittex_all(c, 42);
        printf("OP edittex all tex0=%d tex5=%d\n", c.texture[0], c.texture[5]);

        loopl(6) c.texture[l]=1;
        probe_edittex_orient(c, 7, 0);
        printf("OP edittex orient0 tex0=%d tex1=%d\n", c.texture[0], c.texture[1]);
    }
    {
        cube c; c.children=NULL; loopl(6) c.texture[l]= (l==2?5:1);
        probe_replace(c, 5, 9);
        printf("OP replace old5new9 tex2=%d tex0=%d\n", c.texture[2], c.texture[0]);
    }
    {
        cube c; c.children=NULL; solidfaces(c); c.material=MAT_AIR;
        probe_setmat(c, MAT_WATER);
        printf("OP setmat water material=%d\n", c.material);
    }
    {
        cube c; c.children=NULL; loopl(6) c.texture[l]=l+1;
        probe_flip(c, 0);
        printf("OP flip d0 tex0=%d tex1=%d tex2=%d\n", c.texture[0], c.texture[1], c.texture[2]);

        cube r; r.children=NULL; loopl(6) r.texture[l]=l+1;
        probe_rotate(r, 1);
        printf("OP rotate d1 tex0=%d tex1=%d tex2=%d tex3=%d\n", r.texture[0], r.texture[1], r.texture[2], r.texture[3]);
    }

    // ---- forcemip ----
    {
        extern void probe_forcemip(cube &c, bool fixtex);
        // A subdividing parent whose child 7 is solid with a known deformation.
        cube p; p.children=NULL; p.ext=NULL; emptyfaces(p);
        loopl(6) p.texture[l]=l;
        // Build simple children: all empty except child 7, which is solid.
        p.children = newcubes(F_EMPTY);
        solidfaces(p.children[7]);
        p.children[7].edges[0]=4; // deform child 7 vertex 0 x
        probe_forcemip(p, false);
        printf("OP forcemip parent faces=%08x %08x %08x\n", p.faces[0], p.faces[1], p.faces[2]);
    }

    // ---- op handlers on a small world ----
    // "delete cube": discardchildren + emptyfaces on a child.
    {
        worldroot = newcubes(F_SOLID);
        cube &root = worldroot[0];
        subdividecube(root, false, false);
        for (int l = 0; l < 6; l++) root.children[0].texture[l] = 3;
        discardchildren(root.children[0], true);
        emptyfaces(root.children[0]);
        printf("OP delcube child0 empty=%d tex0=%d\n",
               isempty(root.children[0]), root.children[0].texture[0]);
    }
    // "fill" (mode 1, dir<0): pull a slice solid, copying the backing cell
    // textures. Backing cell here is child index 4 (+Z).
    {
        worldroot = newcubes(F_SOLID);
        cube &root = worldroot[0];
        subdividecube(root, false, false);
        emptyfaces(root.children[4]);
        for (int l = 0; l < 6; l++) root.children[4].texture[l] = 9;
        cube &c = root.children[0];
        // getmaterial(c): common child material (MAT_AIR if none). c is a leaf here.
        ushort mat = c.material;
        discardchildren(c, true);
        c.material = mat;
        solidfaces(c);
        cube &o = root.children[4];
        loopi(6) c.texture[i] = o.children ? DEFAULT_GEOM : o.texture[i];
        printf("OP fill child0 solid=%d tex0=%d mat=%d\n",
               isentirelysolid(c), c.texture[0], c.material);
    }
    return 0;
}
