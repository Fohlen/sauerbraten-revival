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
