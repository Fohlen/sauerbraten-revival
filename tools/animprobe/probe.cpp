// animprobe — dumps ground truth for the MD5 skeletal-animation loader so the C#
// port (World/Entities/Md5Anim.cs) can be verified against the original engine's
// exact output rather than reasoned about.
//
// The frame-composition logic below is a VERBATIM copy of the relevant blocks of
// the engine's md5.h (r6889): the `joints {}` mesh bind-pose load, the
// `baseframe`/`hierarchy`/`frame` animation load, and the per-joint local-pose
// compose (`dq * invbase`, then `parent.base * dq`). Nothing here is our own
// logic — it is copied so the ground truth stays faithful. quat/dualquat/vec come
// straight from shared/geom.h.
//
// Uses the engine's own `vector`/`string` (from tools.h) — like octaprobe — to
// avoid libc++ clashes with tools.h's placement operator delete.
//
// Build: ./build.sh   Run: ./animprobe
// Requires the instrumented reference source (tools/refharness/instrumented/build).
#include "engine/engine.h"
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <cctype>

struct Pmd5joint { vec pos; quat orient; };
struct Pmd5hierarchy { int parent, flags, start, bone; };
struct PBone { int parent; dualquat base, invbase; };

struct Mesh {
    int numbones;
    vector<char *> names;
    vector<PBone> bones;
};

// Parse a possibly-quoted joint name out of a hierarchy/joints line (mirrors the
// loop in md5.h), returning the remainder of the line after the name token.
static void readname(const char *line, char *name, size_t n, const char **rest)
{
    const char *c = line;
    while (*c && isspace((unsigned char)*c)) c++;
    bool allowspace = false;
    if (*c == '"') { c++; allowspace = true; }
    size_t k = 0;
    while (*c && k < n - 1)
    {
        char ch = *c++;
        if (ch == '"') break;
        if (isspace((unsigned char)ch) && !allowspace) break;
        name[k++] = ch;
    }
    name[k] = '\0';
    if (rest) *rest = c;
}

// Port of md5::md5meshgroup::loadmesh's `joints {}` block (mesh bind pose).
static bool loadMeshJoints(const char *path, Mesh &mesh)
{
    FILE *f = fopen(path, "r");
    if (!f) { fprintf(stderr, "animprobe: cannot open %s\n", path); return false; }
    char buf[512];
    vector<Pmd5joint> basejoints;
    int numbones = 0;
    bool loaded = false;
    while (fgets(buf, sizeof(buf), f))
    {
        int tmp;
        if (sscanf(buf, " numJoints %d", &tmp) == 1 && numbones == 0) numbones = tmp;
        else if (strstr(buf, "joints {"))
        {
            while (fgets(buf, sizeof(buf), f) && buf[0] != '}')
            {
                char name[128]; const char *rest;
                readname(buf, name, sizeof(name), &rest);
                int parent; Pmd5joint j;
                if (sscanf(rest, " %d ( %f %f %f ) ( %f %f %f )",
                           &parent, &j.pos.x, &j.pos.y, &j.pos.z,
                           &j.orient.x, &j.orient.y, &j.orient.z) == 7)
                {
                    // verbatim sign handling from md5.h joints block
                    j.pos.y = -j.pos.y;
                    j.orient.x = -j.orient.x;
                    j.orient.z = -j.orient.z;
                    j.orient.restorew();
                    char *nm = new char[strlen(name) + 1];
                    strcpy(nm, name);
                    mesh.names.add(nm);
                    PBone b; b.parent = parent;
                    mesh.bones.add(b);
                    basejoints.add(j);
                }
            }
            loaded = true;
        }
    }
    fclose(f);
    if (!loaded) return false;

    mesh.numbones = numbones;
    // Post-load bind-pose assignment (verbatim from md5.h).
    loopv(basejoints)
    {
        PBone &b = mesh.bones[i];
        b.base = dualquat(basejoints[i].orient, basejoints[i].pos);
        (b.invbase = b.base).invert();
    }
    return basejoints.length() == numbones;
}

static void stripetoken(const char *tok, char *out, size_t n)
{
    size_t len = strlen(tok);
    if (len >= 2 && tok[0] == '"' && tok[len-1] == '"')
    {
        size_t k = len - 2;
        if (k > n - 1) k = n - 1;
        memcpy(out, tok + 1, k);
        out[k] = '\0';
    }
    else
    {
        strncpy(out, tok, n);
        out[n - 1] = '\0';
    }
}

