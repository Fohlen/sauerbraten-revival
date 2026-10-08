# Raycast ground-truth probe

Dumps results from the **original** `physics.cpp:raycube` (r6889) so the C#
port (`World/Physics/OctaRaycast.cs`) can be asserted against the engine's
exact output — not against hand-derived assumptions about the octree DDA
macros (`INITRAYCUBE`, `CHECKINSIDEWORLD`, `DOWNOCTREE`, `FINDCLOSEST`,
`UPOCTREE`) or the deformed-cube clipping-plane intersection, which are easy to
get subtly wrong.

This is the raycast analogue of the octree probe
([`tools/octaprobe`](../octaprobe)) and the A1 movement harness
([`tools/refharness`](../refharness)): a small C++ driver linked against the
original source, used offline to generate golden values.

## What it covers

- `raycube` / `raycubeintersect` / `getclipplanes` — copied **verbatim** from
  `physics.cpp` (they are file-local `static` in the engine, exactly like
  `octaprobe/ops.cpp` copies the `octaedit.cpp` statics). Links `octa.cpp` for
  `genclipplanes`/`visibletris`/`calcvert`.
- The octree DDA: `CHECKINSIDEWORLD` (outside origin), leaf descent +
  `FINDCLOSEST`/`UPOCTREE` (hierarchical step through empty space).
- The three hit paths: the **quick hit** (entirely-solid, `RAY_EDITMAT`,
  `RAY_CLIPMAT`, `lsize==size`), the **deformed-cube clip-plane hit**
  (`INTERSECTPLANES`/`INTERSECTBOX`), and the editor's `rayent` wrapper.
- `RAY_SKIPFIRST` (the editor always uses it) and the editor's exact mode/size
  (`RAY_SKIPFIRST`, `size = gridsize`).

Entities are stubbed out (`disttoent`/`disttooutsideent` return `radius`): the
MVP has no entity octree (`ext->ents`), so the `RAY_ENTS` branch is out of scope.

## Build & run

```sh
./build.sh
./rayprobe
```

## Golden values (r6889)

| scenario | dist | orient | hit surface |
| --- | --- | --- | --- |
| `solid-above` | 4.1000 | −1 | (0,0,0) |
| `cavity-far` | 4.1000 | 0 (−X) | (−1,0,0) |
| `cavity-near` | 4.1000 | 1 (+X) | (1,0,0) |
| `cavity-up` | 4.1000 | 4 (−Z) | (0,0,−1) |
| `deformed-top` | 12.1000 | 5 (+Z) | (0,0,1) |
| `deformed-side` | 5.1000 | 0 (−X) | (−1,0,0) |
| `deformed-slope` | 4.1000 | −1 | (0.577350,−0.577350,−0.577350) |
| `skipfirst-solid` | 0.1000 | 5 (+Z) | (0,0,1) |
| `editmat-water` | 5.1000 | −1 | (0,0,0) |
| `clipmat-glass` | 5.1000 | −1 | (0,0,0) |
| `editor-cavity` (rayent) | 4.1000 | −1 | (−1,0,0) |
| `editor-skipfirst` (rayent) | 4.1000 | −1 | (1,0,0) |

Notes:

- `orient` is derived from `hitsurface` only when it is axis-aligned; a
  deformed-face plane normal (e.g. `deformed-slope`) has no single axis, so the
  raw vector is the golden value and `orient` is reported as −1.
- `deformed-side` hits via the **AABB** entry path, not a plane: its −X face is
  at the world boundary, so `visibletris(showoutside=false)` emits no plane and
  the ray stops at the tight bounding box. `deformed-slope` hits via a real
  deformed-face triangle plane (normal ≈ (1,−1,−1)/√3). Together they lock both
  branches of `raycubeintersect`.
- `solid-above` and the two material hits return `dist` without setting
  `hitsurface` (the `closest == -1` quick-hit path) — that is faithful to the
  original, which leaves the global `hitsurface` untouched there.
