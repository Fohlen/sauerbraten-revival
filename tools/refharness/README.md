# A1 Reference Harness

Runs the **original Cube 2: Sauerbraten movement functions** (SVN r6889) standalone
at a fixed 5 ms step and emits golden CSV traces. This is the ground truth for
movement fidelity — see [../../docs/movement-verification.md](../../docs/movement-verification.md).

## What's original vs. shim

- `refphysics.cpp` — `vecfromyawpitch`, `modifyvelocity`, `modifygravity` copied
  **verbatim** from `src/engine/physics.cpp`, with constants `JUMPVEL=125`,
  `GRAVITY=200`.
- `refmath.h` — minimal `vec`/`physent` shim mirroring `src/shared/geom.h` and
  `src/shared/ents.h`, plus `game::` stubs.
- `main.cpp` — driver: fixed 5 ms loop (mirrors `moveplayer`), a flat-floor
  stand-in for `move()`, scripted input scenarios, CSV output.

The only intentional simplification is the flat-floor collision. Real geometry
is exercised in A3.

## Build & run

```sh
./build.sh
./refharness
```

CSVs are written to `out/`. The harness is deterministic, so traces are
reproducible; the authoritative values live in the table below.

## Current golden output (r6889)

| Scenario | Top horizontal speed | Apex | Airtime |
| --- | --- | --- | --- |
| `ground_forward` | 129.986 u/s | — | — |
| `ground_diag` | 99.989 u/s | — | — |
| `ground_strafe` | 99.989 u/s | — | — |
| `jump_only` | — | 19.604 u | 700 ms |
| `jump_forward` | 151.238 u/s | 19.604 u | 700 ms |

Notes:
- Top speeds approach but do not fully reach 130/100 within 1 s — the friction
  lerp is asymptotic.
- `jump_forward` exceeds the 130 ground cap because of the airborne 1.3× bonus,
  but is throttled by air control (`fric = 30`).
- The jump is **damped**: the impulse lives in `vel`, which is friction-lerped
  toward zero in the air. A naive ballistic estimate gives ~39 u / 1.25 s — wrong.

## Roadmap

- **A1** ✅ this harness (velocity model).
- **A2** instrument the original client for full-precision traces of real play.
- **A3** input-replay differential on identical geometry (flat plane, ramp, stairs).
- **A4** determinism gate.
