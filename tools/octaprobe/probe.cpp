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
    return 0;
}
