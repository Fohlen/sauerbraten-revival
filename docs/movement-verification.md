# Movement Verification (Plan A)

Movement is the product. We prove fidelity with the original SVN r6889 code,
not with vibes. This document defines the full Plan A: A1–A4.

The reference source is assumed at `/Users/fohlen/Downloads/sauerbraten-code-r6889`.

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
| `session2.csv` | 4162 | 99.9% | 100% | 99.9% | 100% |
| `session3.csv` | 7868 | 99.7% | 99.7% | 99.7% | 99.8% |

External events are resynced from `*.events` (teleports, jump pads) plus a
death/respawn detector (a >40 u one-step position jump — the engine's
`game::suicide` on `MAT_DEATH`/`o.z<0`). The remaining misses are subtle
**deformed-cube slope/step-up** interactions (`visibletris`/`classifyface` are
ported, but the hull still differs in some step cases) — the next thing to chase.

---

## A4 — Determinism check

Run the original harness twice with identical inputs and assert bit-identical
output. If the original is deterministic (it should be — no RNG in movement),
then *any* divergence from our port is our bug, and we never have to reason about
nondeterminism.

**Deliverable:** a gate in CI / the task runner.

---

## Collision acceptance criteria (layer 2)

Once our own collision is ported, these must match the original. **Step-up is
mandatory** — see `movement-physics.md` and `physics.cpp:1102`.

- Walk onto the base of a ~18° ramp with no jump (base lip ~1 u) — must succeed
  via `trystepup`.
- Climb 4-unit steps (below `STAIRHEIGHT = 4.1`) without jumping; 5-unit steps
  must block.
- Walk down steps/slopes without airborne hops (`trystepdown`, `PHYS_STEP_DOWN`).
- Continuous ramp walk with no hop, via the `cansmooth` branch.
- Wall slide preserves tangential speed (`slideagainst`/`recalcdir`).
- Match `FLOORZ`/`SLOPEZ` cutoffs: ~18° and 30° surfaces walkable; >60° is wall.

---

## Golden numbers (to be confirmed by A1)

Derived from the ported math; the harness replaces these with exact values.

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

Plan A validates **layer 1 (velocity model)** exactly and, once A3 runs on
geometry, the **layer 2 collision** too. Layers 3–5 (timing, input, camera /
feedback) need their own checks — see [architecture.md](architecture.md):

- match FOV (100), sensitivity, mouse accel (0);
- accumulate mouse motion per input poll and apply once per physics tick;
- match `moveres` sub-stepping;
- reproduce gun sway and jump/land/water sounds.
