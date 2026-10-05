# Features & Roadmap

Living inventory. Order reflects intent, not schedule. MVP target:
**insta CTF, effic CTF, FFA, and coop edit.**

## Roadmap

1. **Movement (velocity model)** — done and validated. See
   [movement-verification.md](movement-verification.md).
2. **World core** — native, dynamic octree (C#/.NET): structure, collision
   traversal, incremental meshing, our `Resource` format, Godot rendering.
3. **Old-content conversion & loading** — one-time bake of hand-picked classic
   maps → our format; render and load. Gets real geometry in-engine.
4. **Movement refinement (collision)** — port `move`/`collide`/`trystepup`/
   `trystepdown`; replay the captured trace on the same map (A3) and close the gap.
5. **Editor MVP** — runtime live cube editing, texturing/blending, entities,
   undo/redo, save/load. Built on the command system.
6. **Combat & spawns** — weapons/hitscan, flags, scoring, respawns.
7. **Modes** — FFA, insta CTF, effic CTF, coop edit.
8. **Community / online** — Steamworks networking, Workshop, UGC.
9. **Demos & replays** — record/playback of the movement + command stream. A
   product feature and the vehicle for visual A/B against the original.

> Rationale: movement's *velocity model* is proven, but collision can only be
> validated against the real maps the trace was captured on. So map loading
> comes before movement refinement, and the editor follows.

---

## 1. Movement (velocity model) — done

- [x] Reverse-engineer original movement constants and update model.
- [x] First playable `CharacterBody3D` prototype of the velocity model.
- [x] **A1** reference harness (original code, fixed 5 ms) — `tools/refharness/`.
- [x] **A2** instrumented client + trace capture (`SAUER_TRACE`).
- [x] **A4** determinism gate.
- [x] Velocity model validated against real gameplay: **100% flat-floor**,
      **97.3% free-air** exact matches (air misses = jump pads/collisions).
- [ ] **A3** input-replay differential — blocked on map geometry (see #4).

## 2. World core (dynamic octree + rendering)

See [rendering.md](rendering.md) for the ArrayMesh + RenderingServer approach.

- [ ] Port the `octa.cpp` core to C# with a small compatibility shim.
- [ ] Collision traversal (`move`/`collide` primitives) — octree-based, not Godot physics.
- [ ] **Chunked ArrayMesh emitter**: octree → per-chunk ArrayMesh → RenderingServer.
- [ ] Per-frame update budget (max N chunks/frame) to avoid hitches.
- [ ] LOD integration via `ArrayMesh.add_lod()` for distance-based simplification.
- [ ] Live collision against the mutable octree (rendering and collision decoupled).
- [ ] `Resource`-based world save/load (compact binary format).
- [ ] Material manifest + blend shader (approximate → exact).

## 3. OGZ import pipeline

See [ogz-import.md](ogz-import.md) for full architecture.

- [ ] **Metadata export patch**: CubeScript patch to original client that
      exports texture slots, mapmodels, lights, etc. to JSON on map load.
- [ ] **Generate metadata**: Run patched client on all target maps, commit
      JSON to `data/metadata/`.
- [ ] **OGZ parser**: C# binary parser for `.ogz` octree + entities.
- [ ] **Importer**: OGZ + metadata → native octree `Resource` + `Level.tscn`.
- [ ] Render + load hand-picked classic maps.
- [ ] First target: `packages/base/church51.ogz` (matches trace fixture
      `session1.csv`).
- [ ] Lightmap import / rebake (post-MVP).
- [ ] Expand feature coverage (blends, layers, mapmodels) map by map.

**New maps use our compact binary format only** — no OGZ at runtime.

## 4. Movement refinement (collision)

- [ ] **Port `move`/`collide` exactly** against the octree.
- [ ] **Port `trystepup`/`trystepdown` exactly** (`STAIRHEIGHT = 4.1`, the
      `cansmooth` slope branch, the `1/scale` vertical component). Required for
      ramps and stairs; Godot's `move_and_slide()` has no up-step.
- [ ] A3: replay the captured trace on the **same map** and diff `o`/`vel`/`falling`.
- [ ] Record/replay external movement events (jump pads, teleports) so replay
      doesn't diverge at them.
- [ ] Fixed-step deterministic simulation (200 Hz, `moveres` substeps).
- [ ] Water, spectator/float, slope sliding, wall sliding, head bumps.

## 5. Editor MVP *(defining feature)*

- [ ] Runtime in-game editing.
- [ ] **Command system**: every mutation is a serializable command with an inverse.
- [ ] Undo/redo via the command stack.
- [ ] Port editor algorithms (add/remove/fill/texture/remip/select).
- [ ] Texturing, blending, entity placement.
- [ ] Selection, copy/paste, fill, remip.
- [ ] Save/load in our format.

## 6. Combat & spawns

- [ ] Hitscan weapons (insta rifle; effic loadout).
- [ ] Flags and capture logic (CTF).
- [ ] Scoring, respawns, spawn selection.
- [ ] HUD.

## 7. Modes

- [ ] FFA.
- [ ] Insta CTF.
- [ ] Effic CTF.
- [ ] Coop edit (multiplayer editing over the command stream).

## 8. Community / online

- [ ] Steamworks networking (movement + command replication).
- [ ] Server-authoritative world; client prediction.
- [ ] Steam Workshop upload/download; content hashes and parity.
- [ ] Matchmaking, friends, achievements.

## 9. Demos & replays

- [ ] Record/playback the replicated stream (movement + world commands).
- [ ] Shares the networking serialization, so it lands with #8.
- [ ] **Visual A/B** against the original: play the same demo in both engines.
- [ ] Not used for A3 — demos replay quantized *outputs* and carry no `jumping`
      input, so they can't re-simulate movement. A3 uses the captured
      `SAUER_TRACE` fixtures (`tools/refharness/traces/`).

---

## Cross-cutting: dynamic world geometry

Cube 2's world is statically generated geometry; only a few entities move. We
are deliberately removing that limit. Because all mutations are commands, the
same mechanism powers:

- live editing,
- **crumbling walls / collapsing floors** (e.g. a race mode),
- scripted geometry in any game mode,
- networked world changes,
- undo/redo.

This is a first-class feature, not a side effect.

## Cross-cutting: modding

- Data-first mods (Resources, ConfigFiles, our maps) are safe and easy.
- Opt-in code mods (C#) for total conversions; require trust.
- Workshop packaging: `.pck`/folder + `mod.json` manifest, API-versioned,
  load-ordered, multiplayer-parity-checked. See
  [architecture.md §7](architecture.md#7-modding--workshop).

## Cross-cutting: configuration

Godot-native only — `ProjectSettings`, `ConfigFile`, `Resource`, `InputMap`.
No CubeScript.
