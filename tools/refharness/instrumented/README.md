# A2 — Instrumented Client (movement capture)

Builds a **patched copy** of the original Sauerbraten client that logs the local
player's full-precision per-5 ms state and inputs to CSV, so the port can be
diffed against real gameplay. See
[`docs/movement-verification.md`](../../../docs/movement-verification.md).

The reference dump is **never modified**: `build_instrumented.sh` copies its
`src/` into an ignored `instrumented/build/` directory, applies the patches, and
builds there. Delete `instrumented/build/` to clean up.

## Patches

| Patch | Purpose |
| --- | --- |
| `../patches/a2-trace.patch` | Adds the `SAUER_TRACE` CSV logger to `engine/physics.cpp`. |
| `../patches/macos-arm64.patch` | Builds native arm64 against Homebrew SDL2 (the Makefile otherwise forces x86_64, which cannot run here). |
| `../patches/ogz-metadata.patch` | Adds the `SAUER_METADATA` JSON exporter on map load (see [`../patches/README.md`](../patches/README.md)). |

## Prerequisites (macOS / arm64)

```sh
brew install sdl2 sdl2_image sdl2_mixer
```

## Build

```sh
tools/refharness/build_instrumented.sh
# optional: SAUER_REF=/path/to/dump tools/refharness/build_instrumented.sh
```

## Capture a trace

Run from the **reference root** so `data/` and `packages/` resolve, with the
trace path in `SAUER_TRACE`:

```sh
cd "$SAUER_REF"
SAUER_TRACE=/tmp/sauer-trace.csv \
  /abs/path/to/tools/refharness/instrumented/build/sauer_client -q"$HOME/.sauerbraten"
```

Then: start a local game / load a map, move (walk, strafe, jump, ramps, stairs),
and quit. The CSV is written to `SAUER_TRACE`.

> The logger records every `ENT_PLAYER` physics step, i.e. the local player at
> 200 Hz. Bots/monsters are `ENT_AI` and are excluded.

## Trace format

```
step,time,move,strafe,jumping,yaw,pitch,ox,oy,oz,vx,vy,vz,fx,fy,fz,physstate,timeinair,floorx,floory,floorz
```

- `move`/`strafe`/`jumping`: inputs at that step.
- `yaw`/`pitch`: absolute view angles (deltas are recoverable).
- `o`/`vel`/`falling`: position and the two velocity accumulators.
- `physstate`: `0..7` (`PHYS_*`).
- `floor`: floor normal.

These are the **golden inputs + outputs** for A3 input-replay differential testing.

## Events file (`SAUER_TRACE.events`)

External movement effects are logged separately (same path + `.events`) so A3 can
resync instead of diverging:

```
step,time,event
<step>,<time>,map,<mapname>
<step>,<time>,jumppad,<entity>,<vx>,<vy>,<vz>
<step>,<time>,teleport,<entity>,<x>,<y>,<z>
```

`step`/`time` use the same clock as the step trace, so events line up exactly.

## Clipplane dump (`SAUER_TRACE.clips`)

For A3 collision debugging (`docs/movement-verification.md`). Every deformed
(`Normal`) cube the player's collision touches is dumped **once** (the engine
caches `genclipplanes` per cube), together with its edge bytes and per-face
classification, so the port's `genclipplanes`/`classifyface`/`visibletris` can be
diffed against the engine's exact output:

```
C <step> <time> <co.x> <co.y> <co.z> <size> <visible> <nplanes> <o.x> <o.y> <o.z> <r.x> <r.y> <r.z> <12 edge bytes hex> <material>
F <orient> <classifyface> <collideface> <nsize> <isSelf> <ro.x> <ro.y> <ro.z> <12 neighbour edge bytes hex>
P <i> <nx> <ny> <nz> <offset> <side>
```

- `C` = cube header: origin/size, `p.visible`, `p.size`, the tight bbox `p.o`/`p.r`,
  the cube's 12 edge bytes, and its `material`.
- `F` = one line per orientation: `classifyface(c, orient, co, size)`,
  `collideface(c, orient)`, the `neighbourcube` result (`nsize`, whether it
  returned the same cube, the neighbour origin `ro`), and the neighbour's 12 edge
  bytes.
- `P` = one line per collision plane.

> Captured traces live in `traces/`. The whole `tools/` tree is excluded from
> Godot's importer via `tools/.gdignore`, so these `.csv` files are not imported
> as translations.
