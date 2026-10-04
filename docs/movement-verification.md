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

---

## A3 — Input-replay differential

Patch the original so movement inputs are read from a file instead of the
keyboard/mouse. One input file then drives **both** the original and our port;
diff the state traces.

Requires the same geometry on both sides. Until our octree/format loads classic
maps, run this on synthetic geometry authored identically in both (flat plane,
ramp, staircase). Once old-content loading lands (roadmap #4), replay on real maps.

A **deterministic bot policy** (e.g. hold W, turn yaw at a constant rate, jump
every N ms) removes human variance and makes the whole comparison automated.

**Deliverable:** `tools/refharness/replay` + an input-file format shared by both.

---

## A4 — Determinism check

Run the original harness twice with identical inputs and assert bit-identical
output. If the original is deterministic (it should be — no RNG in movement),
then *any* divergence from our port is our bug, and we never have to reason about
nondeterminism.

**Deliverable:** a gate in CI / the task runner.

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
feedback) need their own checks — see `architecture.md` and the drill table in
`features.md`:

- match FOV (100), sensitivity, mouse accel (0);
- accumulate mouse motion per input poll and apply once per physics tick;
- match `moveres` sub-stepping;
- reproduce gun sway and jump/land/water sounds.
