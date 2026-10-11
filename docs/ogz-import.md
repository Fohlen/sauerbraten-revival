# OGZ Import Pipeline

One-time import of legacy `.ogz` maps into Sauerbraten Revival's native format.
This is an offline tool, not a runtime feature.

## Goals

1. Import hand-picked classic maps from the original game.
2. Extract all geometry, entities, textures, and lighting.
3. Produce our native format: octree `Resource` + history + `Level.tscn` (refs).
4. **Never load `.ogz` at runtime** — the importer is a build-time tool.

---

## Architecture overview

```
┌─────────────────────────────────────────────────────────────────┐
│                    OGZ Import Pipeline                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────┐    ┌──────────────┐    ┌───────────────────────┐ │
│  │  .ogz    │───▶│  OGZ Parser  │───▶│  In-memory octree     │ │
│  │  file    │    │  (C#)        │    │  + entities           │ │
│  └──────────┘    └──────────────┘    └───────────┬───────────┘ │
│                                                  │             │
│  ┌──────────┐                                    │             │
│  │ metadata │────────────────────────────────────┤             │
│  │  .json   │    (texture slots, mapmodels,      │             │
│  └──────────┘     lights, entity defs)           │             │
│                                                  ▼             │
│                                      ┌───────────────────────┐ │
│                                      │  Native format        │ │
│                                      │  - Level.tscn (refs)  │ │
│                                      │  - world.res (octree) │ │
│                                      │  - history.res        │ │
│                                      │  - materials/         │ │
│                                      └───────────────────────┘ │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## Two-source approach

The importer needs information from two sources:

### 1. OGZ file (binary octree + entities)

The `.ogz` file contains:
- Octree structure (nodes, children, edges, faces).
- Per-face surface info (texture index, lightmap ID, vertex info — the lightmap
  link is not consumed; see [radiance-hints.md §9](radiance-hints.md#9-t1--lightmapgdshader-removal-plan)).
- Entity list (position, type, attributes).
- Map header (worldsize, version, etc.).

This is parsed directly in C# — the format is documented and stable.

### 2. Metadata JSON (texture/mapmodel resolution)

The `.ogz` file stores **indices**, not paths:
- Face texture → `vslots[index]` → `slots[slot.index]` → texture path.
- Mapmodel entity → `mapmodels[attr2]` → model path.
- Lights, sounds, particles → need attribute interpretation.

This metadata is **not** in the `.ogz` file — it's set by the map's `.cfg`
CubeScript, which executes in the original engine.

**Solution**: A one-time CubeScript patch that exports all resolved metadata to
JSON when a map loads in the original client.

---

## Metadata export patch

A small patch to the original Sauerbraten client that, on map load, writes:

```json
{
  "mapname": "church51",
  "worldsize": 1024,
  "slots": [
    {
      "index": 0,
      "textures": [
        { "type": "diffuse", "path": "textures/nieb/metal01.jpg" },
        { "type": "normal", "path": "textures/nieb/metal01_n.jpg" }
      ],
      "shader": "bumpworld",
      "params": { ... }
    },
    ...
  ],
  "vslots": [
    {
      "index": 0,
      "slot": 0,
      "scale": 1.0,
      "rotation": 0,
      "offset": [0, 0],
      "colorscale": [1, 1, 1]
    },
    ...
  ],
  "mapmodels": [
    { "index": 0, "path": "mapmodels/carrot/tree/tree01" },
    ...
  ],
  "entities": [
    {
      "index": 0,
      "type": "light",
      "position": [512, 512, 128],
      "attr1": 200,  // radius
      "attr2": 255,  // r
      "attr3": 240,  // g
      "attr4": 220   // b
    },
    ...
  ],
  "sounds": [
    { "index": 0, "path": "ambience/rain.ogg" },
    ...
  ]
}
```

The concrete implementation is
[`tools/ogztool/patches/ogz-metadata.patch`](../tools/ogztool/patches/ogz-metadata.patch).
It adds `engine/metadata.cpp` and an `exportmapmetadata()` call at the end of
`load_world()`, after the map config has been executed — so slot, mapmodel and
sound indices are fully resolved. It emits **only** what the map actually uses:
it walks the octree (the same traversal `compactvslots()` uses) for the vslots
the geometry references and the slots those resolve to, and resolves only the
mapmodels and map sounds the entity list references. This matters because the
engine's `slots` and `mapmodels` tables are global registries populated by every
exec'd `package.cfg` / `default_map_models.cfg`, not by the map. When the
`SAUER_METADATA` environment variable points at an output file, loading a map
writes the JSON there; otherwise the patch is completely inert. See
[`tools/ogztool/README.md`](../tools/ogztool/README.md) for how to build the
patched client.

This patch is applied once to a copy of the original source, the modified client
is run on each map, and the JSON output is committed to the repository. The
whole map set is exported in one shot by
[`tools/ogztool/export-all-maps.sh`](../tools/ogztool/README.md),
which loops the client over every `*.ogz` and writes one JSON entry per map into
[`metadata-catalogue/`](../metadata-catalogue/README.md).

**No ongoing maintenance** — the original game is frozen; we generate metadata
once for all maps we want to import.

### Old-format maps (pre-v32)

The importer targets **v32+**; the legacy `surfacecompat` format is deliberately
not ported. The 58 maps in the reference dump that predate v32 are normalised
offline by the same tool: [`tools/ogztool/convert-all-maps.sh`](../tools/ogztool/README.md)
loads each old map, **recalculates** its lighting (regenerating the modern
`surfaceinfo`/`vertinfo` link and a fresh, self-consistent atlas set), and
re-saves it as v33 into `assets/packages/`. The reference engine owns the whole legacy
decode ladder, so no such code lives in the revival runtime. The other 277 maps
are already v32+, including the 9 v32 maps (v32→v33 differs only in the cube
material field width: byte → ushort).

---

## OGZ parser (C#)

Implemented in [`World/Ogz/OgzReader.cs`](../World/Ogz/OgzReader.cs), a port of
the load path in `worldio.cpp` (r6889, MAPVERSION 33). It parses the header,
variables, gameident, extras, texture MRU, entities, vslots and the full octree
(including the modern `surfaceinfo` vertex encoding). The lightmap atlas
payload is currently parsed (T1) but is slated for removal
([radiance-hints.md §9](radiance-hints.md#9-t1--lightmapgdshader-removal-plan));
PVS and the blend map are not parsed yet.

```csharp
OgzMap map = OgzReader.Read("church51.ogz");
// map.Header, map.Vars, map.Entities, map.VSlots, map.WorldRoot (runtime Cube[8])
Octa world = map.ToOcta();
```

Key structures:
- **Header**: magic, version, headersize, worldsize, numents, numpvs, lightmaps, blendmap, numvars, numvslots.
- **Octree nodes**: recursive; internal nodes (`OCTSAV_CHILDREN`/`LODCUBE`) hold 8 children, leaves hold 6 texture indices, edge deformation, material and (for v33) per-face `surfaceinfo` + vertices.
- **Entities**: position (3 floats), attr1-5, type, reserved.
- **VSlots**: the map's stored variants. Note the engine overlays unchanged fields from the base variant (populated by the map `.cfg`'s texture commands) at load time, so only fields whose `changed` bit is set are authoritative in the file; the resolved values come from the metadata catalogue.

Ground truth lives in [`metadata-catalogue/`](../metadata-catalogue/README.md)
and is asserted by [`test/OgzReaderTest.cs`](../test/OgzReaderTest.cs): header and
entity fields, stored vslot fields, and geometry counts, per-vslot face histogram
and a canonical FNV-1a checksum.

---

## Import process

```
1. Load metadata JSON for the map.
2. Parse .ogz file → in-memory octree + raw entities.
3. Resolve texture indices → material paths via metadata.
4. Convert octree to our native format (compact binary Resource).
5. Resolve entities to their runtime representation (projections, not scene data):
   - ET_LIGHT → OmniLight3D / SpotLight3D
   - ET_PLAYERSTART → SpawnPoint (custom node)
   - ET_MAPMODEL → MapModel (imported glTF Resource; .obj/.md3 converted offline)
   - ET_SOUND → AudioStreamPlayer3D
   - etc.
   These materialize at load via `IWorld.Apply` from the entity command list —
   they are **not** serialized into `Level.tscn` (see the native format below).
