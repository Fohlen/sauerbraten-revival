# Movement Verification (Plan A)

Movement is the product. We prove fidelity with the original SVN r6889 code,
not with vibes. This document defines the full Plan A: A1–A4.

The reference source is assumed at `/Users/fohlen/Downloads/sauerbraten-code-r6889`.

## Status

| Stage | State | Where |
| --- | --- | --- |
| A1 — standalone reference harness | ✅ | `tools/refharness/` (velocity model, golden CSVs, determinism) |
| A2 — instrumented original client | ✅ | `tools/refharness/patches/a2-trace.patch` (+ `a2b-input-trace.patch`, `a2c-input-replay.patch`, `a2d-weapon-events.patch`, `a2e-move-trace.patch`) |
| A3 — input-replay differential | ✅ | `test/A3ReplayTest.cs`; **`session-clean.csv` 100.0% vel / 100.0% falling, zero misses** |
| A4 — determinism | ✅ | `tools/refharness/main.cpp` (PASS); `SAUER_REPLAY` traces bit-stable run-to-run |
| Live player on the ported physics | ✅ | `World/Ogz/MapPlayer.cs` (`scenes/player_scene.tscn`) |
| Layers 3–5 (timing, input, camera/feedback) | ◑ | timing + input captured & ported; FOV/sensitivity/accel tuning, sway, sounds remain |

### What closed the movement gap

Two things, both in the step/collision layer:

1. **The `-ffast-math` step-up fold** (see below): `trystepup()`'s smooth branch
   round-trips `d->o.z += maxstep+0.1f; … -= maxstep+0.1f`, which the reference's
   `-ffast-math` folds away. The port performed it faithfully and lost `2^-12` per
   substep, drifting up along smooth steps. Fixed in `OctaPhysics.TryStepUp`.
2. **An event-free recording.** The remaining nonzero misses on earlier traces
   were soft step/slope knife-edges where even the original does not reproduce its
   own raw capture bit-for-bit (see the seed-offset caveat). Benchmarking against
   `session-clean.csv` (pure movement, no death/pad) removes that class, leaving the
   ported velocity model + collision exact: **100.0% vel, 100.0% falling, 0.0149 u
   max position error, 3/7001 physstate sub-ULP transitions.**

### What was fixed earlier (genfacevecs)

The dominant A3 residual was a **`genfacevecs` winding bug**: the reference reuses
its solid-branch `GENFACEORIENT` macro in the non-solid branch, emitting face verts
in **reverse order for `dimcoord==0` faces** (LEFT/BACK/BOTTOM). Our port iterated
`0..3` uniformly, inverting those polygons and flipping the winding-sensitive
`insideface()` result, which corrupted `classifyface()`/`visibletris()` visibility
bits (1383/1479 `c.visible` mismatches, first divergence at `session3` step 262).
Fixed in `World/Physics/OctaVisible.cs`; residual now 12/1479 (capture artifacts).

---

## Ground rules

- **Fixed step.** Both engines must run the movement model at `PHYSFRAMETIME = 5 ms`
  (200 Hz) with `moveres` sub-stepping. No variable delta.
- **Same inputs.** A comparison is only meaningful when both sides consume the
  identical input stream.
- **Match operation order.** Reproduce the original lerp form exactly:
  `vel = d + (vel - d) * pow(...)`. Reordered math diverges over long jumps.

---

## A1 — Standalone reference harness

Extract the **byte-identical original movement functions** into a small C++
program — no octree, no engine, no rendering:

- `modifyvelocity`, `modifygravity`, jump handling (from `physics.cpp`).
- `vecfromyawpitch`.
- Minimal `vec` / `physent` shim matching `ents.h` fields and defaults.
- Stubs for `game::allowmove`, `game::physicstrigger`, `lookupmaterial`.
- A flat-floor (and optional AABB-list) ground so collision is deterministic.

It steps the model at 5 ms with a scripted input program and dumps CSV:
`t, move, strafe, jumping, yaw, pitch, o.xyz, vel.xyz, falling.xyz, physstate`.

The port replays the same program and asserts equality within float epsilon.
This isolates the velocity model and is the fastest way to catch a bad port.

**Deliverable:** `tools/refharness/` + golden CSV fixtures checked into the repo.

---

## A2 — Instrument the original client

