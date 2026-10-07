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
| `../patches/a2b-input-trace.patch` | Adds the `SAUER_INPUTTRACE` input/timing logger (`mousemove` + main loop + `physicsframe`). Applied after `a2-trace.patch` (filename order). |
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

## Input / timing trace (`SAUER_INPUTTRACE`)

Captures the **layer-3 (timing) and layer-4 (input) behaviour** the velocity
model sits on top of — how the original quantises the variable frame time into
fixed physics steps, and how it applies mouse look. Inert unless
`SAUER_INPUTTRACE` is set (a separate path from `SAUER_TRACE`):

```sh
SAUER_INPUTTRACE=/tmp/sauer-input.csv \
  /abs/path/to/tools/refharness/instrumented/build/sauer_client -q"$HOME/.sauerbraten"
```

One CSV, tagged by `kind` (all values are written as `%.4f`):

```
kind,seq,a,b,c,d,e,f
I <seq> <dx> <dy> <curtime> <cursens> <yaw> <pitch>     # one per mousemove()
T <seq> <elapsedtime> <scaledtime> <curtime> <timeerr>  # one per rendered frame
P <seq> <diff> <physframetime> <physsteps>              # one per physicsframe()
```

- **`I`** — the **whole frame's** `SDL_MOUSEMOTION` delta is summed
  (`checkmousemotion`) and applied **once per rendered frame** (`mousemove`), not
  once per physics step: `yaw += dx*cursens`, `pitch -= dy*cursens`,
  `cursens = (sensitivity[+accel])/ (33*sensitivityscale)`. `curtime` here lets
  `mouseaccel` be reconstructed. Every physics step in that frame then reads the
  **same** yaw/pitch.
- **`T`** — main-loop timeline (`engine/main.cpp`): `curtime = scaletime(elapsedtime)/100`
  (ms; `scaletime(t) = t*gamespeed`, `gamespeed=100` normally, so `curtime ==
  elapsedtime` and `timeerr` is always 0 while unscaled).
- **`P`** — physics scheduler (`physicsframe`): `physframetime =
  clamp(scaletime(5)/100, 1, 5)` (always 5 ms unscaled) and `physsteps =
  ceil(diff/physframetime)`. **The variable timestep is absorbed by the number
  of fixed 5 ms steps, not by the step size.**

Measured on a real run (church51, coop edit, 200 fps limiter): `curtime` jitters
`5..28` ms, `physframetime` is always `5`, and `physsteps` is `1` most frames but
`2` (and occasionally `3`–`4`) whenever `curtime > 5`. So a faithful port must run
**fixed 5 ms steps** and catch up by step count — never feed a variable `delta`
into `moveplayer`.

## Clipplane dump (`SAUER_TRACE.clips`)

For A3 collision debugging (`docs/movement-verification.md`). Every deformed
(`Normal`) cube the player's collision touches is dumped **once** (the engine
caches `genclipplanes` per cube), together with its edge bytes and per-face
classification, so the port's `genclipplanes`/`classifyface`/`visibletris` can be
diffed against the engine's exact output:

```
C <step> <time> <co.x> <co.y> <co.z> <size> <visible> <nplanes> <o.x> <o.y> <o.z> <r.x> <r.y> <r.z> <12 edge bytes hex> <material> <c.visible>
F <orient> <classifyface> <collideface> <nsize> <isSelf> <ro.x> <ro.y> <ro.z> <12 neighbour edge bytes hex> <neighbour has children>
P <i> <nx> <ny> <nz> <offset> <side>
```

- `C` = cube header: origin/size, `p.visible`, `p.size`, the tight bbox `p.o`/`p.r`,
  the cube's 12 edge bytes, its `material`, and the **stored** `c.visible`.
- `F` = one line per orientation: `classifyface(c, orient, co, size)`,
  `collideface(c, orient)`, the `neighbourcube` result (`nsize`, whether it
  returned the same cube, the neighbour origin `ro`), the neighbour's 12 edge
  bytes, and whether the neighbour node has children (which selects the
  `classifyface` branch).
- `P` = one line per collision plane (including its `side`, i.e. orientation).

> Captured traces live in `traces/`. The whole `tools/` tree is excluded from
> Godot's importer via `tools/.gdignore`, so these `.csv` files are not imported
> as translations.