5. Build the resource manifest (vslot index → full texture paths, plus models and
   sounds; §"Resource manifest" below).
6. Save:
   - world.res (octree Resource)
   - history.res (entity + geometry commands; empty for a fresh import)
   - Level.tscn (scene referencing world.res + materials)
   - materials/manifest.res (resource manifest)
```

---

## Native format (new maps)

New maps created in the editor use a **compact binary octree format** — not OGZ.

| Aspect | OGZ (legacy) | Native format |
| --- | --- | --- |
| Compression | gzip | Godot binary Resource (optionally compressed) |
| Texture refs | indices → CubeScript | indices → material manifest |
| Entities | inline, attr1-5 | command log → runtime projections |
| Lightmaps | embedded | **not imported** — lighting is baked from geometry + entities by the runtime probe volume ([radiance-hints.md](radiance-hints.md)) |
| Versioning | implicit | explicit version field (world blob + command ops) |

The native format is:
- **Simpler**: no CubeScript dependency.
- **Godot-native**: loads via `ResourceLoader`; the map is a directory of
  external resources (`Level.tscn` references `world.res`).
- **Editable**: round-trips through the editor without loss.
- **Versioned**: the command history *is* the version — see
  [editor-and-commands.md §7](editor-and-commands.md#7-map-format-versioned-command-history).

---

## Lightmap handling

The engine bakes direct irradiance per lumel into embedded 512×512 atlases; that
model is **not** used. It is bound to surface topology and is incompatible with
real-time editing, so the T1 import path is dropped. The world is lit instead by
a world-space L1 SH probe volume baked from the map's geometry and entities —
see [radiance-hints.md](radiance-hints.md) (design) and
[lighting.md](lighting.md) §1 (the original model, retained as the T1 removal
inventory). `LightmapGI` is ruled out.

The map's resolved lighting **config** (`ambient`, `skylight`, `sunlight`,
`sunlightyaw`/`sunlightpitch`/`sunlightscale`, `skyboxcolour`) is still exported
in the metadata JSON and is consumed by the radiance bake — see
[radiance-hints.md §4](radiance-hints.md#4-bake-passes).

---

## Entity type mapping

| Sauer type | ID | Godot projection | Status |
| --- | --- | --- | --- |
| ET_LIGHT | 1 | OmniLight3D / SpotLight3D | ✅ `LightBuilder` |
| ET_MAPMODEL | 2 | MeshInstance3D | ✅ `MapModel` (assimp → glTF Resources) |
| ET_PLAYERSTART | 3 | SpawnPoint (custom) | ✅ `PlayerStartBuilder` |
| ET_ENVMAP | 4 | ReflectionProbe | ✅ `EnvMapBuilder` (earthstation, 38 probes) |
| ET_PARTICLES | 5 | GPUParticles3D | ✅ `ParticleBuilder`/`ParticleRenderer` (billboards; custom effects deferred) |
| ET_SOUND | 6 | AudioStreamPlayer3D | ✅ `SoundBuilder` (bt_falls) |
| ET_SPOTLIGHT | 7 | SpotLight3D | ✅ (attaches to a light) |
| ET_TELEPORT | 19 | Teleport (trigger node) | ✅ `TriggerBuilder` (representation) |
| ET_TELEDEST | 20 | TeleDest (trigger node) | ✅ `TriggerBuilder` (representation) |
| ET_JUMPPAD | 23 | JumpPad (trigger node) | ✅ `TriggerBuilder` (representation) |
| Items | 8–18 | Item nodes | ✅ `ItemBuilder` (models converted) |
| Monsters | 21 | Monster nodes | ✅ `MonsterBuilder` (9 models converted) |
| Mode entities | 22, 24–30 | mode nodes | ✅ `ModeEntityBuilder` (flag/base/box/barrel/platform/elevator/respawnpoint/carrot) |

### Entity porting status

Ported: lights + spotlights, envmaps, playerstarts, mapmodels, triggers, items,
particles, sounds, monsters and the mode entities. Every engine + fpsgame entity
type is now represented; the slices were done **one at a time, each verified** (the
`docs/features.md` roadmap mirrors this):

1. **Triggers** ✅ — `TriggerBuilder` represents `ET_TELEPORT` (`attr1` = destination tag,
   `attr2` = model: `>0` a mapmodel, `0` the built-in teleporter, `<0` invisible; `attr3` =
   can_teleport hook, `attr4` = sound), `ET_TELEDEST` (`attr2` = tag, `attr1` = yaw,
   `attr3>0` = keep velocity) and `ET_JUMPPAD` (`(sbyte)attr3*10, (sbyte)attr2*10,
   attr1*12.5`; `attr4` = sound), and records the teleport → destination pairing. The
   runtime behaviour still needs collision + game logic, which isn't ported yet.
2. **Items** ✅ — `ItemBuilder` represents the `ET_*` pickups (shells…quad) with the
   engine's built-in models. `tools/model-import/import_models.py` now also converts
   those (`health`/`boost`/`quad` md3, `ammo/*` obj, and `armor/*` which shares the
   parent `armor/tris.md3` with a per-colour skin), plus `teleporter`/`carrot`/`checkpoint`.
3. **Particles** ✅ — `ParticleBuilder` represents `ET_PARTICLES`; the billboard
   effects (fire/smoke, steam, water, flame, snow, …) render as `GPUParticles3D`
   via `ParticleRenderer`, using the engine's particle definitions (effect id in
   `attr1`). The custom-renderer effects (fireball, tape, meter, lightning,
   flares) are left as per-effect TODOs — see the Particles section below.
4. **Sounds** ✅ — `SoundBuilder` maps `ET_SOUND` to `SoundEmitter`
   (`AudioStreamPlayer3D`): `attr1` indexes the catalogue's `sounds` (relative to
   `packages/sounds/`), `attr2` is the audible radius, `attr3` the full-volume radius.
   .ogg/.wav are loaded from the dump and loop in the dev viewer. Verified on
   **bt_falls** (outdoors/waterfall/hum7, 5 sound entities). Runtime playback control
   is future game logic.
5. **EnvMaps** ✅ — `EnvMapBuilder` maps `ET_ENVMAP` to `EnvMapProbe`
   (`ReflectionProbe`, box mode): `attr1` = radius (0 → `envmapradius` 128, clamped
   0..10000), `attr2` = cubemap size (4..9, then capped by `envmapsize` 7), `attr3` =
   blur (1..2). Verified on **earthstation** (38 probes, mixed radii). The
   `SHADER_ENVMAP` materials that *sample* a probe aren't ported yet (see
   [rendering.md](rendering.md)); this is the reflector only.
6. **Mode entities** ✅ — `ModeEntityBuilder` maps `FLAG`, `BASE`, `BOX`,
   `BARREL`, `PLATFORM`, `ELEVATOR`, `RESPAWNPOINT` and `CARROT` to `ModeEntity`
   nodes, resolving the built-in models (flag colour by `attr2` team, base
   neutral, carrot, checkpoint) and the movable's `mapmodelname(attr2)`. Applies
   the `fixentity` attribute shift and the `entradius` facing arrows. The model
   importer now also converts `flags/{neutral,red,blue}` and
   `base/{neutral,red,blue}` (via `exec`-delegating cfgs). Verified on
   **bt_falls** (5 bases, 2 flags) and **lost** (57 box, 114 barrel, 11 platform,
   13 elevator, +respawnpoints). Representation only — see the gaps below.
7. **Monsters** ✅ — `MonsterBuilder` maps `MONSTER` to `Monster` nodes:
   `attr1` = yaw, `attr2` = monster type (index into `MonsterTypes.All`, the ported
   `monstertypes` table), `attr3` = tag. Resolves the nine body models and the
   `bscale`-scaled body box (radius/eyeheight/aboveeye), and draws the `entradius`
   facing arrow. An out-of-range type falls back to type 0, matching the engine
   warning. The model importer now converts the nine monster models and their view
   weapons. Verified on
   **crnsp1** (220 monsters, all 9 types) and **lost** (235, types 0–5).
   Representation only — the AI (`monster.cpp`/`ai.cpp`/`waypoint.cpp`) is game
   logic and lands with Combat/Modes.

Every engine + fpsgame entity type is now **represented**. See the gaps below for
the runtime behaviour that is still unported.

#### Remaining gaps (detailed)

Attribute semantics are the `fpsgame/game.h` comments +
`entities::fixentity`/`entradius`/`entmodel`. Every entity type is **represented**;
what remains is runtime game logic, not import.

**`ET_ENVMAP` (4) — reflection/cubemap probe** ✅ done, see item 5 above. The
remaining fidelity gap is the material side: engine `SHADER_ENVMAP` surfaces (and
`closestenvmap` selection) are not ported, so nothing samples the probe yet.

**CTF / capture mode entities** ✅ represented, see item 6 above.

| Type | id | Attrs | Projection | Status |
| --- | --- | --- | --- | --- |
| `BASE` | 24 | `attr1` = ammo type, `attr2` = tag | `ModeEntity` + `base/neutral\|red\|blue` | ✅ representation |
| `RESPAWNPOINT` | 25 | `attr1` = yaw, `attr2` = type | `ModeEntity` + `checkpoint` | ✅ representation |
| `BOX` | 26 | `attr1` yaw, `attr2` model, `attr3` weight | `ModeEntity` + mapmodel `attr2` | ✅ representation |
| `BARREL` | 27 | + `attr4` health (default 50) | `ModeEntity` + mapmodel `attr2` | ✅ representation |
| `PLATFORM` | 28 | `attr3` tag, `attr4` speed | `ModeEntity` + mapmodel `attr2` | ✅ representation |
| `ELEVATOR` | 29 | `attr3` tag, `attr4` speed | `ModeEntity` + mapmodel `attr2` | ✅ representation |
| `FLAG` | 30 | `attr1` yaw, `attr2` team | `ModeEntity` + `flags/{red,blue,neutral}` | ✅ representation |
| `CARROT` | 22 | classic SP carrot | `ModeEntity` + `carrot` | ✅ representation |

- Movement attributes are **stored shifted**: `fixentity` copies `attr5=attr4`,
  `attr4=attr3` for `FLAG/BOX/BARREL/PLATFORM/ELEVATOR`, so on-disk `attr1`=yaw,
  `attr2`=model/team, `attr3`=weight/tag, `attr4`=health/speed. `ModeEntityBuilder`
  resolves these onto the node (weight/health/speed/tag/team/direction).
- `PLATFORM`/`ELEVATOR` velocity comes from `yaw` + `maxspeed=attr4` (default 8,
  sign = dir).
- **Runtime behaviour is not ported, by decision:** the box/barrel/platform/
  elevator simulation lives in `fpsgame/movable.cpp`, which is hard-gated to
  single-player (`if(!m_dmsp && !m_classicsp) return;`) — none of the target modes
  (CTF/FFA/coop edit) ever runs it. It is **deliberately not ported**; a moving
  platform belongs to the command-based dynamic-world plan instead. The other mode
  logic (`ctf.h`, `capture.h`) lands with Combat/Modes (roadmap items 5–6).
  (Note: these are `ENT_INANIMATE`; `bounce()` is for grenades/debris/gibs, not
  them — see [movement-physics.md](movement-physics.md).)

**Monsters (`MONSTER`, 21)** ✅ represented, see item 7 above.

- Attributes: `attr1` = yaw, `attr2` = monstertype (`0–8`), `attr3` = tag.
  `MonsterTypes.All` is a direct port of the engine's `monstertypes` table
  (gun/speed/health/freq/lag/rate/pain/loyalty/bscale/weight/name/model/vwep);
  `MonsterBuilder` applies the `bscale` body scaling (`radius *= bscale/10`,
  `eyeheight = 8·bscale/10`, `aboveeye = 7·bscale/10`). Where a type has a view
  weapon, a `MonsterViewWeapon` child node is attached (the engine hangs it off
  the body's `tag_weapon` bone).
- **Still needed for behaviour:** the AI and combat (`monster.cpp`, `ai.cpp`,
  `waypoint.cpp`) are game logic, not import — they land with Combat/Modes, as
  does the runtime vwep bone attachment/animation.

**Model import work** (`tools/model-import/`): ✅ `flags/` (md2), `base/` (md3,
`red`/`blue`/`neutral` via `exec`-delegating cfgs), the nine monster models,
the six monster view-weapons and the seven player view-weapons (`vwep/*`) now
convert, alongside item/teleporter/checkpoint/carrot. The player view weapons are
run back through `md5dir` so their skins/bumpmaps resolve from `hudguns/<gun>`
(they load their mesh from `vwep/<gun>` but their textures from the `md5dir`). The
only remaining unconverted built-ins are mapmodels referenced solely by
box/barrel/platform/elevator maps (resolved per-map through the catalogue as usual).

### Edit-mode entity markers

In edit mode Cube2 spawns a blue `PART_EDIT` splash at every entity
(`renderparticles.cpp`). We mirror it with a **static blue billboard**
(`World/Entities/EntityMarker.cs`, texture `packages/particles/base.png`, colour
`0x3232FF`), plus a small facing arrow for directional entities. Markers are added
for **playerstarts**, **triggers** (`teleport`/`teledest`/`jumppad`), **items**,
**particles**, **sounds**, **monsters** and the **mode entities** (with a facing
arrow for `monster`/`flag`/`respawnpoint`/`box`/`barrel`/`platform`/`elevator`,
matching `entradius`).
They are **not** added to **mapmodels** (they render their real model),
**lights** or **envmaps** (their probe has its own extent). The whole layer is in the
`entity_markers` group so a future game/edit-mode split can hide it via
`EntityMarker.SetVisible`.

This is a gizmo, deliberately **separate from the particle system** below.

### Particles (`ET_PARTICLES`)

`makeparticles` (`renderparticles.cpp:1368`) switches on `attr1`. Most effects are
predictable and map to billboard particles/static markers; a few are genuine
emitters (`GPUParticles3D`), and a few need custom shaders — those are left as
per-effect TODOs rather than ported now:

| attr1 | effect | mapping |
| --- | --- | --- |
| 0 | fire + smoke | ✅ `GPUParticles3D` (flame + smoke) |
| 1 | steam vent | ✅ `GPUParticles3D` |
| 2 | water fountain | ✅ `GPUParticles3D` |
| 3 | fire ball | custom (explosion) — **TODO** |
| 4 | tape / streak | custom (`PT_TAPE`) — **TODO** |
| 5, 6 | meter / metervs | custom — **TODO** |
| 7 | lightning | custom renderer — **TODO** |
| 9 | steam | ✅ `GPUParticles3D` |
| 10 | water | ✅ `GPUParticles3D` |
| 11 | flame | ✅ `GPUParticles3D` |
| 12 | smoke plume | ✅ `GPUParticles3D` |
| 13 | snow | ✅ `GPUParticles3D` |
| 32–35 | lens flares | custom (`lensflare`) — **TODO** |

**Represented** as `ParticleEmitter` nodes (`ParticleBuilder`). The billboard
effects (0/1/2/9/10/11/12/13) are rendered as `GPUParticles3D` by
`ParticleRenderer` (using the engine textures/colours and the `regularflame`/
`regularsplash` parameters). The custom-renderer effects stay as TODOs. On
church51 the effects are 8 × fire+smoke (`0`, **rendered**), 8 × fireball (`3`)
and 103 × tape (`4`) — the two custom kinds are deferred; no plain billboard can
represent tape/text/icon/lightning/meter/flare.

---

## Content import tools

The pipeline is split into offline tools, all reading the read-only reference dump
(`$SAUER_REF`) and writing **git-ignored** output under `assets/`:

| Tool | Output | Purpose |
| --- | --- |
| `tools/model-import/import_models.py` | `assets/models/*.glb` + `models.json` | Converts mapmodels and built-in item/teleporter/flag/base/monster (+view-weapon), player and **player view-weapon** (`vwep/*`) models to glTF with **assimp**, applies per-model skins/bumps, and writes the model manifest. Flat models are single-skin; skeletal **MD5** models (`mrfixit`, `snoutx10k`, `ogro2`, `inky`, `captaincannon`, their `hudguns`, the `vwep/*`) additionally emit `skeleton`, per-part `skins` (`md5skin`), `tags` (`md5tag`), and animation metadata. The 28 first-person hudgun variants are imported as linked hands/weapon parts (one GLB per mesh, `md5link` recorded); `md5adjust` is baked into animation clips, while `md5pitch` is preserved in the manifest. Handles parent-folder geometry fallback (e.g. `armor/*` sharing `armor/tris.md3`, `base/*` resolving `../base.md3` via `exec`-delegating cfgs, `snoutx10k/blue` resolving its mesh in the parent root), `md5dir` skin/bumpmap resolution (`vwep/<gun>` textures live in `hudguns/<gun>`), Cube2 `<dds>`/`<mad:...>` path tags, and the engine's `mdlscale/4`. |
| `tools/texture-import/import_textures.py` | `assets/textures/**` | Copies every texture a map references (slot textures, model skins/bumps, and the engine defaults for water/lava/particles) into the project, preserving relative paths. |
| `tools/refharness/build_instrumented.sh` | `metadata-catalogue/<map>.json` | Builds the patched reference client that exports the metadata (see above). |

Godot imports `assets/models/` and `assets/textures/` as Resources; the viewer loads
the imported Resources and falls back to the dump for anything not imported.

## Resource manifest

Implemented in [`World/Save/ResourceManifest.cs`](../World/Save/ResourceManifest.cs)
(a `Resource` wrapper around a canonical, versioned blob) and
[`World/Save/ManifestBuilder.cs`](../World/Save/ManifestBuilder.cs) (catalogue →
manifest). It is written to `materials/manifest.res` by `MapStore.Save` and read
back by `MapLevel`, which rebuilds a `MaterialSet` from the stored full paths.

Stored per map:

- **Materials** (one per vslot): the octree's face texture index, its transform
  (scale/rotation/offset), tint/glow/alpha, shader name, and the **full engine
  path** of each texture layer (diffuse/normal/glow).
- **Models**: `mapmodels` index → model path (full).
- **Sounds**: `sounds` index → sound path (full).

**Index → full path, never index → index.** The catalogue's global slot/model/
sound registries are meaningless outside the original engine's load order, so the
resolved paths are baked in once. The blob is byte-stable (magic + version, fixed
field order), so the map archive hash covers it like the octree. Asserted by
[`test/ResourceManifestTest.cs`](../test/ResourceManifestTest.cs).

## Converter GUI

A single-panel wizard imports one map at a time:
[`scenes/converter.tscn`](../scenes/converter.tscn) +
[`scripts/ConverterWizard.cs`](../scripts/ConverterWizard.cs).

1. Pick the Sauerbraten install folder (`FileDialog`); the path is remembered in
   `user://settings.cfg` (`sauerbraten/path`).
2. Pick a map from a filterable list of `<install>/packages/**/*.ogz`.
3. Convert: `OgzConverter` reads the OGZ, loads the committed
   `metadata-catalogue/<map>.json`, and writes `user://maps/<map>/` (user space,
   §7.4 of [editor-and-commands.md](editor-and-commands.md)); the panel shows the
   entity/resource counts and the archive content hash.