Sauerbraten's client is authoritative for its own movement and sends its own
`N_POS` (position, `vel`, `falling`, `physstate`, yaw/pitch/roll) to the server
(`fpsgame/client.cpp:953+`). Demos therefore contain an output trace, but it is
network-quantized (`DMF`/`DVELF`) — too lossy for strict comparison.

Instead, add a small patch that logs the **full-precision** per-5 ms state plus
the inputs (`move`, `strafe`, `jumping`, yaw/pitch deltas) to CSV.

**Deliverable:** a patched (separate) original build + a capture script.

**Implemented (macOS/arm64).** `tools/refharness/build_instrumented.sh` copies the
reference `src/` into an ignored build dir, applies
`tools/refharness/patches/a2-trace.patch` (the `SAUER_TRACE` logger) and
`tools/refharness/patches/macos-arm64.patch` (native arm64 against
Homebrew SDL2 — the Makefile otherwise forces x86_64, which cannot run without
Rosetta), and builds there. **The reference dump is never modified.** See
`tools/refharness/instrumented/README.md` for the trace format and the run
command (`SAUER_TRACE=<path> sauer_client`).

**Results.** A real 26 s capture (`tools/refharness/traces/session1.csv`, 5273
steps, 23 jump takeoffs) was replayed through the documented model by
`tools/refharness/validate_trace.py`, split so collision can't pollute the test:

| Environment | Steps | Exact match | Notes |
| --- | --- | --- | --- |
| Flat floor (`floor.z == 1`) | 2686 | **100.0%** | max vel error 1e-5 |
| Free air (`PHYS_FALL`) | 2465 | **97.3%** | misses are jump pads / collisions, not model errors |

This confirms the velocity model (friction, forward/airborne multipliers, jump,
gravity) matches the original in real play. Residual air misses coincide with
external events (e.g. a jump pad at step 3143) and collision steps — which need
geometry to reproduce.

**Replay prerequisites.** The capture map is `packages/base/church51.ogz`.
The A2 patch now also logs the **map name** and external movement events
(**jump pads, teleports**) to `SAUER_TRACE.events` (`step,time,event`), so A3 can
resync at them instead of diverging (e.g. the jump pad at step 3143).
`session1.csv` predates this and must be re-captured with the current build.

---

## A3 — Input-replay differential

Patch the original so movement inputs are read from a file instead of the
keyboard/mouse. One input file then drives **both** the original and our port;
diff the state traces.

Requires the same geometry on both sides. The runtime octree now loads classic maps
in-engine (`World/Ogz/`), so A3 can replay against **real geometry**; synthetic
geometry (flat plane, ramp, staircase) stays useful for isolating the collision
primitives.

A **deterministic bot policy** (e.g. hold W, turn yaw at a constant rate, jump
every N ms) removes human variance and makes the whole comparison automated.

**Deliverable:** `tools/refharness/replay` + an input-file format shared by both.

**Implemented (stage A3.2).** `World/Physics/` ports the velocity model
(`Velocity.cs`), the octree collision + `move()` state machine (`OctaPhysics.cs`:
`collide`/`octacollide`/`fuzzycollidesolid`/`fuzzycollideplanes`, `move`,
`trystepup`, `trystepdown`, `findfloor`, `landing`, `falling`, `slideagainst`,
`switchfloor`), and the full face visibility machinery the deformed-cube collision
hull needs (`OctaVisible.cs`: `genfacevecs`/`insideface`/`occludesface`/`visibletris`/
`classifyface`; `OctaClipping.cs`: `genclipplanes`/`clampcollide` + `EntCapsule`).
`test/A3ReplayTest.cs` replays the captured traces through the port and diffs state:

| Trace | Steps | vel | falling | o | physstate |
| --- | ---: | ---: | ---: | ---: | ---: |
| `session2.csv` | 4162 | 100.0% | 100% | 99.9% | 100% |
| `session3.csv` | 7868 | 99.8% | 99.9% | 99.8% | 99.9% |
| `session-stable2.csv` | 25901 | 100.0% | 100.0% | 100.0% | 100.0% |
| `session-clean.csv` (no events) | **7001** | **100.0%** | **100.0%** | **100.0%** | **100.0%** |

