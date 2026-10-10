# Features & Roadmap

**Single source of truth for the roadmap and status.** Design docs explain *how*
something works; this file says *what* we are building, *in what order*, and
*what is done*. If another doc and this file disagree about ordering, timing, or
status, this file wins — fix the other doc.

MVP target: **insta CTF, effic CTF, FFA, and coop edit.**

Status legend: `[x]` done · `[~]` partial · `[ ]` not started / deferred.

---

## Roadmap

Order reflects intent, not schedule.

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

### Post-MVP / deferred fidelity

Explicitly **out of scope for the MVP**, to be picked up after the roadmap above.
This list is the one place these are tracked; design lives in the linked docs.

- **Lighting** ([lighting.md](lighting.md)) — **T1** import the original baked
  lightmaps (cheap, high value). *Opportunistic*: schedule it to ride along with
  the first editor-driven mesher/format change rather than as a dedicated phase.
  **T2** rebake (`calclight`), envmap world sampling, blend-layer rendering —
  strictly post-MVP.
- **Renderer extras** ([rendering.md](rendering.md)) — LOD, specular maps,
  alpha/transparency (`alphafront`/`alphaback`, `LAYER_*`), exact `visibletris`,
  face merges, T-joints, grass, RenderingServer mesh streaming.
- **`movable.cpp`** (box/barrel/platform/elevator) — deliberately not ported; SP-only.
- **Single-player / bot AI** — out of MVP scope.

---

## 1. World core (octree + rendering)