The CLI/headless equivalent (`OgzConverter.ConvertFile`) exists but has no
argument-parsing front end yet.

## File locations

Current (dev viewer; reads `.ogz` directly at runtime — **not** the shipping path):

```
World/Ogz/OgzReader.cs        # Binary parser + runtime octree build
World/Ogz/OgzMap.cs           # ToOcta() + map vars/entities accessors
World/Ogz/OgzGeometry.cs      # Ground-truth geometry checksum
World/Ogz/OgzMapViewer.cs     # [Tool] dev viewer (scenes/editor_scene.tscn)
metadata-catalogue/<map>.json # Committed metadata (ground truth)
tools/model-import/           # Model converter (Python)
tools/texture-import/         # Texture importer (Python)
assets/models/                # git-ignored, Godot-imported
assets/textures/              # git-ignored, Godot-imported
```

Importer (native format):

```
World/Save/OgzConverter.cs    # OGZ (+ catalogue) → native map directory
World/Save/ResourceManifest.cs# Resource manifest: resource/data type + blob codec
World/Save/ManifestBuilder.cs # catalogue → manifest; manifest → MaterialSet
World/Save/MapStore.cs        # writes world.res/history.res/Level.tscn/materials/
World/Save/MapLevel.cs        # loads a native map dir; rebuilds materials from manifest
scripts/ConverterWizard.cs    # GUI wizard (scenes/converter.tscn)

user://maps/<map>/            # Converted maps (user space, §7.4)
├── Level.tscn                # Scene: refs world.res + materials; mode config
├── world.res                 # Native octree Resource
├── history.res               # Command log (empty on import)
└── materials/
    └── manifest.res          # Resource manifest (textures/models/sounds by full path)
```

The map is a directory of external resources; its **archive** (`.zip`/`.tar`) is
the unit of distribution, hashing, and parity. See
[editor-and-commands.md §7](editor-and-commands.md#7-map-format-versioned-command-history).

---

## Summary

1. **Metadata patch**: One-time CubeScript export → JSON for all target maps.
2. **OGZ parser**: C# binary parser for octree + entities.
3. **Importer**: Combines OGZ + metadata → native format + Godot scene, with a
   full resource manifest (textures/models/sounds by full path) and a GUI wizard.
4. **Native format**: New maps use compact binary, not OGZ.
5. **No runtime OGZ**: The importer is a build-time tool only.

This approach:
- Avoids maintaining a CubeScript interpreter.
- Decouples from the original engine at runtime.
- Allows importing any map with a one-time metadata export.
- Produces clean Godot-native output.
