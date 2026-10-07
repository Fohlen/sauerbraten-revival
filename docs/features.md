# Features & Roadmap

Living inventory. Order reflects intent, not schedule. MVP target:
**insta CTF, effic CTF, FFA, and coop edit.**

This file only tracks status. Detail lives in the per-topic docs referenced below.

## Roadmap

1. **World core** — native, dynamic octree (C#/.NET): structure, collision
   traversal, incremental meshing, our `Resource` format, Godot rendering. — *core done*
2. **Old-content conversion & loading** — one-time bake of hand-picked classic
   maps → our format; render and load. — *done in the dev viewer; native bake pending*
3. **Physics (movement + collision)** — `modifyvelocity`/`move`/`collide`/
   `trystepup`/`trystepdown`, validated against the original (A1–A4). — *done, ~99.8%*
4. **Editor MVP** — runtime live cube editing, texturing/blending, entities,
   undo/redo, save/load. Built on the command system.
5. **Combat & spawns** — weapons/hitscan, flags, scoring, respawns.
6. **Modes** — FFA, insta CTF, effic CTF, coop edit.
7. **Community / online** — Steamworks networking, Workshop, UGC.
8. **Demos & replays** — record/playback of the movement + command stream.

> Rationale: movement's *velocity model* is proven, but collision can only be
> validated against the real maps the trace was captured on. So map loading
> comes before movement/collision refinement, and the editor follows.

---

## 1. World core (octree + rendering) — see [rendering.md](rendering.md)

Done:

- [x] Port the `octa.cpp` core to C# (`World/Octa.cs`, `World/CubeGeometry.cs`).
- [x] Octree collision traversal (`move`/`collide`/…, `World/Physics/`) — octree-based, not Godot physics.
- [x] **Chunked ArrayMesh emitter** (`OctaMesher.BuildChunked`, `ChunkedWorldMesh`,
      `ArrayMeshBuilder`); one material per vslot surface.
- [x] Material manifest: diffuse + tint (`MaterialBuilder`, `calctexgen`), per-face UVs.
- [x] Normal maps.
- [x] Glow (emission) maps.
- [x] Skybox: cubemap `skybox` mapvar → `SkyBuilder` + `shader_type sky`.
- [x] Texture pipeline: `ITextureSource` seam + `tools/texture-import/` + runtime dump fallback.
- [x] Material surfaces (water/lava/glass) — `MaterialMesher` + `MaterialTextures`.

Remaining:

- [ ] `Resource`-based world save/load (compact binary format) — blocks the editor and the standalone importer.
- [x] Per-frame chunk update budget + incremental dirty-chunk rebuilds
      (`WorldChunkRenderer`, `OctaMesher.BuildChunk`, `OctaFaceWalker.WalkRegion`;
      `test/IncrementalMeshTest.cs`). Default chunk 128, budget 4/frame — see
      [rendering.md](rendering.md#chunking-strategy) for the measured tradeoff.
- [ ] LOD via `ArrayMesh.add_lod()`.
- [ ] Deferred: specular maps, alpha/transparency (`alphafront`/`alphaback`, `LAYER_*`), blend shader.

## 2. OGZ import pipeline — see [ogz-import.md](ogz-import.md)

Done (lives in the dev viewer; not yet a standalone offline tool):

- [x] Metadata export patch (`SAUER_METADATA`) + metadata for `church51`, `bt_falls` in `metadata-catalogue/`.
- [x] OGZ parser (`World/Ogz/OgzReader.cs`, MAPVERSION 33) → runtime `Cube` octree.
- [x] Dev viewer renders maps from OGZ with materials + entities (`scenes/editor_scene.tscn`).
- [x] Content importers: `tools/model-import/` (assimp → glTF + skins), `tools/texture-import/`.
- [x] Entities: lights/spotlights, playerstarts, mapmodels, triggers (teleport/teledest/jumppad),
      items, particles (billboard effects), sounds, edit-mode markers.

Remaining:

- [ ] Standalone converter: OGZ + metadata → native octree `Resource` + `history.res` + `Level.tscn` (blocks on §1 save format).
- [ ] envmap / monsters / mode entities (flag, box, platform, elevator, …).
- [ ] Lightmap import / rebake (post-MVP).
- [ ] Expand feature coverage (blends, layers) map by map.

**New maps use our compact binary format only** — no OGZ at runtime.

## 3. Physics (movement + collision) — see [movement-verification.md](movement-verification.md), [movement-physics.md](movement-physics.md)

Done:

- [x] Reverse-engineer original movement constants and velocity model.
- [x] **A1** reference harness (`tools/refharness/`), **A2** trace capture (`SAUER_TRACE`), **A4** determinism gate.
- [x] Velocity model validated: **100% flat-floor**, **97.3% free-air** exact matches.
- [x] Port `move`/`collide`/`trystepup`/`trystepdown` + clipping/visibility machinery (`World/Physics/`).
- [x] **A3** input-replay differential on real geometry: ~99.8% (session2 pos 99.9%, session3 ~99.8%).
- [x] External events (teleports, jump pads, death/respawn) resynced; fixed 200 Hz `moveres` substeps.
- [x] Live player on the ported physics (`MapPlayer.cs`; timing/input layers 3–4 ported).

Remaining:

- [ ] Close the last ~0.2%: deformed-cube slope/step-up landing timing.
- [ ] Water, crouch, spectator-float physics states.

## 4. Editor MVP *(defining feature)*

See [editor-and-commands.md](editor-and-commands.md) for the command-system design
that underpins this entire section.

- [ ] Runtime in-game editing.
      - [x] **M4 prototype**: `World/Editor/` — `EditCursor` (cursor cast + grid/
            orient/selection), `CursorGeometry`, `OctaRay` (prototype ray cast),
            `EditorController` (wire-up + selection box), `EmptyMap` +
            `scenes/empty_map.tscn` (synthetic editable world). Asserted by
            `test/EditCursorTest.cs`, `test/OctaRayTest.cs`.
      - [ ] Replace `OctaRay` with the real `raycube` port; full `delta_do`
            modifier dispatch; remaining edit binds.
- [x] **Selection model** (M0): `Selection`/`Block3` port of `selinfo`
      (`World/Commands/`, asserted by `test/SelectionTest.cs`).
- [x] **World-command system** (M3): `WorldOp`/`WorldCommand`/payloads,
      `WorldCommandCodec` (`var_to_bytes` over positional arrays),
      `IWorld`/`OctaWorld` apply seam (`World/Commands/`, asserted by
      `test/CommandSystemTest.cs`). Shared by editor, game modes and scripts;
      a `CommandSource` tag distinguishes them.
- [x] Undo/redo via the command stack (see editor doc §5.5: undo is a
      server-sequenced `ApplyInverse`, own-`Editor`-changes-only; redo re-applies).
- [x] Port editor algorithms (add/remove/fill/texture/remip/select).
      - [x] **M1a structural primitives**: `CopyCube`/`PasteCube`
            (`World/OctaEdit.cs`), `DiscardChildren`/`GetMippedTexture`/`FreeOcta`/
            `FamilySize`/`OppositeOcta` (`World/Octa.cs`); asserted by
            `test/OctaEditTest.cs`.
      - [x] **M1b lookups & mapping**: `SubdivideCube` (full, incl. edge resampling
            + `validatec`), `LookupCube`, `BlockCube` (`World/OctaEdit.cs`);
            asserted against the original engine's exact edge bytes via
            `tools/octaprobe/` and `test/OctaLookupTest.cs`.
      - [x] **M1c op handlers**: `BlockCopy` (capture), `PasteBlock` (transparent
            paste), `DeleteCube`, `Fill`, `GetMaterial`, `ForEachSelCube`
            (`World/OctaEdit.cs`); asserted by `test/OctaOpsTest.cs` with engine
            ground truth.
      - [x] **M1 texture/material/flip/rotate**: `SetFaceTexture`,
            `ReplaceTexture`, `SetMaterial`/`EditMaterial`, `FlipCube`,
            `RotateCube`, `ForceMip` (`World/OctaEdit.cs`); asserted against the
            engine by `test/OctaFaceTest.cs`. Fixed a latent whole-byte vertex-write
            bug in `CubeGeometry.SetCubeVector` (now nibble-correct).
      - [x] **M1d `remip`**: `VisibleFace` + `Remip`/`RemipAll`
            (`World/OctaEdit.cs`); asserted against the engine by
            `test/RemipTest.cs`. M1 complete.
- [ ] Texturing, blending, entity placement.
- [ ] Selection, copy/paste, fill, remip.
- [ ] Save/load in our format.

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

See [authority-and-latency.md](authority-and-latency.md) — the network model is
**server-authoritative** (edits, movement, combat) with client prediction; this
diverges deliberately from Cube 2's client-authoritative model because of the
Steam leaderboard. **Prerequisite:** close the movement determinism gap first.

- [ ] **Authority decision** — documented; server authority is a hard requirement
      of the leaderboard (client authority ruled out for the product).
- [ ] **Prediction-scope rule** — movement predicts only against the last
      acknowledged world-command sequence ([authority-and-latency.md §7](authority-and-latency.md#7-the-rule-this-forces-prediction-scope-open)).
- [ ] Steamworks networking (movement + command replication).
- [ ] Server-authoritative world; client prediction + reconciliation; lag
      compensation for hitscan.
- [ ] Steam Workshop upload/download; content hashes and parity.
- [ ] Matchmaking, friends, achievements.

## 8. Demos & replays

- [ ] Record/playback the replicated stream (movement + world commands).
- [ ] Shares the networking serialization, so it lands with #7.
- [ ] **Visual A/B** against the original: play the same demo in both engines.
- [ ] Not used for A3 — demos replay quantized *outputs* and carry no `jumping`
      input, so they can't re-simulate movement. A3 uses the captured
      `SAUER_TRACE` fixtures (`tools/refharness/traces/`).

---

## Cross-cutting: dynamic world geometry

Cube 2's world is statically generated geometry; only a few entities move. We
deliberately remove that limit. All mutations are commands, so the same mechanism
powers live editing, crumbling walls / collapsing floors, scripted geometry, and
networked world changes. This is a first-class feature, not a side effect.

## Cross-cutting: modding

- Data-first mods (Resources, ConfigFiles, our maps) are safe and easy.
- Opt-in code mods (C#) for total conversions; require trust.
- Workshop packaging: `.pck`/folder + `mod.json`, API-versioned, load-ordered,
  multiplayer-parity-checked. See [architecture.md §7](architecture.md#7-modding--workshop).

## Cross-cutting: configuration

Godot-native only — `ProjectSettings`, `ConfigFile`, `Resource`, `InputMap`.
No CubeScript.
