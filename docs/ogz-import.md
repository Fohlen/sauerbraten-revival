# OGZ Import Pipeline

One-time import of legacy `.ogz` maps into Sauerbraten Revival's native format.
This is an offline tool, not a runtime feature.

## Goals

1. Import hand-picked classic maps from the original game.
2. Extract all geometry, entities, textures, and lighting.
3. Produce our native format: octree `Resource` + `Level.tscn`.
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
│                                      │  - world.res (octree) │ │
│                                      │  - Level.tscn         │ │
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
- Per-face surface info (texture index, lightmap ID, vertex info).
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
[`tools/refharness/patches/ogz-metadata.patch`](../tools/refharness/patches/ogz-metadata.patch).
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
[`tools/refharness/patches/README.md`](../tools/refharness/patches/README.md) for
how to build the patched client.

This patch is applied once to a copy of the original source, the modified client
is run on each map, and the JSON output is committed to the repository.

**No ongoing maintenance** — the original game is frozen; we generate metadata
once for all maps we want to import.

---

## OGZ parser (C#)

Implemented in [`World/Ogz/OgzReader.cs`](../World/Ogz/OgzReader.cs), a port of
the load path in `worldio.cpp` (r6889, MAPVERSION 33). It parses the header,
variables, gameident, extras, texture MRU, entities, vslots and the full octree
(including the modern `surfaceinfo` vertex encoding). Lightmaps, PVS and the
blend map are not parsed yet.

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
5. Convert entities to Godot nodes:
   - ET_LIGHT → OmniLight3D / SpotLight3D
   - ET_PLAYERSTART → SpawnPoint (custom node)
   - ET_MAPMODEL → MeshInstance3D (load .obj/.md3)
   - ET_SOUND → AudioStreamPlayer3D
   - etc.
6. Build material manifest (index → ShaderMaterial).
7. Save:
   - world.res (octree Resource)
   - Level.tscn (scene with entities + world reference)
   - materials/ (imported materials)
```

---

## Native format (new maps)

New maps created in the editor use a **compact binary octree format** — not OGZ.

| Aspect | OGZ (legacy) | Native format |
| --- | --- | --- |
| Compression | gzip | Godot binary Resource (optionally compressed) |
| Texture refs | indices → CubeScript | indices → material manifest |
| Entities | inline, attr1-5 | Godot nodes in scene tree |
| Lightmaps | embedded | separate or baked via LightmapGI |
| Versioning | implicit | explicit version field |

The native format is:
- **Simpler**: no CubeScript dependency.
- **Godot-native**: loads via `ResourceLoader`.
- **Editable**: round-trips through the editor without loss.

---

## Lightmap handling

Options for imported lightmaps:

1. **Convert to baked textures**: Extract Sauer's packed lightmaps, assign to
   faces as a secondary UV channel.
2. **Rebake with LightmapGI**: Place lights from entity data, run Godot's bake.
3. **Dynamic lighting only**: Skip lightmaps for MVP; use real-time lights.

Recommendation: Start with (3) for MVP, add (1) or (2) for visual fidelity later.

---

## Entity type mapping

| Sauer type | ID | Godot equivalent | Status |
| --- | --- | --- | --- |
| ET_LIGHT | 1 | OmniLight3D / SpotLight3D | ✅ `LightBuilder` |
| ET_MAPMODEL | 2 | MeshInstance3D | ✅ `MapModel` (assimp → glTF Resources) |
| ET_PLAYERSTART | 3 | SpawnPoint (custom) | ✅ `PlayerStartBuilder` |
| ET_ENVMAP | 4 | ReflectionProbe | later |
| ET_PARTICLES | 5 | GPUParticles3D | planned |
| ET_SOUND | 6 | AudioStreamPlayer3D | ✅ `SoundBuilder` (bt_falls) |
| ET_SPOTLIGHT | 7 | SpotLight3D | ✅ (attaches to a light) |
| ET_TELEPORT | 19 | Teleport (trigger node) | ✅ `TriggerBuilder` (representation) |
| ET_TELEDEST | 20 | TeleDest (trigger node) | ✅ `TriggerBuilder` (representation) |
| ET_JUMPPAD | 23 | JumpPad (trigger node) | ✅ `TriggerBuilder` (representation) |
| Items | 8–18 | Item nodes | ✅ `ItemBuilder` (models converted) |
| Game-specific rest | 19–30 | mode nodes | triggers ✅, rest later |

### Entity porting status

Already ported: lights + spotlights, playerstarts, mapmodels. The remainder is being
done **one slice at a time, each verified** (the `docs/features.md` roadmap mirrors this):

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
3. **Particles** — `ET_PARTICLES` → `GPUParticles3D`, using the engine's particle
   definitions (effect id in `attr1`).
4. **Sounds** ✅ — `SoundBuilder` maps `ET_SOUND` to `SoundEmitter`
   (`AudioStreamPlayer3D`): `attr1` indexes the catalogue's `sounds` (relative to
   `packages/sounds/`), `attr2` is the audible radius, `attr3` the full-volume radius.
   .ogg/.wav are loaded from the dump and loop in the dev viewer. Verified on
   **bt_falls** (outdoors/waterfall/hum7, 5 sound entities). Runtime playback control
   is future game logic.

Later: `ET_ENVMAP`, monsters, and the remaining mode entities (flag, box, barrel,
platform, elevator, …).

### Edit-mode entity markers

In edit mode Cube2 spawns a blue `PART_EDIT` splash at every entity
(`renderparticles.cpp`). We mirror it with a **static blue billboard**
(`World/Entities/EntityMarker.cs`, texture `packages/particles/base.png`, colour
`0x3232FF`), plus a small facing arrow for directional entities. Markers are added
for **playerstarts** and **triggers** (`teleport`/`teledest`/`jumppad`), and will be
added for **items**. They are **not** added to **mapmodels** (they render their real
model) or **lights** (they have their own gizmos). The whole layer is in the
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

## File locations

```
tools/ogz-import/
├── OgzParser.cs           # Binary parser
├── MetadataLoader.cs      # JSON metadata loader
├── OctreeConverter.cs     # OGZ octree → native format
├── EntityConverter.cs     # Entities → Godot nodes
├── MaterialBuilder.cs     # Build material manifest
└── ImportPipeline.cs      # Orchestrates the import

data/metadata/
├── church51.json          # Generated metadata per map
├── dust2.json
└── ...

assets/maps/
├── church51/
│   ├── world.res          # Native octree
│   ├── Level.tscn         # Scene with entities
│   └── materials/         # Imported materials
└── ...
```

---

## Summary

1. **Metadata patch**: One-time CubeScript export → JSON for all target maps.
2. **OGZ parser**: C# binary parser for octree + entities.
3. **Importer**: Combines OGZ + metadata → native format + Godot scene.
4. **Native format**: New maps use compact binary, not OGZ.
5. **No runtime OGZ**: The importer is a build-time tool only.

This approach:
- Avoids maintaining a CubeScript interpreter.
- Decouples from the original engine at runtime.
- Allows importing any map with a one-time metadata export.
- Produces clean Godot-native output.