// Port of md5::loadanim()'s parse + frame compose for one frame.
static bool dumpFrame(const char *path, Mesh &mesh, int wantFrame)
{
    FILE *f = fopen(path, "r");
    if (!f) { fprintf(stderr, "animprobe: cannot open %s\n", path); return false; }

    vector<Pmd5hierarchy> hierarchy;
    vector<Pmd5joint> basejoints;
    int animdatalen = 0, animframes = 0;
    float *animdata = NULL;
    char buf[512];
    bool done = false;
    while (!done && fgets(buf, sizeof(buf), f))
    {
        int tmp;
        if (sscanf(buf, " MD5Version %d", &tmp) == 1) { }
        else if (sscanf(buf, " numJoints %d", &tmp) == 1) { }
        else if (sscanf(buf, " numFrames %d", &animframes) == 1) { }
        else if (sscanf(buf, " frameRate %d", &tmp) == 1) { }
        else if (sscanf(buf, " numAnimatedComponents %d", &animdatalen) == 1)
        {
            if (animdatalen > 0) animdata = new float[animdatalen];
        }
        else if (strstr(buf, "bounds {"))
        {
            while (fgets(buf, sizeof(buf), f) && buf[0] != '}');
        }
        else if (strstr(buf, "hierarchy {"))
        {
            while (fgets(buf, sizeof(buf), f) && buf[0] != '}')
            {
                char rawname[128]; Pmd5hierarchy h;
                if (sscanf(buf, " %100s %d %d %d", rawname, &h.parent, &h.flags, &h.start) == 4)
                {
                    char name[128];
                    stripetoken(rawname, name, sizeof(name));
                    int k = hierarchy.length();
                    h.bone = k;
                    // remap to the mesh bone with the same name (md5.h behaviour)
                    if (k < mesh.names.length() && strcmp(mesh.names[k], name))
                        loopi(mesh.numbones) if (!strcmp(mesh.names[i], name)) { h.bone = i; break; }
                    hierarchy.add(h);
                }
            }
        }
        else if (strstr(buf, "baseframe {"))
        {
            while (fgets(buf, sizeof(buf), f) && buf[0] != '}')
            {
                Pmd5joint j;
                if (sscanf(buf, " ( %f %f %f ) ( %f %f %f )",
                           &j.pos.x, &j.pos.y, &j.pos.z,
                           &j.orient.x, &j.orient.y, &j.orient.z) == 6)
                {
                    j.pos.y = -j.pos.y;
                    j.orient.x = -j.orient.x;
                    j.orient.z = -j.orient.z;
                    j.orient.restorew();
                    basejoints.add(j);
                }
            }
        }
        else if (sscanf(buf, " frame %d", &tmp) == 1)
        {
            int numdata = 0;
            while (fgets(buf, sizeof(buf), f) && buf[0] != '}')
            {
                char *src = buf, *next = src;
                for (; numdata < animdatalen; numdata++, src = next)
                {
                    animdata[numdata] = (float)strtod(src, &next);
                    if (next <= src) break;
                }
            }
            if (tmp != wantFrame) continue;

            dualquat *frame = new dualquat[mesh.numbones];
            loopv(basejoints)
            {
                Pmd5hierarchy &h = hierarchy[i];
                Pmd5joint j = basejoints[i];
                if (h.start < animdatalen && h.flags)
                {
                    float *jdata = &animdata[h.start];
                    if (h.flags & 1) j.pos.x = *jdata++;
                    if (h.flags & 2) j.pos.y = -*jdata++;
                    if (h.flags & 4) j.pos.z = *jdata++;
                    if (h.flags & 8) j.orient.x = -*jdata++;
                    if (h.flags & 16) j.orient.y = *jdata++;
                    if (h.flags & 32) j.orient.z = -*jdata++;
                    j.orient.restorew();
                }
                dualquat dq(j.orient, j.pos);
                int k = h.bone;
                PBone &b = mesh.bones[k];
                dq.mul(b.invbase);
                dualquat &dst = frame[k];
                if (b.parent < 0) dst = dq;
                else dst.mul(mesh.bones[b.parent].base, dq);
                dst.fixantipodal(mesh.bones[k].base);
            }

            loopi(mesh.numbones)
            {
                const dualquat &d = frame[i];
                quat rc = quat(d.real); rc.invert();
                quat t; t.mul(d.dual, rc); t.mul(2.0f);
                printf("BONE %d %d %s %.6f %.6f %.6f %.6f %.6f %.6f %.6f\n",
                       wantFrame, i, mesh.names[i],
                       t.x, t.y, t.z, d.real.x, d.real.y, d.real.z, d.real.w);
            }
            delete[] frame;
            done = true;
        }
    }
    fclose(f);
    if (animdata) delete[] animdata;
    return done;
}

int main(int argc, char **argv)
{
    const char *model = argc > 1 ? argv[1] : "mrfixit";
    const char *anim = argc > 2 ? argv[2] : "idle";
    int frame = argc > 3 ? atoi(argv[3]) : 0;

    const char *ref = getenv("SAUER_REF");
    if (!ref) ref = "/Users/fohlen/Downloads/sauerbraten-code-r6889";
    char meshpath[1024], animpath[1024];
    snprintf(meshpath, sizeof(meshpath), "%s/packages/models/%s/%s.md5mesh", ref, model, model);
    snprintf(animpath, sizeof(animpath), "%s/packages/models/%s/%s.md5anim", ref, model, anim);

    Mesh mesh;
    if (!loadMeshJoints(meshpath, mesh)) { fprintf(stderr, "animprobe: mesh load failed\n"); return 1; }
    printf("# model=%s anim=%s frame=%d numJoints=%d\n", model, anim, frame, mesh.numbones);

    if (!dumpFrame(animpath, mesh, frame)) { fprintf(stderr, "animprobe: anim load failed\n"); return 1; }
    return 0;
}
