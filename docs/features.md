# Features & Roadmap

Living inventory. Order reflects intent, not schedule. MVP target:
**insta CTF, effic CTF, FFA, and coop edit.**

## Roadmap

1. **Movement** — velocity model + full **Plan A** verification harness + HUD/CSV logger.
2. **World core** — native, dynamic octree (GDExtension): structure, collision
   traversal, incremental meshing, our `Resource` format, Godot rendering.
3. **Editor MVP** — runtime live cube editing, texturing/blending, entities,
   undo/redo, save/load. Built on the command system.
4. **Old-content conversion & loading** — one-time bake of hand-picked classic
   maps → our format; load and play. Fast validation of the port.
5. **Combat & spawns** — weapons/hitscan, flags, scoring, respawns.
6. **Modes** — FFA, insta CTF, effic CTF, coop edit.
7. **Community / online** — Steamworks networking, Workshop, UGC.

---

## 1. Movement & physics

- [x] Reverse-engineer original movement constants and update model.
- [x] First playable `CharacterBody3D` prototype of the velocity model.
- [ ] **Plan A verification harness** (A1–A4) — see
      [movement-verification.md](movement-verification.md).
- [ ] Ported octree collision: `move`, `collide`, `trystepup`, `trystepdown`.
- [ ] Fixed-step deterministic simulation (200 Hz, `moveres` substeps).
- [ ] Water, spectator/float, slope sliding, wall sliding, head bumps.

## 2. World core (dynamic octree)

- [ ] Reuse `octa.cpp` core via GDExtension with a compatibility shim.
- [ ] Port editor algorithms (add/remove/fill/texture/remip/select).
- [ ] Incremental per-chunk mesh emission within a frame budget.
- [ ] Live collision against the mutable octree.
- [ ] `Resource`-based world save/load (replaces `.ogz`).
- [ ] Material manifest + blend shader (approximate → exact).

## 3. Editor MVP *(defining feature)*

- [ ] Runtime in-game editing.
- [ ] **Command system**: every mutation is a serializable command with an inverse.
- [ ] Undo/redo via the command stack.
- [ ] Texturing, blending, entity placement.
- [ ] Selection, copy/paste, fill, remip.
- [ ] Save/load in our format.

## 4. Old-content conversion

- [ ] Headless converter reusing original `worldio.cpp` to re-serialize
      octree + entities + materials into our format.
- [ ] Hand-picked classic maps load and play.
- [ ] Lightmap import / rebake.
- [ ] Expand feature coverage (blends, layers, mapmodels) map by map.

## 5. Combat & spawns

- [ ] Hitscan weapons (insta rifle; effic loadout).
- [ ] Flags and capture logic (CTF).
- [ ] Scoring, respawns, spawn selection.
- [ ] HUD.

## 6. Modes

- [ ] FFA.
- [ ] Insta CTF.
- [ ] Effic CTF.
- [ ] Coop edit (multiplayer editing over the command stream).

## 7. Community / online

- [ ] Steamworks networking (movement + command replication).
- [ ] Server-authoritative world; client prediction.
- [ ] Steam Workshop upload/download; content hashes and parity.
- [ ] Matchmaking, friends, achievements.

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
- Opt-in code mods (GDScript/GDExtension) for total conversions; require trust.
- Workshop packaging: `.pck`/folder + `mod.json` manifest, API-versioned,
  load-ordered, multiplayer-parity-checked. See
  [architecture.md §7](architecture.md#7-modding--workshop).

## Cross-cutting: configuration

Godot-native only — `ProjectSettings`, `ConfigFile`, `Resource`, `InputMap`.
No CubeScript.
