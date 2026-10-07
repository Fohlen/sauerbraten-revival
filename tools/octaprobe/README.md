# Octree ground-truth probe

Dumps results from the **original** `octa.cpp` (r6889) so the C# port
([`World/OctaEdit.cs`](../../World/OctaEdit.cs)) can be asserted against the
engine's exact output — not against hand-derived assumptions about the octree
edge macros (`cubeedge`, `edgeset`, `octaindex`, `octastep`), which are easy to
get subtly wrong.

This is the octree analogue of the A1 movement harness
([`tools/refharness`](../refharness)): a small C++ driver linked against the
original source, used offline to generate golden values.

## What it covers

- `lookupcube(to, tsize, ro, rsize)` — leaf resolution and origin/size masking.
- `subdividecube(c)` on a **deformed** cube (`edges[0] = 4`) — the exact child
  edge bytes, including the `validatec` pass that empties degenerate children.
- `blockcube` coordinate mapping (the `octaedit.cpp` orientation math) for
  representative block cells.
- Op outcomes: `mpdelcube` (delete empties the cube, keeping its texture) and the
  `mpeditface` mode-1 fill/carve branch (including the selection-origin advance).
- Face/texture/material ops (`edittexcube`, `replacetexcube`, `setmat`) and
  `flipcube`/`rotatecube`, `forcemip`. These live in `ops.cpp`, which copies the
  original `octaedit.cpp` functions **verbatim** (they are file-local `static` in
  the engine and not exported by any header).
- `remip` collapse decisions (consistent solid children collapse even with differing
  but invisible textures; a deformed child prevents collapse; nested levels collapse
  together). Asserted by [`test/RemipTest.cs`](../../test/RemipTest.cs).
- Cursor geometry (`rayboxintersect`, `normalizelookupcube`) for the editor cursor.
  Asserted by [`test/EditCursorTest.cs`](../../test/EditCursorTest.cs).
- Face/corner push (`pushedge`, `linkedpush`) for `mpeditface` modes 0/2. Asserted
  by [`test/OctaFaceTest.cs`](../../test/OctaFaceTest.cs).
- `emptymap` / `splitocta` (world creation). Asserted by
  [`test/CommandSystemTest.cs`](../../test/CommandSystemTest.cs).

## Build & run

```sh
./build.sh
./octaprobe
```

Output (r6889, worldscale 4):

```
LOOKUP (5,2,7) tsize=0 -> ro=(0,0,0) rsize=8 solid=1
LOOKUP (1,1,1) tsize=2 -> ro=(0,0,0) rsize=2
SUBDIV parent verts: (4,0,0) (0,0,0) (0,8,0) (8,8,0) (0,0,8) (8,0,8) (0,8,8) (8,8,8)
GT child 0: 00 00 00 00 00 00 00 00 00 00 00 00
GT child 1: 00 00 00 80 80 80 80 80 80 80 80 80
GT child 2: 84 80 80 80 80 80 80 80 80 80 80 80
GT child 3: 00 80 80 80 80 80 80 80 80 80 80 80
GT child 4: 84 80 80 80 80 80 80 80 80 80 80 80
GT child 5: 00 80 80 80 80 80 80 80 80 80 80 80
GT child 6: 80 80 80 80 80 80 80 80 80 80 80 80
GT child 7: 80 80 80 80 80 80 80 80 80 80 80 80
BLOCK orient0 cell(0,0,0) -> s=(8,8,8)
BLOCK orient0 cell(1,2,1) -> s=(16,16,24)
BLOCK orient1 cell(0,0,0) -> s=(16,8,8)
OP edittex all tex0=42 tex5=42
OP edittex orient0 tex0=7 tex1=1
OP replace old5new9 tex2=9 tex0=1
OP setmat water material=4
OP flip d0 tex0=2 tex1=1 tex2=3
OP rotate d1 tex0=5 tex1=6 tex2=3 tex3=4
OP forcemip parent faces=84848446 84848484 84848484
PUSH edge0 dir1 dc0 edges=81 80 80 80
LINK d0 x0y0 dir-1: 43 80 80 80 80 80 80 80 80 80 80 80
LINK d0 x1y1 dir-1: 44 80 80 80 80 80 80 80 80 80 80 80
OP remip collapse before=9 after=1 children=0
OP remip keep before=9 after=1 children=0
OP remip deformed before=9 after=9 children=1
OP remip nested before=17 after=1 children=0
EMPTY scale=10 worldsize=1024 nodes=8 maxLeaf=512
EMPTY scale=12 worldsize=4096 nodes=8 maxLeaf=2048
EMPTY scale=14 worldsize=16384 nodes=72 maxLeaf=4096
CUR raybox hit dist=3.4641 orient=1
CUR raybox z hit dist=12.0000 orient=5
CUR normalize g8 lu=(13,29,5) lusize=8
CUR normalize g32 lu=(0,0,0) lusize=32
CUR normalize g8b lu=(16,16,8) lusize=8
OP delcube child0 empty=1 tex0=3
OP fill child0 solid=1 tex0=9 mat=0
```

These values are asserted by [`test/OctaLookupTest.cs`](../../test/OctaLookupTest.cs).

## Notes

- The `validatec` call inside `subdividecube` (with the default `size = 0`) is
  load-bearing: it empties malformed children. A port that omits it produces
  wrong edge bytes (this cost us a debugging round — hence this probe).
- Links `octa.cpp` from the instrumented reference tree; `stubs.cpp` provides the
  engine symbols `octa.cpp` references but the probe never calls.
