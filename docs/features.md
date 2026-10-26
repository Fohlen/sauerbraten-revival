# Features & Roadmap

**Single source of truth for the roadmap and status.** Design docs explain *how*
something works; this file says *what* we are building, *in what order*, and
*what is done*. If another doc and this file disagree about ordering, timing, or
status, this file wins — fix the other doc.

MVP target: **CTF, Rugby, Prop Hunt, and Racing**, on top of **coop edit** as
the shared sandbox layer.

Status legend: `[x]` done · `[~]` partial · `[ ]` not started / deferred.

---

## Roadmap

Order reflects intent, not schedule.

1. **World core** — native, dynamic octree (C#/.NET): structure, collision
   traversal, incremental meshing, our `Resource` format, Godot rendering. — *core done*
2. **Classic content loading** — ship original `.ogz` maps + resolved metadata and
   read them at load time; render and play. — *done* ([ADR 0006](adr/0006-ogz-import.md))
3. **Physics (movement + collision)** — `modifyvelocity`/`move`/`collide`/
   `trystepup`/`trystepdown`, validated against the original (A1–A4). — *done, zero deviation*
4. **Editor MVP** — runtime live cube editing, texturing/blending, entities,
   undo/redo, save/load. Built on the command system.
5. **Combat & spawns** — weapons/hitscan, flags, scoring, respawns.
6. **Modes** — CTF (insta/effic loadouts), Rugby, Prop Hunt, Racing, coop edit.
7. **Demos & replays** — record/playback of the movement + command stream.
8. **Community / online** — Steamworks networking, lobbies/matchmaking, Workshop, UGC.

> Rationale: movement's *velocity model* is proven, but collision can only be
> validated against the real maps the trace was captured on. So map loading
> comes before movement/collision refinement, and the editor follows.

### Post-MVP / deferred fidelity

Explicitly **out of scope for the MVP**, to be picked up after the roadmap above.
This list is the one place these are tracked; design lives in the linked docs.

- **Lighting** ([adr/0002-radiance-hints.md](adr/0002-radiance-hints.md)) — **chosen:** a
  world-space L1 SH probe volume ("radiance hints"): soft, diffuse-only, async
  full bake + incremental region rebake on edit. 
- **Renderer extras** ([rendering.md](rendering.md)) — specular maps, RenderingServer mesh streaming.
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
- [x] Material surfaces (water/lava/glass) — `MaterialMesher` + `MaterialTextures` (exact occlusion culling via `OctaEdit.VisibleFace`).
- [x] Exact `visibletris` culling (`OctaVisible.VisibleTris` wired into `OctaFaceWalker`/`OctaMesher`, including partial single-triangle faces and diagonal flip; asserted against all 18,811 unmerged baked surfaces in church51 by `test/OctaMeshTest.cs`).
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
- [x] Face merges and T-joints (done; [rendering.md](rendering.md#implementation-status)).

Remaining:

- [ ] RenderingServer mesh streaming (bypass the scene tree; [rendering.md](rendering.md#renderingserver-usage-planned-optimisation)).
- [ ] Grass.
- [ ] **Post-MVP fidelity** (see [Post-MVP / deferred fidelity](#post-mvp--deferred-fidelity)):
      lighting ([adr/0002-radiance-hints.md](adr/0002-radiance-hints.md)), envmap, specular maps,
      blend shader.
      - [x] **Texture layers** — bottom-layer geometry rendered (`LAYER_BOTTOM`, `vslot.layer`):
            `MaterialSet`/`MetadataCatalogue` carry the layer mapping, `OctaMesher` emits the
            second layer with its own material/UVs. Asserted on `bt_falls` by
            `test/LayerMeshTest.cs`.
      - [x] **Alpha/transparency** (`MAT_ALPHA` cubes): the mesher keys alpha faces to a separate
            surface (`MaterialSet.AlphaBit`), alpha never culls against opaque neighbours (both
            directions), and `AlphaMaterial` supplies a premultiplied, two-sided,
            per-`alphafront`/`alphaback` transparent shader. Asserted by `test/AlphaMeshTest.cs`.
            The top/bottom **blend** (blendmap) is still open.

## 2. Classic content (OGZ)

See [ADR 0006](adr/0006-ogz-import.md).

Done (classic maps are read at load time in both the viewer and the play scene):

- [x] Metadata export patch (`SAUER_METADATA`) + metadata for `church51`, `bt_falls` in `metadata-catalogue/`.
- [x] OGZ parser (`World/Ogz/OgzReader.cs`, MAPVERSION 33) → runtime `Cube` octree.
- [x] Dev viewer renders maps from OGZ with materials + entities (`scenes/editor_scene.tscn`).
- [x] Content importers: `tools/model-import/` (assimp → glTF + skins), `tools/texture-import/`.
- [x] Entities: lights/spotlights, playerstarts, mapmodels, triggers (teleport/teledest/jumppad),
      items, particles (billboard effects), sounds, edit-mode markers.
- [x] envmap / monsters / mode entities — all represented (import only). See
      [ADR 0006 § Entity type mapping](adr/0006-ogz-import.md#entity-type-mapping)
      for the concrete work items (runtime behaviour remains unported):
      - [x] `ET_ENVMAP` (4) → `EnvMapProbe` (`ReflectionProbe`). `EnvMapBuilder`
            resolves `attr1` radius (0 → 128, clamp 0..10000), `attr2` cubemap size
            (4..9, capped by `envmapsize` 7), `attr3` blur (1..2). Asserted by
            `test/EntityEnvMapTest.cs` on **earthstation** (38 probes). The
            sampling material side (`SHADER_ENVMAP`, `closestenvmap`) is still
            open (deferred post-MVP).
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
      `movable.cpp:90`), so **the classic modes we port (insta/effic CTF, FFA,
      coop edit) never simulate one** — and none of
      `ctf.h`/`capture.h`/`collect.h` reference it. Our own modes (Rugby, Prop
      Hunt, Racing) build moving geometry through the command stream instead.
      Only **16 of 316** base maps use these types, all SP-campaign maps
      (`mpsp10`, `crnsp1`, `skrsp1`, `level9`, …); our fixtures (`church51`,
      `bt_falls`) have zero. It is also the *wrong* mechanism for the dynamic-world
      plan: our mutation path is `WorldCommand`-based (see
      [Cross-cutting: dynamic world geometry](#cross-cutting-dynamic-world-geometry)),
      so a moving platform / crumble should be a `GameMode`/`Script` command
      stream, not a revived SP-only rigid-body subsystem. The mode entities stay
      as **representation** (models + gizmos), which is correct. Revisited only if
      we ship the SP campaign, and then via commands.
- [x] ~~**Lightmap import** (T1)~~ — **dropped**; superseded by
      [adr/0002-radiance-hints.md](adr/0002-radiance-hints.md). The T1 parsing/link artifacts
      (`OgzLightMap`, `SurfaceKey`/UV2, `WorldSnapshot` `CubeExt` v2) still exist
      and are slated for removal (adr/0002-radiance-hints.md § T1 Removal Inventory).
- [x] ~~Standalone converter: OGZ + metadata → native octree `Resource` + `history.res` + `Level.tscn`.~~
      **Dropped** ([ADR 0006](adr/0006-ogz-import.md)): classic maps ship as `.ogz`
      and are read at load time; the native directory format is the editor's save
      format. There is no GUI wizard or CLI importer.
- [x] **One-shot full metadata export** — capture all resolved engine state in the
      single ~25-min sweep (resolved shaders/slots/vslots/config) as **JSON only**;
      no binary sidecar (blendmap/PVS are already in the OGZ; lighting is baked
      at runtime — [adr/0002-radiance-hints.md](adr/0002-radiance-hints.md)). The
      exporter is **extended and validated** (`shaderType`/`smooth`/`texmask`/grass,
      `shaders[]`, `config`, vslot `linked`/`changed`); re-exporting the whole
      catalogue still pending.

Remaining:

- [ ] Parse the **blendmap** and **PVS** sections (post-MVP).
- [ ] Expand feature coverage (blends, layers) map by map.
      - [x] **Layers** — bottom-layer geometry emitted (`LAYER_BOTTOM`, `vslot.layer`) and
            asserted on `bt_falls` (`test/LayerMeshTest.cs`).

**New maps use our compact binary format only** — no OGZ at runtime.

## 3. Physics (movement + collision)

See [movement-physics.md](movement-physics.md) (and [ADR 0003](adr/0003-movement-verification.md)).

Done:

- [x] Reverse-engineer original movement constants and velocity model.
- [x] **A1** reference harness (`tools/refharness/`), **A2** trace capture (`SAUER_TRACE`), **A4** determinism gate.
- [x] Velocity model validated: **100% flat-floor** exact match, and **100.0% match** across all steps on event-free traces (A1/A3).
- [x] Port `move`/`collide`/`trystepup`/`trystepdown` + clipping/visibility machinery (`World/Physics/`).
- [x] **A3** input-replay differential on real geometry: **`session-clean.csv` 100.0% velocity, 100.0% falling, 0.0149 u max position error** (7001 steps, event-free movement, zero misses). This validates **layer 2 (collision)** — ramps, step-up/down, wall slide, `FLOORZ`/`SLOPEZ` cutoffs.
- [x] External events (teleports, jump pads, death/respawn) resynced; fixed 200 Hz `moveres` substeps.
- [x] Deterministic reference replay (`SAUER_REPLAY`, self-terminating) + reference `move()` trace (`SAUER_MOVETRACE`).
- [x] Live player on the ported physics (`MapPlayer.cs`; timing/input layers 3–4 ported).
- [x] Water + spectator-float states (`PhysEnt.InWater`/`State`, the `vel /= 8`
      entry damp, mid-body water-exit re-lookup, `CS_EDITING`/`CS_SPECTATOR`
      floating branch) — verified against the original via the A1 golden fixtures
      (`tools/refharness/golden/water_*.csv`, `float_forward.csv`) and
      `test/WaterPhysicsTest.cs`. **Crouch does not exist in Cube 2 r6889** (no
      crouch key, fixed player height) and is therefore not a port item.
- [x] All stages implemented, verified, and deviation is zero: deformed-cube slope/step-up landing timing resolved (via `-ffast-math` step-up fold + event-free recording; zero velocity/falling misses; 3/7001 label-only `PHYS_STEP_UP` flips on a 45° slope contour with 0.0000 pos/vel error).
- [x] Water, spectator-float physics states (crouch is not a Cube 2 feature —
      no crouch key and a fixed player volume; nothing to port).
- [x] `bounce()` for `ENT_BOUNCE` bodies (grenades/debris/gibs) — ported and
      wired (`OctaPhysics.Bounce`, `BounceBody`, the dynent cache + `plcollide`;
      asserted by `test/BouncePhysicsTest.cs` / `test/BounceBodyTest.cs`).
- [x] ~~Movable physics (box/barrel/platform/elevator) — `ENT_INANIMATE`,
      `moveplayer`/`moveplatform`. **Deliberately not ported**: SP-only
      (`movable.cpp:90`), unused by the classic modes we port, and superseded by the
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
      `history.res`, `Level.tscn`) + compaction + archive hash;
      `F2` save / `F3` load binds in `EditorController`. See
      [editor-and-commands.md](editor-and-commands.md#7-map-format-versioned-command-history).
- [x] Persistent user keybinds (`user://input.cfg`) — persistence implemented
      (`scripts/InputBindings.cs`); the rebind UI lands with M4e. See
      [Cross-cutting: configuration](#cross-cutting-configuration).

Remaining:

- [ ] **M4e GUI**: texture/material pickers, entity placement, keybinds via
      `InputMap` ([editor-and-commands.md §10](editor-and-commands.md#10-implementation-order-milestones)).
- [ ] Texturing, blending (layers no longer ride the lightmap alpha — that
      model is gone with T1; the blendmap must be sampled directly),
      entity placement.
- [ ] **Prefabs, recipes, cross-map paste** (M8) — `ReplaceBlock` content
      resources, rotation, recipe API.

## 5. Combat & spawns

- [x] Player model + view — third-person camera first (inspect models).
      - [x] Third-person camera toggle (`MapPlayer`, `third_person` bind, engine
            `thirdpersondistance`/`thirdpersonup`; `test/MapPlayerTest.cs`).
      - [x] **MD5 import** (`tools/model-import/import_models.py`): skeletal player
            bodies (`mrfixit`, `snoutx10k`, `ogro2`, `inky`, `captaincannon`) and
            their first-person `hudguns` (hands + weapon meshes linked by `md5link`,
            emitted as per-part GLBs). The manifest preserves per-part skins, tags,
            clips, pitch corrections and link metadata; `md5adjust` corrections are
            baked into clips. Fixed `<dds>`/`<mad:...>` tag stripping and variant-dir
            skin resolution.
      - [x] **MD5 animation parts + pitch import** (`md5animpart`/`md5pitch`): the
            manifest now carries `animParts`/`partAnims` (per-part bone masks and clip
            tables with engine fps + selection priority) and `pitch`
            (`ModelCatalog`; `test/ModelCatalogPartsTest.cs` + Python
            `tools/model-import/test_import_models.py`). The glb still bakes one
            full-body clip per name, so the runtime composes the parts (walk-while-aim)
            in the next step.
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
      - [x] Anim state machine mirroring `renderclient`: **movement + combat done**
            (`MapPlayer.SelectBodyAnim` — idle/forward/backward/left/right/jump,
            swim/sink, edit, lag, hold 1..6, attack 1..7, pain, dying, dead;
            `test/BodyAnimTest.cs`). Faithfully ports `renderclient`
            (`rendermodel.cpp:1060-1110`) and `renderplayer` (`render.cpp:133`).
      - [x] First-person hudgun display: imports the linked hand/weapon GLBs as a
            camera-local model, maps Cube2's Z-up coordinates into Godot's camera
            frame, and starts the idle clips. Placement is pending in-engine visual
            review; weapon switching (`HudgunModel.SetGun`) and attack animations
            (`HudgunModel.UpdateAnim` / `gun shoot`) wired with combat.
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
            Real weapon selection (`MapPlayer.SelectWeapon`) swaps the view weapon and
            the `hold N`/`attack N` anims drive the pose.
- [x] Hitscan weapons (insta rifle; effic loadout).
      - [x] **Weapon definitions** (`Gun`/`Guns`, `WeaponInfo`, `GameSound`,
            `WeaponCatalog` + `resources/weapons/*.tres`), locked to the engine
            `guns[]`/`vweps[]` tables by `test/WeaponCatalogTest.cs`.
      - [x] **Per-player weapon state** (`PlayerState`, `World/Game/`): the weapon
            half of `fpsstate` (`Ammo[NUMGUNS]`, `GunSelect`, `GunWait`) plus
            `respawn()`/`spawnstate()` loadouts (insta, efficiency, ffa/coop edit)
            and the `itemstats[]` base-ammo table. Modes are **data-first
            `GameModeInfo` Resources** (`GameModeCatalog`, `resources/gamemodes/*.tres`)
            keyed by a stable string id and carrying the engine `M_*` flags
            (`ModeFlags`) — no engine index, so mods can add/override modes.
            Asserted by `test/PlayerStateTest.cs`.
      - [x] **Firing / hitscan raycast and hit resolution** (`WeaponRay`,
            `WeaponHitscan`, `MapPlayer.TryAttack`): cylinder intersection
            (`LineCylinderIntersect`), Sauerbraten MT19937 spread (`CalcRayOffset`),
            world octree occlusion (`RayCube`), multi-pellet shotgun aggregation,
            shooter kickback velocity, target push impulses and `DoDamage` armour
            absorption. Asserted by `test/WeaponRayTest.cs`, `test/WeaponHitscanTest.cs`,
            and `test/MapPlayerTest.cs`.
      - [x] **Audio and shot effects** (`GameAudio`, `WeaponEffectsRenderer`): 70 hardcoded
            sounds loaded from `data/sounds.cfg` (`assets/sounds/` or reference dump fallback),
            slot/alt sampling, volume scaling, local 2D and 3D positional playback, empty-ammo
            and hit confirmation sounds. Asserted by `test/GameAudioTest.cs`.
      - [x] **Audio settings**: `sound_enabled` / `sound_volume` (0..255) persist in
            `user://settings.cfg` and are applied at startup via `GameAudio.Apply`.
            Asserted by `test/UserSettingsTest.cs`.
- [x] Projectiles & explosions (Rocket Launcher & Grenade Launcher).
      - [x] **Radial splash damage & self-push** (`radialeffect` / `explode`): distance-attenuated damage `(damage * (1 - dist / (1.5 * exprad)))`, self-damage halving (`EXP_SELFDAMDIV = 2`), self-push amplification (`EXP_SELFPUSH = 2.5`), rocket jump impulse mechanics.
      - [x] **Rocket projectiles** (`projectile`, `projsplash`): linear flight at speed 320, dynamic collision against world octree & candidate bounding cylinders, visual fireball/spark trail and impact detonation.
      - [x] **Grenade bouncers** (`bouncer`, `BounceBody`): wire existing `BounceBody.Kind.Grenade` physics to weapon firing, fuse timer (`ttl = 1500`), detonation on timeout or target contact, and bounce reflections.
- [x] Item pickups & respawns.
      - [x] **Item state and rules** (`PlayerState.CanPickup`, `Pickup`, `HasMaxAmmo`, `CheckQuad`): faithful port of `fpsgame/game.h` item mechanics (health, boost, armour absorption hierarchy, quad damage countdown, ammo caps).
      - [x] **Item manager and respawn system** (`ItemManager`, `Item`): ports `spawntime`, `delayspawn`, `canspawnitem`, `spawnitems`, and `checkitems` (12 unit pickup radius, scaling respawn delays, delayed armour/boost/quad spawns, `NoItems`/`NoAmmo` mode filtering).
      - [x] **Visual animation and audio** (`Item.UpdateVisual`): smooth sinusoidal vertical bobbing (`1 + sin(lastmillis/100 + x + y)/20`) and 36-degree/s yaw spinning (`lastmillis/10.0`), item pickup/spawn sounds (`S_ITEM*`, `S_ITEMSPAWN`, `S_PUPOUT`), and voice lines (`S_V_BOOST`, `S_V_QUAD`). Asserted by `test/ItemPickupTest.cs`.
- [x] Flags and capture logic (CTF).
- [x] Scoring, respawns, spawn selection.
- [x] HUD.

## 6. Modes

MVP modes are **CTF, Rugby, Prop Hunt, and Racing**, with **coop edit** as the
shared sandbox layer. FFA is already implemented and stays as the arena baseline.

- [x] FFA (arena baseline; not an MVP target).
- [x] **CTF** — insta and effic loadouts (both ported as `GameModeInfo` Resources).
- [ ] **Coop edit** (multiplayer editing over the command stream).
- [ ] **Rugby**.
- [ ] **Prop Hunt**.
- [ ] **Racing** — the dynamic-world showcase: crumbling walls, falling floors,
      and new geometry created through the command stream.

## 7. Demos & replays

- [ ] Record/playback the replicated stream (movement + world commands).
- [ ] Shares the networking serialization, so it lands with #7.
- [ ] **Visual A/B** against the original: play the same demo in both engines.
- [ ] Not used for A3 — demos replay quantized *outputs* and carry no `jumping`
      input, so they can't re-simulate movement. A3 uses the captured
      `SAUER_TRACE` fixtures (`tools/refharness/traces/`).

## 8. Community / online

See [authority-and-latency.md](authority-and-latency.md) — the network model is
**server-authoritative** (edits, movement, combat) with client prediction; this
diverges deliberately from Cube 2's client-authoritative model because of the
Steam leaderboard. Movement determinism is verified with zero deviation (Plan A: A1–A4 complete).

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
      - [ ] `ShotIntentPacket` & `ShotResultPacket` network codec.
      - [ ] Server `PlayerPositionBuffer` rewind manager (~500 ms history buffer).
      - [ ] Server-authoritative `WeaponHitscan.Fire` execution over rewound client view timestamps.
- [ ] Steam Workshop upload/download; content hashes and parity.
- [ ] Matchmaking, friends, achievements.

---

## Cross-cutting: dynamic world geometry

Cube 2's world is statically generated geometry; only a few entities move. We
deliberately remove that limit. All mutations are commands, so the same mechanism
powers live editing, crumbling walls / collapsing floors, scripted geometry, and
networked world changes. This is a first-class feature, not a side effect.

## Cross-cutting: modding

See [vision.md §12](vision.md#12-modding--workshop) for the design. Everything
gameplay-facing is moddable through Godot **Resources** and **GDScript**.

- [ ] Data mods (Resources, ConfigFiles, our maps) — safe and easy.
- [ ] **Script mods (GDScript)** over a versioned API for physics, weapons and
      game modes; opt in per pack (runs in-process).
- [ ] Every gameplay-facing seam (mode rules, weapon behaviour, physics overrides,
      entity logic) reachable from Resources + GDScript — no C# rebuild for a mod.
- [ ] Fidelity baseline: A1–A4 always measure the **unmodded** game.
- [ ] Workshop packaging: `.pck`/folder + `mod.json`, API-versioned,
      load-ordered, multiplayer-parity-checked.

## Cross-cutting: configuration

Godot-native only — `ProjectSettings`, `ConfigFile`, `Resource`, `InputMap`.
No CubeScript.

- [x] Editor/game actions are `InputMap` actions (`editor_*` in `project.godot`).
- [x] Lighting quality mode persists as a user setting (`lighting_mode` in
      `user://settings.cfg`; `scripts/UserSettings.cs`).
- [x] Audio enabled/volume persist as user settings (`sound_enabled` / `sound_volume`
      in `user://settings.cfg`; `scripts/UserSettings.cs`).
- [x] Look sensitivity/accel/invert/FOV persist as user settings (`sensitivity`,
      `sensitivity_scale`, `mouse_accel`, `invert_mouse`, `fov` in
      `user://settings.cfg`; applied to `MapPlayer` at spawn).
- [x] Screen settings persist as user settings (`screen_mode`, `window_width`,
      `window_height`, `vsync`, `max_fps` in `user://settings.cfg`; applied at
      startup via `ScreenSettings.Apply` — windowed / exclusive fullscreen /
      borderless fullscreen, original `scr_w`/`scr_h` range and `maxfps` cap).
      Asserted by `test/UserSettingsTest.cs`.
- [x] Persistent user remaps via `user://input.cfg` (load on startup, write on
      rebind; `scripts/InputBindings.cs`). The rebinding UI lands with M4e. Design:
      [vision.md §11.1](vision.md#keybinds-defaults-remapping-and-what-is-not-yet-bound).
- [ ] Remappable mouse gestures (editor press+release / analog scroll) — deferred.
