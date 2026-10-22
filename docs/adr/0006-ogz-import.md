# ADR 0006: Classic Content Import (OGZ)

## Status

Accepted. Supersedes the standalone-converter plan that previously lived in the
(old) `docs/ogz-import.md`. The native map directory
([editor-and-commands.md §7](../editor-and-commands.md#7-map-format-versioned-command-history))
remains the **editor save format**; it is no longer the output of a classic-map
converter.

---

## Context

Sauerbraten's classic maps are `.ogz` octree files. They store **indices**, not
paths: a face texture is a `vslots[index]` → `slots[slot.index]` lookup, a
mapmodel entity is a `mapmodels[attr2]` lookup, and lights/sounds/particles need
attribute interpretation. Those registries are populated by the map's `.cfg`
CubeScript at load time, so they are not present in the `.ogz` file itself.

Sauerbraten Revival is a Godot 4 (.NET) port whose headline feature is a
**runtime-mutable native octree** with live in-game editing. The world is already
read with a faithful C# port of the engine's `worldio.cpp` load path
([`World/Ogz/OgzReader.cs`](../../World/Ogz/OgzReader.cs), `MAPVERSION` 33), and
the play scene already builds and plays classic maps straight from `.ogz`
(`scenes/player_scene.tscn` → `OgzMapViewer` → `MapWorld`).

The earlier plan was to offer a **converter** (a GUI wizard plus a C# importer)
that turned each classic map into the native map-directory format. That plan had
two problems:

1. **It required every user to build the patched reference client** to export the
   resolved metadata catalogue — a tedious, non-obvious toolchain step, with a
   large read-only reference dump as a prerequisite.
2. It duplicated data we can ship: the original `.ogz` maps and the resolved
   metadata are produced once and are small/stable enough to distribute.

So the converter is dropped. Instead we ship the original maps and read them at
load time.

---

## Decision

1. **Classic maps ship as original `.ogz` files** in the asset pack
   (`assets/packages/<pkg>/<map>.ogz`). Maps older than v32 are normalised to
   v33 **offline** by the reference engine, whose own decoder owns the legacy
   surface format; the revival runtime never implements that ladder.
2. **The load-time path parses OGZ in C#** with the ported reader. There is **no
   CubeScript** and **no converter**: the game builds the octree, materials and
   entities directly from the file plus the committed catalogue.
3. **Resolved metadata is committed** as `metadata-catalogue/<map>.json`,
   exported once by the patched reference client
   ([`tools/ogztool`](../../tools/ogztool/README.md)). Users never build the
   exporter; the developer regenerates the catalogue and ships it.
4. **The reference dump is dev-only.** The game and its tests do not require it to
   run from the shipped asset pack; it is only needed to regenerate the catalogue
   or normalise an old map.
5. **The native map directory stays the editor's save format.** New maps created
   in the in-game editor are saved as `world.res` + `history.res` + `Level.tscn`
   ([editor-and-commands.md §7](../editor-and-commands.md#7-map-format-versioned-command-history)).
   It is not used to bake classic maps.

### What "import" means now

"Import" is a **load-time** operation, not an offline conversion:

```
  shipped .ogz  ──▶  OgzReader (C#)  ──▶  runtime Octa + entities
        │                                            │
        └──▶  metadata-catalogue/<map>.json ─────────┘
              (slots / vslots / mapmodels / sounds / config)
```

This removes the editor-mode↔play-mode format split for classic content: the same
reader that powers the dev viewer powers the game.

---

## Architecture overview

```
┌─────────────────────────────────────────────────────────────────┐
│                    Classic content loading                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────┐    ┌──────────────┐    ┌───────────────────────┐  │
│  │  .ogz    │───▶│  OgzReader   │───▶│  Runtime Octa         │  │
│  │  (shipped)  │  │  (C#)        │    │  + entities           │  │
│  └──────────┘    └──────────────┘    └───────────┬───────────┘  │
│                                                  │              │
│  ┌──────────────────┐                            │              │
│  │ metadata .json   │────────────────────────────┘              │
│  │  (committed)     │  slots, vslots, mapmodels, sounds, config  │
│  └──────────────────┘                                           │
│                                                  ▼              │
│                                      ┌───────────────────────┐  │
│                                      │  Meshing / entities   │  │
│                                      │  / radiance hints     │  │
│                                      └───────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## The two sources

### 1. `.ogz` — binary octree + entities

- Octree structure (nodes, children, edges, faces).
- Per-face surface info (texture index, lightmap ID, vertex info — the lightmap
  link is **not** consumed; see [0002-radiance-hints.md § T1 Removal Inventory](0002-radiance-hints.md#t1-removal-inventory)).
- Entity list (position, type, attributes).
- Map header (worldsize, version, …).

The format is documented and frozen (r6889, `MAPVERSION` 33); it is parsed
directly in C#.

### 2. `metadata-catalogue/<map>.json` — resolved resources

The `.ogz` file stores indices; the catalogue stores the resolved data the map
actually uses:

- `slots` → full texture paths (diffuse/normal/glow), shader name and params.
- `vslots` → per-variant transform (`scale`/`rotation`/`offset`/`colorscale`) and
  the base variant it overlays.
- `mapmodels` → model path.
- `entities` → raw attrs, plus type/position (as stored in the map).
- `sounds` → sound path.
- `config` → the map's lighting config (`ambient`, `skylight`, `sunlight`,
  `sunlightyaw`/`sunlightpitch`/`sunlightscale`, `skyboxcolour`).
- `geometry` → ground-truth cube-tree counts, surface/vertex totals, per-vslot
  face histogram and a canonical FNV-1a checksum.

**No runtime CubeScript.** The catalogue is produced once by a CubeScript patch in
the *original* client and committed as JSON.

#### Metadata export patch

[`tools/ogztool/patches/ogz-metadata.patch`](../../tools/ogztool/patches/ogz-metadata.patch)
adds `engine/metadata.cpp` and an `exportmapmetadata()` call at the end of
`load_world()`, after the map config has run — so slot, mapmodel and sound
indices are fully resolved. When `SAUER_METADATA` points at an output file,
loading a map writes the JSON there; otherwise the patch is inert. It emits
**only** what the map uses: it walks the octree the way `compactvslots()` does
for the vslots the geometry references (and the slots those resolve to), and
resolves only the mapmodels and map sounds the entity list references — the
engine's `slots`/`mapmodels` tables are global registries populated by every
`package.cfg`/`default_map_models.cfg`, not by the map. See
[`tools/ogztool/README.md`](../../tools/ogztool/README.md).

The full test set is exported in one shot; see
[add & convert maps](../../tools/ogztool/convert-maps.md).

#### Old-format maps (pre-v32)

The reader targets **v32+**; the legacy `surfacecompat` format is deliberately
not ported. The 58 maps in the reference dump that predate v32 are normalised
offline by the same patched client: it loads each old map, **recalculates** its
lighting (regenerating the modern `surfaceinfo`/`vertinfo` link and a fresh,
self-consistent atlas set), and re-saves it as v33. The normalised maps land in
`assets/packages/` and ship with the game. `calclight` is mandatory — a
converted map saved without it would carry a stale/garbage lightmap link. The 9
v32 maps only need the material field widened (byte → ushort) and go through the
same step.

---

## OGZ parser (C#)

[`World/Ogz/OgzReader.cs`](../../World/Ogz/OgzReader.cs) is a port of the load
path in `worldio.cpp` (r6889, `MAPVERSION` 33). It parses the header, variables,
gameident, extras, texture MRU, entities, vslots and the full octree (including
the modern `surfaceinfo` vertex encoding). The lightmap atlas payload is parsed
but not used (T1); PVS and the blend map are not parsed yet.

```csharp
OgzMap map = OgzReader.Read("church51.ogz");
// map.Header, map.Vars, map.Entities, map.VSlots, map.WorldRoot
Octa world = map.ToOcta();
```

Key structures:

- **Header**: magic, version, headersize, worldsize, numents, numpvs, lightmaps,
  blendmap, numvars, numvslots.
- **Octree nodes**: recursive; internal nodes (`OCTSAV_CHILDREN`/`LODCUBE`) hold 8
  children, leaves hold 6 texture indices, edge deformation, material and (v33)
  per-face `surfaceinfo` + vertices.
- **Entities**: position (3 floats), attr1–5, type, reserved.
- **VSlots**: the map's stored variants. The engine overlays unchanged fields from
  the base variant (populated by the map `.cfg`'s texture commands) at load time,
  so only fields whose `changed` bit is set are authoritative in the file; the
  resolved values come from the metadata catalogue.

Ground truth lives in [`metadata-catalogue/`](../../metadata-catalogue/README.md)
and is asserted by [`test/OgzReaderTest.cs`](../../test/OgzReaderTest.cs): header
and entity fields, stored vslot fields, and geometry counts, per-vslot face
histogram and canonical FNV-1a checksum.

---

## Entity type mapping

| Sauer type | ID | Godot projection | Status |
| --- | --- | --- | --- |
| ET_LIGHT | 1 | OmniLight3D / SpotLight3D | ✅ `LightBuilder` |
| ET_MAPMODEL | 2 | MeshInstance3D | ✅ `MapModel` (assimp → glTF Resources) |
| ET_PLAYERSTART | 3 | SpawnPoint (custom) | ✅ `PlayerStartBuilder` |
| ET_ENVMAP | 4 | ReflectionProbe | ✅ `EnvMapBuilder` |
| ET_PARTICLES | 5 | GPUParticles3D | ✅ `ParticleBuilder`/`ParticleRenderer` (billboards; custom effects deferred) |
| ET_SOUND | 6 | AudioStreamPlayer3D | ✅ `SoundBuilder` |
| ET_SPOTLIGHT | 7 | SpotLight3D | ✅ (attaches to a light) |
| ET_TELEPORT | 19 | Teleport (trigger node) | ✅ `TriggerBuilder` (representation) |
| ET_TELEDEST | 20 | TeleDest (trigger node) | ✅ `TriggerBuilder` (representation) |
| ET_JUMPPAD | 23 | JumpPad (trigger node) | ✅ `TriggerBuilder` (representation) |
| Items | 8–18 | Item nodes | ✅ `ItemBuilder` (models converted) |
| Monsters | 21 | Monster nodes | ✅ `MonsterBuilder` (9 models converted) |
| Mode entities | 22, 24–30 | mode nodes | ✅ `ModeEntityBuilder` (flag/base/box/barrel/platform/elevator/respawnpoint/carrot) |

Every engine + fpsgame entity type is **represented**; the remaining work is
runtime game logic (collision, AI, modes), not import. The per-slice verification
history (triggers, items, particles, sounds, envmaps, mode entities, monsters)
lives in [features.md §2](../features.md#2-classic-content-ogz), which is the
live status source.

Attribute semantics come from the `fpsgame/game.h` comments plus
`entities::fixentity`/`entradius`/`entmodel`. Notable details:

- Movement attributes are **stored shifted**: `fixentity` copies `attr5=attr4`,
  `attr4=attr3` for `FLAG`/`BOX`/`BARREL`/`PLATFORM`/`ELEVATOR`, so on disk
  `attr1`=yaw, `attr2`=model/team, `attr3`=weight/tag, `attr4`=health/speed.
- `PLATFORM`/`ELEVATOR` velocity comes from `yaw` + `maxspeed=attr4` (default 8,
  sign = direction). Their simulation (`movable.cpp`) is SP-only
  (`if(!m_dmsp && !m_classicsp) return;`) and **deliberately not ported**; a
  moving platform belongs to the command-based dynamic-world plan instead.
- Monsters use `attr1`=yaw, `attr2`=type (index into the ported `monstertypes`
  table), `attr3`=tag, with the `bscale` body box and `entradius` facing arrow.
  The AI (`monster.cpp`/`ai.cpp`/`waypoint.cpp`) is game logic and lands with
  Combat/Modes.

### Edit-mode entity markers

In edit mode Cube2 spawns a blue `PART_EDIT` splash at every entity
(`renderparticles.cpp`). We mirror it with a **static blue billboard**
(`World/Entities/EntityMarker.cs`, texture `packages/particles/base.png`, colour
`0x3232FF`), plus a small facing arrow for directional entities. Markers are
added for playerstarts, triggers, items, particles, sounds, monsters and the mode
entities; **not** for mapmodels (they render their real model), lights or envmaps
(their probe has its own extent). The layer is in the `entity_markers` group so a
future game/edit-mode split can hide it via `EntityMarker.SetVisible`.

### Particles (`ET_PARTICLES`)

`makeparticles` (`renderparticles.cpp:1368`) switches on `attr1`. The billboard
effects (0/1/2/9/10/11/12/13) render as `GPUParticles3D` via `ParticleRenderer`
using the engine's textures/colours and the `regularflame`/`regularsplash`
parameters. The custom-renderer effects stay as per-effect TODOs: fireball (3),
tape/streak (4 `PT_TAPE`), meter/metervs (5/6), lightning (7), and lens flares
(32–35). No plain billboard can represent tape/text/icon/lightning/meter/flare.

---

## Lightmap handling

The engine bakes direct irradiance per lumel into embedded 512×512 atlases; that
model is **not** used. It is bound to surface topology and is incompatible with
real-time editing, so the import path drops it. The world is lit instead by a
world-space L1 SH probe volume baked from the map's geometry and entities — see
[0002-radiance-hints.md](0002-radiance-hints.md). `LightmapGI` is ruled out.

The map's resolved lighting **config** is still exported in the catalogue and is
consumed by the radiance bake — see
[0002-radiance-hints.md § 4. Bake Passes](0002-radiance-hints.md#4-bake-passes).

---

## Content import tools (offline, dev-only)

All tools read the read-only reference dump (`$SAUER_REF`) and write output that
is **not committed** (`assets/`) or the catalogues that **are**:

| Tool | Output | Purpose |
| --- | --- | --- |
| `tools/ogztool/convert-all-maps.sh` | `assets/packages/*.ogz` | Normalise pre-v32 maps to v33 offline. |
| `tools/ogztool/export-all-maps.sh` | `metadata-catalogue/<map>.json` | Export resolved metadata via the patched client. |
| `tools/model-import/import_models.py` | `assets/models/*.glb` + `models.json` | Convert mapmodels and built-in models to glTF with **assimp**, apply skins/bumps, write the model manifest. Handles `md5dir` skins, `<dds>`/`<mad:...>` tags, `mdlscale/4`, and parent-folder geometry fallback. |
| `tools/texture-import/import_textures.py` | `assets/textures/**` | Copy every texture a map references into the project, preserving relative paths. |

See [add & convert maps](../../tools/ogztool/convert-maps.md) for the end-to-end
recipe, and [`tools/ogztool/README.md`](../../tools/ogztool/README.md) for the
tool's own options.

Godot imports `assets/models/` and `assets/textures/` as Resources; the runtime
resolves maps, textures, sounds and models through the single
[`ContentRoots`](../../World/Content/ContentRoots.cs) resolver (project
`res://assets/` first, then the reference dump).

---

## Resource manifest (native / editor saves)

[`World/Save/ResourceManifest.cs`](../../World/Save/ResourceManifest.cs) and
[`World/Save/ManifestBuilder.cs`](../../World/Save/ManifestBuilder.cs) implement
the manifest used by the **native map directory** (editor saves), not the classic
import. It stores, per map:

- **Materials** (one per vslot): the face texture index, its transform
  (scale/rotation/offset), tint/glow/alpha, shader name, and the **full engine
  path** of each texture layer (diffuse/normal/glow).
- **Models**: `mapmodels` index → model path (full).
- **Sounds**: `sounds` index → sound path (full).

**Index → full path, never index → index.** The catalogue's global registries are
meaningless outside the original engine's load order, so resolved paths are baked
in once. The blob is byte-stable (magic + version, fixed field order), so the
map archive hash covers it like the octree. Asserted by
[`test/ResourceManifestTest.cs`](../../test/ResourceManifestTest.cs).

---

## Native format (editor maps)

New maps created in the in-game editor use the compact binary octree format, not
OGZ:

| Aspect | OGZ (legacy) | Native format |
| --- | --- | --- |
| Compression | gzip | Godot binary Resource (optionally compressed) |
| Texture refs | indices → CubeScript | indices → material manifest |
| Entities | inline, attr1–5 | command log → runtime projections |
| Lightmaps | embedded | **not imported** — radiance hints ([0002](0002-radiance-hints.md)) |
| Versioning | implicit | explicit version field (world blob + command ops) |

The native format is Godot-native (`ResourceLoader`), editable, versioned (the
command history *is* the version) and round-trips through the editor without
loss. See
[editor-and-commands.md §7](../editor-and-commands.md#7-map-format-versioned-command-history).

---

## Consequences

**Positive**

- No converter to build, maintain, document or ship; no tedious exporter setup
  for users.
- One load path for classic content in both the viewer and the game, so what the
  developer inspects is what ships.
- Maps can be added by copying the file and exporting its catalogue; the shipped
  asset pack is self-contained (no reference dump needed at runtime).
- The reference engine stays exactly where it belongs: an offline oracle for
  metadata, old-format normalisation, and content conversion.

**Negative / trade-offs**

- The runtime depends on the OGZ reader staying correct for the shipped maps;
  the format is frozen (r6889) so that is a bounded, well-tested dependency.
- The shipped `.ogz` files are larger and less "native" than a converted blob,
  and the catalogue must travel with each map.
- Pre-v32 maps require an offline normalisation step (reference client) before
  they can ship.

---

## Alternatives considered

- **Offline OGZ → native converter (the previous plan).** Rejected: it required
  every user to build the patched reference client to export metadata, and it
  duplicated data (converted maps) that the shipped `.ogz` + catalogue already
  provide.
- **Runtime CubeScript.** Rejected: the whole point of the revival is to drop
  CubeScript; a partial interpreter would be a large, unsafe maintenance burden.
- **Porting the pre-v32 `surfacecompat` decoder.** Rejected: the reference engine
  already owns that ladder, so normalising offline is cheaper and safer than
  reimplementing a format we only need once.

---

## Key source files

- `World/Ogz/OgzReader.cs` — binary parser (`worldio.cpp` port).
- `World/Ogz/OgzMap.cs` — `ToOcta()` + map vars/entities accessors.
- `World/Ogz/OgzGeometry.cs` — ground-truth geometry checksum.
- `World/Ogz/OgzMapViewer.cs` / `World/Ogz/MapWorld.cs` — runtime build of a classic map.
- `World/Content/ContentRoots.cs` — the single asset resolver (`assets/…`, then dump) for maps, textures, sounds and models.
- `World/Save/` — native map directory, manifest, and editor save/load.
- `metadata-catalogue/` — committed resolved metadata (ground truth).
- `tools/ogztool/` — metadata export + old-map normalisation.
- `tools/model-import/`, `tools/texture-import/` — content conversion.