See [rendering.md](rendering.md).

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
- [x] `Resource`-based world save/load (compact binary format) — `WorldSnapshot`
      (canonical octree + entity blob, versioned, content-hashed) wrapped in Godot
      `WorldResource`/`HistoryResource`, plus the map-directory layout + compaction +
      archive content hash (`World/Save/`, asserted by `test/WorldSnapshotTest.cs`
      and `test/MapFormatTest.cs`). Round-trips church51 exactly. See
      [editor-and-commands.md](editor-and-commands.md#7-map-format-versioned-command-history).
- [x] Per-frame chunk update budget + incremental dirty-chunk rebuilds
      (`WorldChunkRenderer`, `OctaMesher.BuildChunk`, `OctaFaceWalker.WalkRegion`;
      `test/IncrementalMeshTest.cs`). Default chunk 128, budget 4/frame — see
      [rendering.md](rendering.md#chunking-strategy) for the measured tradeoff.

Remaining:

- [ ] LOD via `ArrayMesh.add_lod()`.
- [ ] RenderingServer mesh streaming (bypass the scene tree; [rendering.md](rendering.md#renderingserver-usage-planned-optimisation)).
- [ ] Exact `visibletris` culling, face merges, T-joints, grass.
- [ ] **Post-MVP fidelity** (see [Post-MVP / deferred fidelity](#post-mvp--deferred-fidelity)):
      lightmaps/layers/envmap ([lighting.md](lighting.md)), specular maps,
      alpha/transparency (`alphafront`/`alphaback`, `LAYER_*`), blend shader.

## 2. OGZ import pipeline

See [ogz-import.md](ogz-import.md).

Done (lives in the dev viewer; not yet a standalone offline tool):

- [x] Metadata export patch (`SAUER_METADATA`) + metadata for `church51`, `bt_falls` in `metadata-catalogue/`.
- [x] OGZ parser (`World/Ogz/OgzReader.cs`, MAPVERSION 33) → runtime `Cube` octree.
- [x] Dev viewer renders maps from OGZ with materials + entities (`scenes/editor_scene.tscn`).
- [x] Content importers: `tools/model-import/` (assimp → glTF + skins), `tools/texture-import/`.
- [x] Entities: lights/spotlights, playerstarts, mapmodels, triggers (teleport/teledest/jumppad),
      items, particles (billboard effects), sounds, edit-mode markers.
- [x] envmap / monsters / mode entities — all represented (import only). See
      [ogz-import.md § Entity porting status](ogz-import.md#entity-porting-status)
      for the concrete work items (runtime behaviour remains unported):
      - [x] `ET_ENVMAP` (4) → `EnvMapProbe` (`ReflectionProbe`). `EnvMapBuilder`
            resolves `attr1` radius (0 → 128, clamp 0..10000), `attr2` cubemap size
            (4..9, capped by `envmapsize` 7), `attr3` blur (1..2). Asserted by
            `test/EntityEnvMapTest.cs` on **earthstation** (38 probes). The
            sampling material side (`SHADER_ENVMAP`, `closestenvmap`) is still
            open — see [lighting.md](lighting.md).
      - [x] Mode entities (CTF/capture) with their **models and gizmos**:
            `ModeEntityBuilder` handles `FLAG`, `BASE`, `BOX`, `BARREL`,
            `PLATFORM`, `ELEVATOR`, `RESPAWNPOINT`, `CARROT` (the `fixentity`
            attribute shift + `entradius` facing arrows). Model import now also
            converts `flags/{neutral,red,blue}` and `base/{neutral,red,blue}`.
            Asserted by `test/EntityModeTest.cs` on **bt_falls** (5 base, 2 flag)
            and **lost** (57 box, 114 barrel, 11 platform, 13 elevator).
      - [x] Monsters (`MONSTER`, 21): `MonsterBuilder` maps them to `Monster`
            nodes (`attr1` yaw, `attr2` type, `attr3` tag), resolving the ported
            `monstertypes` table (`MonsterTypes`) and the `bscale` body box, plus
            the `entradius` facing arrow. The nine monster models and their view
            weapons now convert.
            Asserted by `test/EntityMonsterTest.cs` on **crnsp1** (220, all 9
            types) and **lost** (235). The AI (`monster.cpp`, `ai.cpp`,
            `waypoint.cpp`) is game logic and lands with Modes/Combat.
- [x] `bounce()` physics for `ENT_BOUNCE` (grenades/debris/gibs — `weapon.cpp`
      `bouncer`) **ported and wired**: `OctaPhysics.Bounce` (both forms),
      `interppos`, the `collide(..., playercol)` overload, the dynent cache,
      `plcollide`/`ellipsecollide`, and the `game::bounced`/`dynentcollide` seams.
      The runtime node is `World/Entities/BounceBody.cs`. Asserted by
      `test/BouncePhysicsTest.cs` and `test/BounceBodyTest.cs`. Note: boxes/
      barrels/platforms are **not** `ENT_BOUNCE` — they are `ENT_INANIMATE` and use
      `moveplayer`/`moveplatform` (`movable.cpp`).
- [x] **`movable.cpp` (box/barrel/platform/elevator) — deliberately not ported.**
      It is hard-gated to single-player (`if(!m_dmsp && !m_classicsp) return;`,
      `movable.cpp:90`), so **every mode we target (insta/effic CTF, FFA, coop edit)
      never simulates one** — and none of `ctf.h`/`capture.h`/`collect.h` reference
      it. Only **16 of 316** base maps use these types, all SP-campaign maps
      (`mpsp10`, `crnsp1`, `skrsp1`, `level9`, …); our fixtures (`church51`,
      `bt_falls`) have zero. It is also the *wrong* mechanism for the dynamic-world
      plan: our mutation path is `WorldCommand`-based (see
      [Cross-cutting: dynamic world geometry](#cross-cutting-dynamic-world-geometry)),
      so a moving platform / crumble should be a `GameMode`/`Script` command
      stream, not a revived SP-only rigid-body subsystem. The mode entities stay
      as **representation** (models + gizmos), which is correct. Revisited only if
      we ship the SP campaign, and then via commands.

Remaining:

- [ ] Standalone converter: OGZ + metadata → native octree `Resource` + `history.res` + `Level.tscn`.
      - [x] **Geometry + entities**: `OgzConverter` writes `world.res` + `Level.tscn` from
            a parsed `.ogz` (asserted by `test/MapFormatTest.cs`, church51 round-trips
            exactly). Metadata-driven material manifest emission is still pending.
      - [ ] CLI/headless front end for `OgzConverter` (the GUI wizard exists;
            [ogz-import.md § Converter GUI](ogz-import.md#converter-gui)).
- [~] **One-shot full metadata export** — capture all resolved engine state in the
      single ~25-min sweep (resolved shaders/slots/vslots/config) as **JSON only**;
      no binary sidecar (lightmaps/blendmap/PVS are already in the OGZ). The
      exporter is **extended and validated** (`shaderType`/`smooth`/`texmask`/grass,
      `shaders[]`, `config`, vslot `linked`/`changed`); re-exporting the whole
      catalogue still pending. Design:
      [lighting.md § Export everything in one pass](lighting.md#4-export-everything-in-one-pass).
- [ ] **Lightmap import** (T1; opportunistic — piggyback on the editor mesher/
      format change, not a dedicated phase; [lighting.md](lighting.md)) — read the
      embedded atlases, preserve the per-face lightmap link (`lmid`/`uv`/`norm`)
      through our format, sample in a custom shader.
- [ ] Parse the **blendmap** and **PVS** sections (post-MVP; [lighting.md](lighting.md)).
- [ ] Expand feature coverage (blends, layers) map by map.

**New maps use our compact binary format only** — no OGZ at runtime.

## 3. Physics (movement + collision)

See [movement-verification.md](movement-verification.md) and
[movement-physics.md](movement-physics.md).

Done:

- [x] Reverse-engineer original movement constants and velocity model.
- [x] **A1** reference harness (`tools/refharness/`), **A2** trace capture (`SAUER_TRACE`), **A4** determinism gate.
- [x] Velocity model validated: **100% flat-floor**, **97.3% free-air** exact matches.
- [x] Port `move`/`collide`/`trystepup`/`trystepdown` + clipping/visibility machinery (`World/Physics/`).
- [x] **A3** input-replay differential on real geometry: **`session-clean.csv` 100.0% velocity, 100.0% falling, 0.0149 u max position error** (7001 steps, event-free movement). This validates **layer 2 (collision)** — ramps, step-up/down, wall slide, `FLOORZ`/`SLOPEZ` cutoffs.
- [x] External events (teleports, jump pads, death/respawn) resynced; fixed 200 Hz `moveres` substeps.
- [x] Deterministic reference replay (`SAUER_REPLAY`, self-terminating) + reference `move()` trace (`SAUER_MOVETRACE`).
- [x] Live player on the ported physics (`MapPlayer.cs`; timing/input layers 3–4 ported).
- [x] Water + spectator-float states (`PhysEnt.InWater`/`State`, the `vel /= 8`
      entry damp, mid-body water-exit re-lookup, `CS_EDITING`/`CS_SPECTATOR`
      floating branch) — verified against the original via the A1 golden fixtures
      (`tools/refharness/golden/water_*.csv`, `float_forward.csv`) and
      `test/WaterPhysicsTest.cs`. **Crouch does not exist in Cube 2 r6889** (no
      crouch key, fixed player height) and is therefore not a port item.
- [x] Close the last ~0.2%: deformed-cube slope/step-up landing timing (fixed via the `-ffast-math` step-up fold + event-free recording; the 3/7001 residual are `PHYS_STEP_UP` **label-only** flips on a 45° slope contour with zero position/velocity error).
- [x] Water, spectator-float physics states (crouch is not a Cube 2 feature —
      no crouch key and a fixed player volume; nothing to port).
- [x] `bounce()` for `ENT_BOUNCE` bodies (grenades/debris/gibs) — ported and
      wired (`OctaPhysics.Bounce`, `BounceBody`, the dynent cache + `plcollide`;
      asserted by `test/BouncePhysicsTest.cs` / `test/BounceBodyTest.cs`).
- [x] ~~Movable physics (box/barrel/platform/elevator) — `ENT_INANIMATE`,
      `moveplayer`/`moveplatform`. **Deliberately not ported**: SP-only
      (`movable.cpp:90`), unused by every target mode, and superseded by the
      command-based dynamic-world plan (see §2 above).~~

Remaining:

- [ ] Layers 3–5 camera/feedback: match FOV (100), tune sensitivity, mouse accel,
      gun sway, jump/land/water sounds. See
      [movement-physics.md § Port notes](movement-physics.md#5-port-notes--deviations-in-the-current-mvp).
- [ ] Optional: extend the refharness to give `bounce()` a golden differential
      (current harness collision is a flat-floor stand-in).

## 4. Editor MVP *(defining feature)*

See [editor-and-commands.md](editor-and-commands.md) for the command-system design
that underpins this entire section, and §10 there for the M0–M9 **implementation
order** (design detail for this roadmap item).

- [x] Runtime in-game editing.
      - [x] **M4 prototype**: `World/Editor/` — `EditCursor` (cursor cast + grid/
            orient/selection), `CursorGeometry`, `OctaRay` (ray cast),
            `EditorController` (wire-up + selection box), `EmptyMap` +
            `scenes/empty_map.tscn` (synthetic editable world). Asserted by
            `test/EditCursorTest.cs`, `test/OctaRayTest.cs`.
      - [x] Replace `OctaRay` with the real `raycube` port
            (`World/Physics/OctaRaycast.cs` + render-hull `genclipplanes` in
            `OctaClipping.GetRenderClipPlanes`), asserted against the engine via
            `tools/rayprobe/` (`test/OctaRaycastTest.cs`). `OctaRay` is now a thin
            editor seam (RAY_SKIPFIRST + grid size).
      - [x] Full `delta_do` modifier dispatch; remaining edit binds.
            `EditorController` ports the `delta_use`/`delta_do` universal delta
            (`data/stdlib.cfg`): a held modifier key sets the active operation, the
            wheel dispatches on it. The full id table (`DeltaModifier`) is routed —
            fill (0), grid (1), face (2), corner (3), rotate (4), texture (5) — with
            the not-yet-ported ids (height-map brush, entity attrs, vslot params,
            combos) explicit no-ops rather than a silent fall-through to fill.
            Remaining binds wired: `edittex`, `editmat`, `editremip`, `allfaces`,
            `selcorners`, `pastehilite`, `pushsel`. Asserted by
            `test/DeltaDispatchTest.cs`; binds are `InputMap` actions asserted by
            `test/EditorInputMapTest.cs`.
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
      - [x] **M1e selection transforms**: `FlipSelection` (`mpflip`) and
            `RotateSelection` (`mprotate`); asserted by
            `test/SelectionTransformTest.cs`.
- [x] Selection, copy/paste, fill, remip — `Selection`/`Block3` (`World/Commands/`),
      `BlockCopy`/`PasteBlock`/`Fill`/`DeleteCube`/`Remip`/`RemipAll`
      (`World/OctaEdit.cs`); asserted by `test/SelectionTest.cs`,
      `test/OctaOpsTest.cs`, `test/RemipTest.cs`. The edit binds and `delta_do`
      dispatch that drive them interactively are **done** — see the M4d item above
      (`EditorController` + `DeltaModifier`, `test/DeltaDispatchTest.cs`).
- [x] Save/load in our format (M5): `WorldSnapshot` + map directory (`world.res`,
      `history.res`, `Level.tscn`) + compaction + archive hash; `OgzConverter`;
      `F2` save / `F3` load binds in `EditorController`. See
      [editor-and-commands.md](editor-and-commands.md#7-map-format-versioned-command-history).

Remaining:

- [ ] **M4e GUI**: texture/material pickers, entity placement, keybinds via
      `InputMap` ([editor-and-commands.md §10](editor-and-commands.md#10-implementation-order-milestones)).
- [ ] Texturing, blending (the blendmap + lightmap-alpha pipeline is the layer
      model — see [lighting.md § Layers](lighting.md#14-layers--the-alpha-channel-is-the-blend-mask)),
      entity placement.
- [ ] **Prefabs, recipes, cross-map paste** (M8) — `ReplaceBlock` content
      resources, rotation, recipe API.
- [ ] Persistent user keybinds (`user://input.cfg`) — see
      [Cross-cutting: configuration](#cross-cutting-configuration).

## 5. Combat & spawns

- [~] Player model + view — third-person camera first (inspect models).
      - [x] Third-person camera toggle (`MapPlayer`, `third_person` bind, engine
            `thirdpersondistance`/`thirdpersonup`; `test/MapPlayerTest.cs`).
      - [x] **MD5 import** (`tools/model-import/import_models.py`): skeletal player
            bodies (`mrfixit`, `snoutx10k`, `ogro2`, `inky`, `captaincannon`) and
            their `hudguns`. New manifest fields `skeleton`, per-part `skins`
            (`md5skin`), `tags` (`md5tag`), de-duplicated `anims` (`md5anim`); fixed
            `<dds>`/`<mad:...>` tag stripping and variant-dir skin resolution.
      - [x] Load the player body model into the scene (`PlayerModel`; third-person,
            grounded; orientation verified).
      - [x] **MD5 animation parse** (`World/Entities/Md5Anim.cs` +
            `Md5MeshBind.cs`, `Quat`/`Dq`): faithful port of the engine's
            `loadanim` frame compose, asserted against golden output from
            `tools/animprobe` (`test/Md5AnimTest.cs`, `test/golden/anim/`).
      - [x] Runtime anim wiring: the model glb is **baked with its `anims.cfg`
            clip table** (`tools/model-import` assimp + merge), so Godot's own importer
            produces the `AnimationPlayer` and animates the skeleton — no manual
            engine-pose mapping (that deformed the mesh; see
            [character-animation.md](character-animation.md)). `PlayerModel.Play(name)`
            selects clips; idle runs by default. Body orientation verified in-engine
            (upright, facing correct).
      - [~] Anim state machine mirroring `renderclient`: **movement done**
            (`MapPlayer.SelectBodyAnim` — idle/forward/backward/left/right/jump,
            swim/sink, edit; `test/BodyAnimTest.cs`). Attack/hold/pain/dead land with
            weapons/combat.
      - [ ] First-person view model (hudguns).
      - [x] **Third-person view weapon** attached to the body's `Weapon` bone
            (render.cpp `renderplayer` / `vweps[]`). The seven `vwep/*` models now
            convert (`tools/model-import`, including `md5dir`-relative skin/bumpmap
            resolution), and `PlayerModel.Configure(..., viewWeapon:)` parents the
            rigid model under the body skeleton's `Weapon` attachment point
            (`test/PlayerViewWeaponTest.cs`). `player_scene.tscn` attaches
            `vwep/rifle` by default; `OgzMapViewer.ViewWeapon` is a live export
            (backed by `PlayerModel.SetViewWeapon`), so the weapon can be swapped or
            cleared from the remote debugger without reloading the body, and
            `PlayerModel.ViewWeaponRotationDegrees` is exported to tune the attach.
            The real weapon selection and the `hold N`/`attack N` anims land with the
            state machine below.
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

- [x] **Authority decision** — documented; server authority is a hard requirement
      of the leaderboard (client authority ruled out for the product).
- [x] **Permission model** — a mode/config concern (editor vs game mode), enforced
      in `Sink.MayApply` (editor-commands.md §11.8).
- [x] **Prediction-scope rule** — decided: movement predicts only against the
      last acknowledged world-command sequence ([authority-and-latency.md §7](authority-and-latency.md#7-the-rule-this-forces-prediction-scope-decided)).
- [ ] **Map-hash gate** — clients and server agree on the map before play; a
      mismatch refuses the match.
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

See [architecture.md §7](architecture.md#7-modding--workshop) for the design.

- [ ] Data-first mods (Resources, ConfigFiles, our maps) — safe and easy.
- [ ] Opt-in code mods (C#) for total conversions; require trust.
- [ ] Workshop packaging: `.pck`/folder + `mod.json`, API-versioned,
      load-ordered, multiplayer-parity-checked.

## Cross-cutting: configuration

Godot-native only — `ProjectSettings`, `ConfigFile`, `Resource`, `InputMap`.
No CubeScript.

- [x] Editor/game actions are `InputMap` actions (`editor_*` in `project.godot`).
- [ ] Persistent user remaps via `user://input.cfg` (load on startup, write on
      rebind) — lands with the settings UI / M4e. Design:
      [architecture.md §6.1](architecture.md#61-keybinds-defaults-remapping-and-what-is-not-yet-bound).
- [ ] Remappable mouse gestures (editor press+release / analog scroll) — deferred.
