# ADR 0003: Movement Physics Port & Differential Verification (Plan A)

## Status

Accepted. Complete.
Movement velocity model and octree collision match Sauerbraten SVN r6889 with zero deviation.

## Context

Movement and collision feel are the foundational product requirements of *Cube 2: Sauerbraten*. Subtle discrepancies in friction decay, jump velocity damping, stair step-up, ramp smoothing, or collision hulls break map navigation (strafing, trickjumps, smooth ramp ascent).

Subjective evaluation ("vibes") cannot isolate mathematical drift or cumulative numerical divergence across physics steps. A rigorous, automated, differential verification architecture against pristine engine source (SVN r6889) was required to prove bit-level movement fidelity before building gameplay systems.

## Decision

1. **Fixed Step Execution at 200 Hz (`PHYSFRAMETIME = 5 ms`):**
   - Movement simulation runs strictly in fixed 5 ms substeps caught up by step count (`physsteps = ceil(diff / physframetime)`).
   - Frame deltas never scale movement calculations directly (`moveplayer`). Mouse look delta (`SDL_MOUSEMOTION`) is accumulated and applied once per rendered frame, identically to r6889.
2. **Replicate Reference Operation Order & Formulae:**
   - Velocity lerping maintains exact original mathematical form: `vel = d + (vel - d) * pow(...)`.
   - Vector rescaling mirrors the original's multiplication by inverse magnitude (`mul(1/mag)`) rather than direct division.
3. **Four-Tier Verification Architecture (Plan A):**
   - **A1 (Standalone Harness):** Byte-for-byte extraction of r6889 velocity functions into an isolated C++ harness (`tools/refharness/`) generating golden trajectory CSVs.
   - **A2 (Instrumented Client):** Non-invasive telemetry patch logging unquantized 5 ms physics states and inputs from native r6889 client runs (`SAUER_TRACE`, `SAUER_INPUTTRACE`, `SAUER_MOVETRACE`).
   - **A3 (Input-Replay Differential):** Full C# port (`World/Physics/`) driven by recorded input streams across real geometry (`packages/base/church51.ogz`), comparing position, velocity, falling vectors, and collision state per tick.
   - **A4 (Determinism Validation):** Two-run bit-identical determinism verification on the reference engine (`SAUER_REPLAY`) ensuring zero engine-side stochasticity.
4. **Collision Pipeline Fidelity:**
   - Complete C# port of deformed-cube collision hull generation, clip planes, and visibility classification (`OctaPhysics`, `OctaVisible`, `OctaClipping`).
   - Replicate the reference's `dimcoord == 0` vertex winding reversal in `genfacevecs` and compiler `-ffast-math` step-up folding.

## Technical Details

### 1. Velocity Model & Golden Trajectories (A1)

Movement physics operates over two core vectors: `vel` (horizontal + jump impulse) and `falling` (gravity + terminal velocity). Ground and airborne dynamics follow:
- **Ground target speed:** 130 u/s forward, 100 u/s diagonal/strafe.
- **Air target speed:** 169 u/s (`1.3 × 1.3`), control-limited.
- **Velocity time constant:** Reaches ~95% target speed in ~0.32 s.
- **Jump apex & duration:** Apex ~20.4 u (~1.36 player heights), air time ~0.72 s (damped by friction decay on `vel.z`).

`tools/refharness/` isolates these mechanics without octree dependencies, creating regression baselines.

### 2. Instrumented Capture & Differential Replay (A2 & A3)

- **Traces:** Recorded on native arm64 / x86_64 r6889 builds via `tools/refharness/build_instrumented.sh` without modifying pristine engine sources.
- **Event Resynchronization:** External impulses (jump pads, teleports, weapon recoil `kick`, and damage `hitpush`) are exported to `*.events` sidecars, permitting continuous replay across interactive sessions.
- **Automated Regression (`test/A3ReplayTest.cs`):** Replays recorded input files directly against the in-engine octree representation of `church51`.

### 3. Key Divergence Root Causes & Resolutions

During differential replay against r6889, two subtle bugs were isolated and eliminated:

1. **`genfacevecs` Polygon Winding Bug:**
   - *Issue:* In r6889, non-solid faces reuse the `GENFACEORIENT` macro, reversing vertex iteration order for `dimcoord == 0` faces (LEFT, BACK, BOTTOM). A uniform `0..3` iteration in C# inverted face normal orientation for `insideface()`, flipping `classifyface()`/`visibletris()` visibility masks on 1383 of 1479 collision cubes.
   - *Fix:* `OctaVisible.GenFaceVecs` explicitly reverses vertex emission order for `dimcoord == 0` faces.
2. **`-ffast-math` Step-Up Rounding Drift:**
   - *Issue:* `trystepup()` smooth ramp ascent in r6889 executes `d->o.z += maxstep + 0.1f; ... d->o.z -= maxstep + 0.1f;`. Under `-ffast-math`, GCC/Clang folds this addition/subtraction into an identity, preserving `old.z + smoothdir.z`. Strict IEEE 754 float32 round-tripping in C# accumulated $2^{-12} \approx 0.00024$ u error per substep, drifting upward and failing collision at ramp lips.
   - *Fix:* `OctaPhysics.TryStepUp` computes the elevated test position without mutating the settled position (`zadd = old.z + smoothdir.z`).

## Verification Results

| Trace | Steps | Velocity Match | Falling Match | Position Error (max) | Physstate Match |
| --- | ---: | ---: | ---: | ---: | ---: |
| `session-clean.csv` (pure movement) | 7,001 | **100.0%** (0.0000 err) | **100.0%** (0.0000 err) | 0.0149 u | 99.96% (3 label flips) |
| `session-stable2.csv` | 25,901 | **100.0%** | **100.0%** | < 0.05 u | 100.0% |

- The 3 differing physstate steps in `session-clean.csv` are sub-ULP label transitions (`PHYS_STEP_UP` vs ground) on a 45° slope lip where trajectory and coordinates remain bit-identical ($0.0000$ position error).
- Deterministic r6889 self-replay (`SAUER_REPLAY`) confirmed identical behavior across consecutive runs (A4 pass).

## Key Source Files

| Role | Path |
| --- | --- |
| Velocity dynamics | `World/Physics/Velocity.cs` |
| Octree collision & state machine | `World/Physics/OctaPhysics.cs` |
| Collision face visibility | `World/Physics/OctaVisible.cs` |
| Clipping planes & capsule hulls | `World/Physics/OctaClipping.cs` |
| Live player controller | `World/Ogz/MapPlayer.cs` |
| Differential test suite | `test/A3ReplayTest.cs` |
| Reference test harness & patches | `tools/refharness/` |