**`session-clean.csv` — movement gap closed (for pure movement).** A 35 s recording
on `church51` deliberately kept free of external events (no death/respawn, no jump
pads; a single gun-recoil `kick` and one teleport at the very end, both logged).
The port replays all 7001 steps with **zero velocity misses** (max err `0.0000`),
**zero falling misses** (`0.0000`), o max err `0.0149`, and only 3 physstate
differences (sub-0.02 u soft step/slope transitions). The earlier residual was a
mix of (a) the `-ffast-math` step-up fold (fixed, see below) and (b) capture-vs-
replay knife-edges on soft lips that even the original does not reproduce bit-for-
bit against its own raw capture. Restricting the benchmark to event-free movement
removes (b) and shows the ported physics is exact for the velocity model and
collision.

External events are resynced from `*.events` (teleports, jump pads, and — since
`a2d-weapon-events.patch` — weapon recoil `kick` and impact push `hitpush`, which
also covers being shot) plus a death/respawn detector (a >40 u one-step position
jump — the engine's `game::suicide` on `MAT_DEATH`/`o.z<0`).

**Deterministic replay of the reference (`SAUER_REPLAY`).**
`tools/refharness/patches/a2c-input-replay.patch` makes the instrumented client read
an A2 trace back as **inputs** (`SAUER_REPLAY=<trace.csv>`), seeding only the row-0
state and then free-running, so the original must regenerate the trajectory from
inputs alone. Two runs are byte-identical (A4). This turns a one-time manual
recording into a regenerable, deterministic fixture and lets us ask: *can the
original reproduce its own recording?*

The replay **self-terminates** at EOF (`quit()`), so it works under a plain shell
command with no timeout (a replay runs in real time — `physicsframe` catches up per
rendered frame). `SAUER_REPLAY_NOQUIT=1` keeps it up.

On `session-stable2.csv` the free-running reference follows the recording exactly
until the **first weapon launch** (step 11990, a `kick`); without re-applying the
logged impulse the replay diverges there. Before the `a2d-weapon-events.patch`
instrumentation existed the reference diverged at the first such launch on the
earlier 12k trace (~step 5000). This confirms the movement model (jump, gravity,
collision, pads) is faithfully regenerated from inputs; only unmodelled external
impulses break it.

**The 3/7001 `session-clean` physstate differences are label-only.** All three sit on
the same 45° slope (`floor=(0.447,0,0.894)`) at a contour crossing, with **o err
0.0000 and vel err 0.0000** — the trajectory is bit-identical. Two are a single-step
`4→5→4` flip; the third a one-step `5`. `PHYS_STEP_UP` is set inside `trystepup()`
and persists because `landing()` leaves `STEP_UP`/`STEP_DOWN` untouched, so whether
the port enters it on a given 5 ms grid is a **discrete `trystepup()` success/`cansmooth`
boolean** that flips when an *intermediate* candidate point (`old + smoothdir`)
differs by ~1e-6 — a sub-ULP sensitivity, not a trajectory error. Closing it would
require bit-exact reproduction of `trystepup()`'s candidate-point collision tests
(matching the compiler's `-ffast-math` on every intermediate). Not a fidelity bug.

**Seed-offset caveat.** The replay seeds from **row 0's post-step state** and then
applies row 1's inputs, so it is one physics step ahead of a from-scratch run. On
`session-clean.csv` (no events) the reference replay is bit-stable run-to-run, but
it still drifts from the raw capture by <0.05 u from ~step 3130 on — a soft
step-down/fall transition resolved one step differently, then the sub-unit offset
propagates. So the raw capture and the engine's own replay are not identical at
knife-edge lips; the port benchmark (`rows[k-1] state + rows[k] inputs → rows[k]`)
uses the capture directly and is unaffected by this.

**Localized residual (now).** The port's first miss on `session-stable2.csv` is step
18315: a **step/ramp** case where the port keeps `ps=4` (step-up) while the engine
reports `ps=6` (step-down) / ground with an opposite-sign `vz`; a few nearby steps
show the same signature. It is a lip / step launch (`trystepup`/`cansmooth`/
`trystepdown` interplay), not a velocity-model error — the falling column stays at
100%. Earlier (pre-weapon-events) traces had the same category at their first miss
(`session-stable` step 5195: landing relaunch `vz=+17.3` while dropping horizontal
speed `(104,42)→(92,33)`).

**`-ffast-math` step-up fix.** `trystepup()`'s smooth branch does
`d->o.z += maxstep+0.1f; … d->o.z -= maxstep+0.1f;`. Under the reference's
`-ffast-math` the compiler **folds the two z adds**, so the settled z is exactly
`old + smoothdir.z`. The port performed the round-trip faithfully and lost
`2^-12 ≈ 0.00024` **per substep** in float32, drifting the player up along the
smooth ramp and flipping `collide()` at the lip. `OctaPhysics.TryStepUp` now
computes the raised z without mutating the settled z (`zadd = old.z + smoothdir.z`),
reproducing the fold. No regressions on any trace; moves the `session-stable3`
first miss from step 3035 → 3036. Also matched the reference's `rescale(1)`
(`mul(1/mag)`, not `div(mag)`) for `stairdir`/`smoothdir`, which likewise changes
rounding.

**Knife-edge caveat (3036).** Replaying `session-stable3.csv` through the **original
engine** (free-running, `SAUER_REPLAY`) diverges from the recording at step ~3030
and transitions `ps=5→4` one substep earlier than the recording shows — the
recording and the original's own replay disagree at this lip. So the port matching
the recording at 3036 is partly chance; the reproducible target is the *reference
replay's* trace, not the raw capture. Reference `move()` ground truth:
`tools/refharness/patches/a2e-move-trace.patch` (`SAUER_MOVETRACE=<path>`) dumps the
per-substep `move()`/`trystepup()` decision chain; the port's equivalent is
`OctaPhysics.MoveTrace` + `test/MoveTraceProbe.cs`.

**Clipplane ground truth (A3.3–A3.4).** The instrumented client also dumps
`SAUER_TRACE.clips`: for every deformed cube the player's collision touches, its
edge bytes, `material`, the **stored `c.visible`**, and per face the
`classifyface`/`collideface`/`neighbourcube` result (`nsize`, neighbour edges,
children) plus the generated planes. Diffing our port against it
(`tools/refharness/patches/a2-trace.patch`, `test/A3ClipsProbe.cs`, 1479 cubes):

| Check | Result |
| --- | --- |
| `neighbourcube` (`nsize`, neighbour edges, children) | **0 mismatches** |
| `genclipplanes` bbox | **0 mismatches** |
| `genclipplanes` planes | 1465/1479 (`nplanes` differs on 14) |
| `genfacevecs` / `insideface` / `collapsedface` | **exact** — verified against a standalone C++ reproduction of the reference macros |
| collision visibility mask (`c.visible`) | 12/1479 differ (was 1383) |

The mask diff was **mostly a `genfacevecs` winding bug, now fixed**. The reference
`genfacevecs()` reuses the `GENFACEORIENT` macro from its solid branch (it is not
`#undef`'d between the two branches), which emits the face verts in **reverse order**
for the `dimcoord == 0` faces (LEFT/BACK/BOTTOM). That order feeds `insideface()`,
whose winding-sensitive result decides `classifyface()`/`visibletris()` visibility
bits. Our port iterated `0..3` uniformly, so those faces got an inverted polygon,
flipping 1383/1479 mask bits and causing the first `session3` miss (step 262: ours
`trystepup`ed while the engine slid off a deformed slope). Reversing the iteration
for `dimcoord == 0` faces fixed it.

**Remaining gap.** After the `genfacevecs` fix, the first `session3` miss moved to
step 266 (a deformed-cube slope landing while falling): ours lands on the slope
(`PHYS_SLOPE`) one or two 5 ms sub-steps before the engine does. The residual is
~0.1–0.2% of steps (≈12–25 steps over a ~39 s capture), confined to sliding/landing
on deformed (non-axis-aligned) cube faces. The remaining 12 mask mismatches in the
`session7.csv.clips` probe are size-2 cubes whose recorded `c.visible` is internally
inconsistent with the reference `classifyface()` (verified by recompiling the r6889
functions verbatim), so they are treated as capture artifacts rather than port bugs.
The `MASK` diff and the slope-landing residue can be revisited by porting
`setcubevisibility()` + the `neighbourdepth`/`neighbourstack` context exactly.

---

## A4 — Determinism check

Run the original harness twice with identical inputs and assert bit-identical
output. If the original is deterministic (it should be — no RNG in movement),
then *any* divergence from our port is our bug, and we never have to reason about
nondeterminism.

**Deliverable:** a gate in CI / the task runner.

---

## Collision acceptance criteria (layer 2)

Validated by the A3 real-geometry differential. These are the behaviours it
covers — **step-up is mandatory** — see `movement-physics.md` and
`physics.cpp:1102`.

- Walk onto the base of a ~18° ramp with no jump (base lip ~1 u) — must succeed
  via `trystepup`.
- Climb 4-unit steps (below `STAIRHEIGHT = 4.1`) without jumping; 5-unit steps
  must block.
- Walk down steps/slopes without airborne hops (`trystepdown`, `PHYS_STEP_DOWN`).
- Continuous ramp walk with no hop, via the `cansmooth` branch.
- Wall slide preserves tangential speed (`slideagainst`/`recalcdir`).
- Match `FLOORZ`/`SLOPEZ` cutoffs: ~18° and 30° surfaces walkable; >60° is wall.

---

## Golden numbers

The A1 harness (`tools/refharness/`) is the source of truth; the values below are
the derived expectations.

| Quantity | Expected |
| --- | --- |
| Ground forward top speed | 130 u/s |
| Ground diagonal / strafe top speed | 100 u/s |
| Airborne forward target | 169 u/s (`1.3 × 1.3`), control-limited |
| Ground velocity time constant | ~95% of target in ~0.32 s |
| Jump apex (flat, no input) | ~20.4 u (≈1.36 player heights) |
| Jump air time (flat, no input) | ~0.72 s |

Note: the jump is *damped* because the impulse lives in `vel`, which is friction-
lerped toward zero in air. The naive ballistic estimate (~39 u / 1.25 s) is wrong;
the harness gives the truth.

---

## Layer coverage

Plan A validates **layer 1 (velocity model)** and **layer 2 (collision)** exactly
via the A3 real-geometry differential. Layers 3–5 (timing, input, camera /
feedback) need their own checks — see [architecture.md](architecture.md).

**Layer 3–4 captured (A2 input trace).** `SAUER_INPUTTRACE`
(`tools/refharness/patches/a2b-input-trace.patch`, see the
[instrumented README](../tools/refharness/instrumented/README.md#input--timing-trace-sauer_inputtrace))
records the original's input + scheduling so the port can match it exactly:

- **Timing.** The main loop computes a **variable `curtime`** (measured `5..28` ms
  at a 200 fps cap, with `timeerr` always 0 while `gamespeed=100`). `physicsframe()`
  then runs `physsteps = ceil(diff / physframetime)` **fixed 5 ms** steps
  (`physframetime = clamp(scaletime(5)/100, 1, 5)`). So the variable frame time is
  absorbed by the **number of fixed steps**, not the step size — the port must do
  the same and never feed a variable `delta` into `moveplayer`.
- **Mouse look.** The whole frame's `SDL_MOUSEMOTION` delta is summed and applied
  **once per rendered frame** (`mousemove`), *not* once per physics tick. Every
  physics step in that frame reads the same yaw/pitch. (The earlier note in this
  doc — “apply once per physics tick” — was wrong.) Confirmed from a real capture
  (`traces/session-motion.input.csv`): exactly one application per motion frame
  (never more), 272 of which also had `physsteps > 1`; `cursens` is a constant
  `0.0909` (= `3/(33*1)`, so `mouseaccel = 0`), `Δyaw = dx*cursens` (max error
  `0.0012°`) and `Δpitch = -dy*cursens` saturated at `±90°` by `fixcamerarange()`.
- **Move/strafe/jump.** `forward`/`backward`/`left`/`right` set `move`/`strafe` to
  `-1/0/+1` from key state; `jump` sets `jumping` on press (via `canjump()`), and
  `modifyvelocity` consumes it on the ground.

**Ported into the live player.** `World/Ogz/MapPlayer.cs` implements all of the
above: fixed 5 ms steps caught up by count, per-frame mouse application, and the
`move`/`strafe`/`jumping` key mapping. Two sign fixes were needed because Godot's
captured-mouse/screen X handedness on macOS runs opposite to SDL's:

- **mouse yaw**: `yaw -= dx*cursens` (original: `+=`) so cursor-right looks right;
- **strafe**: `strafe_right` ⇒ `Strafe=+1` (original `dir()`: `-1`) so A/D aren't
  swapped.

Both are guarded by tests (`test/MapPlayerLookTest.cs`, `test/MapPlayerTest.cs`).

Still to check: match FOV (100), fine-tune sensitivity, confirm mouse accel (0);
reproduce gun sway and jump/land/water sounds; crouch, water, spectator-float.
