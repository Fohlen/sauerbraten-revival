# Captured traces (A2/A3 fixtures)

Traces produced by the instrumented client (`SAUER_TRACE=...`). See
[`../instrumented/README.md`](../instrumented/README.md) for capture instructions
and the column format, and `../validate_trace.py` for the validator.

| Trace | Map | Steps | Duration | Notes |
| --- | --- | --- | --- | --- |
| `session1.csv` | `packages/base/church51.ogz` | 5273 | 26.4 s | walk / strafe / 23 jumps; a jump pad at step 3143 |

## session1

- **Map:** `packages/base/church51.ogz` — gzip'd OGR (2.1 MB → 9.1 MB).
  No accompanying `.cfg`, so it uses stock textures (cubic-script-def free).
- **Capture:** arm64 instrumented r6889 client, `SAUER_TRACE`.
- **Validation:** `python3 ../validate_trace.py session1.csv`
  - flat floor: **2686/2686 (100.0%)** exact `vel`
  - free air: **2398/2465 (97.3%)** exact `vel`+`falling`
  - misses are external (jump pad at step 3143) or collisions — not model errors.
- **Replay caveats for A3:** `session1.csv` predates the extended A2 patch, so it
  has no `session1.csv.events` (map name, jump pads, teleports) and would diverge
  at step 3143. Re-capture on this map with the current instrumented build for A3.
