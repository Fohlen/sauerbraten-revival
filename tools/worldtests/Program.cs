using System;
using Godot;
using SauerbratenRevival.World;

int failures = 0;

void Check(bool cond, string name)
{
    Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
    if (!cond) failures++;
}

// octa.h: OctaStep
Check(Octa.OctaStep(0, 0, 0, 0) == 0, "OctaStep origin");
Check(Octa.OctaStep(1, 0, 0, 0) == 1, "OctaStep +x");
Check(Octa.OctaStep(0, 1, 0, 0) == 2, "OctaStep +y");
Check(Octa.OctaStep(0, 0, 1, 0) == 4, "OctaStep +z");
Check(Octa.OctaStep(1, 1, 1, 0) == 7, "OctaStep +++");

// octa.h: faces
var c = new Cube();
c.SolidFaces();
Check(c.IsEntirelySolid(), "SolidFaces -> IsEntirelySolid");
Check(!c.IsEmpty(), "solid is not empty");
c.EmptyFaces();
Check(c.IsEmpty(), "EmptyFaces -> IsEmpty");

// octa.h: edge helpers
byte e = Cube.EdgeMake(3, 7);
Check(Cube.EdgeGet(e, 0) == 3 && Cube.EdgeGet(e, 1) == 7, "EdgeMake/EdgeGet");
byte ed = 0;
Cube.EdgeSet(ref ed, 0, 5);
Check(Cube.EdgeGet(ed, 0) == 5, "EdgeSet low nibble");
Cube.EdgeSet(ref ed, 1, 9);
Check(Cube.EdgeGet(ed, 1) == 9, "EdgeSet high nibble");

// octa.h: CubeEdge byte view of faces
c.SetCubeEdge(0, 1, 1, 0xAB);
Check(c.CubeEdge(0, 1, 1) == 0xAB, "CubeEdge/SetCubeEdge roundtrip");

// octa.cpp: world allocation + lookup
var w = new Octa(3);
Check(w.WorldSize == 8, "WorldSize == 8 at scale 3");
Check(w.AllocNodes == 1, "NewMap allocates one node");
Check(w.WorldRoot.Length == 8, "root node has 8 cubes");
Check(w.WorldRoot[0].IsEntirelySolid(), "root cube is solid");
Check(w.WorldRoot[0].Texture[0] == Octa.DEFAULT_GEOM, "default geom texture");
Check(w.LookupMaterial(new Vector3(1, 1, 1)) == Octa.MAT_AIR, "LookupMaterial -> MAT_AIR");
Check(!w.InsideWorld(new Vector3I(-1, 0, 0)), "outside world (-1,0,0)");
Check(w.InsideWorld(new Vector3I(7, 7, 7)), "inside world (7,7,7)");
Check(w.LeafCube(new Vector3I(7, 7, 7)) is not null, "LeafCube inside world");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
