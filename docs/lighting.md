# Lighting: Lightmaps, Layers & Envmap

Reference and plan for reproducing Sauerbraten's world lighting. Everything here
is grounded in SVN r6889 (`src/engine/lightmap.cpp`, `octarender.cpp`,
`renderva.cpp`, `blend.cpp`, `physics.cpp`, `texture.cpp`, `data/glsl.cfg`) and in
our current code (`World/Ogz/`, `World/Save/`, `World/Meshing/`,
`World/Materials/`).

Status: **not implemented**. Rendering currently uses approximated dynamic
Godot lights via `World/Entities/LightBuilder.cs`. This document records the
design so we can pick it back up without re-deriving it.

Related: [rendering.md](rendering.md) (Godot rendering strategy),
[ogz-import.md](ogz-import.md) (OGZ pipeline + metadata exporter),
[features.md](features.md) (roadmap), [architecture.md](architecture.md) §staged
fidelity.

---

## TL;DR / decision

- **Never use Godot `LightmapGI`.** Our world is an `ArrayMesh` emitted by our own
  mesher, not a Godot static scene; Sauerbraten lightmaps are *direct per-lumel
  irradiance with no GI bounce*. Import the original atlases and sample them with
  our own world shader.
- **Three tiers:**
  1. **T0 — dynamic lights (have).** `LightBuilder` approximation; playable.
  2. **T1 — import baked lightmaps (cheap, high value).** Read the atlases + the
     per-face/per-vertex lightmap link that the OGZ already carries; sample in a
     custom shader. This is the "cheap insurance" slice.
  3. **T2 — rebake, envmap sampling, blend-layer rendering (expensive, deferred).**
     `calclight` is ~1,400 dense lines and needs `shadowray`; post-MVP.
- **Layers and envmap are coupled to lightmaps** (shared vertex normals + a shared
  per-surface element key). Treat them as one data-model milestone, not three.
- **No binary sidecar.** The lightmaps, blendmap and PVS are all *inside the OGZ*
  (`save_world`/`load_world`, `worldio.cpp`); duplicating them into a `.bin` is a
  second source of truth for no gain. Read them from the map. The only
  engine-required export is the **JSON catalogue**, which carries what the OGZ
  cannot: resolved texture/vslot/mapmodel/sound paths and shader/config state
  (the OGZ stores indices). See §4.
- **Export the resolved metadata in one engine pass.** The all-maps JSON sweep
  costs ~25 min, dominated by startup/load. Capture the resolved
  lighting/shader/config state so we never need a second sweep.

---

## 1. The original model

### 1.1 Lightmap atlases

Baked irradiance is stored in shared **512×512 RGB(A) atlases**, one or more per
map. `LightMap` (`engine/lightmap.h`):

```
LightMap { int type, bpp, tex, offsetx, offsety;
           PackNode packroot; uint lightmaps, lumels;
           int unlitx, unlity; uchar *data; }
```

Constants: `LM_MINW/LM_MINH 2`, `LM_MAXW/LM_MAXH 128` (per-surface lightmap
dimensions), `LM_PACKW/LM_PACKH 512` (atlas page size).

`type` (`lightmap.h`):

| Bits | Meaning |
| --- | --- |
| `LM_TYPE = 0x0F` | `0` diffuse, `1` `LM_BUMPMAP0` (color), `2` `LM_BUMPMAP1` (direction) |
| `LM_ALPHA = 1<<4` | atlas has an alpha channel (layer blend mask / alpha world) |

`bpp` is 4 when `(type & LM_ALPHA) && (type & LM_TYPE) != LM_BUMPMAP1`, else 3.

`unlitx/unlity` (optional, flag `0x80` in the file) point at a 1×1 texel holding
the ambient/skylight value for that atlas — the fallback used by the `remapunlit`
logic for unlit faces (`octarender.cpp:249`).

### 1.2 Per-surface link

The link from geometry to atlas is stored per face in `surfaceinfo` and per
vertex in `vertinfo` (`engine/octa.h`):

```
vertinfo   { ushort x, y, z, u, v, norm; }
surfaceinfo{ uchar lmid[2]; uchar verts, numverts; }
```

- `lmid[0]` = top-layer atlas; `lmid[1]` = bottom-layer atlas (for blended faces).
- `lmid < LMID_RESERVED (6)` is a **reserved constant**: `LMID_AMBIENT=0`,
  `LMID_AMBIENT1`, `LMID_BRIGHT`, `LMID_BRIGHT1`, `LMID_DARK`, `LMID_DARK1`.
  A real atlas index is `lmid - LMID_RESERVED` into `lightmaps[]`.
- `u/v` are 16-bit, normalized to the 512 atlas: texel = `u * 512 / 65536`, i.e.
  Godot UV = `u / 65536.0`.
- `norm` is an encoded smooth normal, present only when the shader is
  `SHADER_NORMALSLMS` (bump lightmaps) or `SHADER_ENVMAP` (envmap reflection).
- `numverts` carries layer flags: `LAYER_TOP (1<<5)`, `LAYER_BOTTOM (1<<6)`,
  `LAYER_DUP (1<<7)`, `LAYER_BLEND = TOP|BOTTOM`; `MAXFACEVERTS = 15`.

The mesh element key (`octarender.cpp:180`) is
`sortkey(tex, lmid, dim, layer, envmap, alpha)`, and `addcubeverts`
(`octarender.cpp:813`) computes `v.lm` from `vinfo[k].u/v` and writes the
per-vertex normal + tangent frame.

### 1.3 Runtime shader

- Bind: `renderva.cpp:1063` binds `lightmaptexs[lmid]`; `:1073` binds
  `lmid+1` (`lmdir`) for `LM_BUMPMAP0/1` pairs.
- Diffuse world shader (`data/glsl.cfg` `stdworld`/`worldshader`):
  `gl_FragColor = diffuse * lm;` where `lm = texture2D(lightmap, texcoord1)`.
- Bump world shader: samples `lmcolor` + `lmdir`, then
  `lmc.rgb = max(lmc.rgb * clamp(dot(lmlv, bump), 0, 1), ambient.xyz)`.
- Envmap reflection: `textureCube(envmap, reflect)` where `reflect` derives from
  the camera and the vertex normal, mixed in with `envscale`.

Note the atlas stored in the file is per-`LightMap`; at runtime the engine may
repack several into larger `lightmaptexs` (`batchlightmaps`/`roundlightmaptex`).
That repack is an OpenGL texture-unit optimization we do **not** need to copy.

### 1.4 Layers — the alpha channel *is* the blend mask

This is the strongest coupling and the easiest thing to miss.

- A face with a `vslot.layer` (and not `MAT_ALPHA`) generates a **second**
  lightmap: `setupsurfaces` sets `w->type |= LM_ALPHA`, `bpp = 4`, and
  `generatealpha()` (`lightmap.cpp:677`) fills the **alpha channel** by sampling
  the blendmap (`lookupblendmap`, `blend.cpp`) and/or the slot's
  `layermask`/`layermaskmode`/`layermaskscale`.
- `finishlightmap` classifies the face from that alpha:
  all-255 → `SURFACE_LIGHTMAP_TOP`, all-0 → `SURFACE_LIGHTMAP_BOTTOM`, mixed →
  `SURFACE_LIGHTMAP_BLEND` (sets `LAYER_BLEND`; vertices are duplicated,
  `LAYER_DUP`).
- The blend shader reads it directly: `gl_FragColor.a =
  texture2D(lightmap, texcoord0).a` (the `blendworld` variant in `data/glsl.cfg`).

The blendmap (`engine/blend.cpp`) is an octree of 8-bit weights (`BM_SOLID`,
`BM_BRANCH`, `BM_IMAGE` of 64×64, `BM_SCALE 1`), stored in the OGZ as a separate
section (`hdr.blendmap`, `worldio.cpp:1238`). It is the *authoring* source; the
lightmap alpha is its per-lumel bake.

> Distinction: **layer** blending uses the lightmap alpha; `alphafront`/`alphaback`
> (`MAT_ALPHA`) use the texture alpha. Both live in `sortkey.alpha`.

### 1.5 Envmap

There is no envmap bake. The cubemap is sampled live; the couplings are
structural:

- `SHADER_ENVMAP` forces the same per-vertex normal channel that
  `SHADER_NORMALSLMS` needs (`lightmap.cpp:setupsurfaces`).
- The envmap index is resolved per face by `closestenvmap(orient, co, size)`
  (`texture.cpp:2801`, called at `octarender.cpp:1076`) and stored in
  `elementset.envmap`.
- Blended faces get a **separate** `envmap2` from the layer's shader
  (`octarender.cpp:1077,1089`), so each layer can reflect a different probe.
- Water/lava/glass `materialsurface`s resolve their own envmap
  (`material.cpp:424`).

`ET_ENVMAP` entities are already imported as Godot `ReflectionProbe`s
(`World/Entities/EnvMapProbe.cs`, `EnvMapBuilder`; see
[ogz-import.md § entity porting](ogz-import.md#entity-porting-status)), but the
sampling side (`SHADER_ENVMAP`/`closestenvmap`) is open.

### 1.6 Baking

`calclight` (`lightmap.cpp`) is the expensive part, ~2,733 lines total:

- `setupsurface` — 2D unwrap of arbitrary convex quads / split planes, `lpu`
  from `lightprecision`/`lightlod`, lightmap dimension.
- `generatelumel`/`generatelightmap` — point lights with radius falloff,
  spotlight cones, sun, ambient, adaptive supersampling (`lmaa`),
  `RAY_SHADOW` rays.
- `calcskylight` — 17 fixed hemisphere rays against `skylightcolor`.
- `finishlightmap` — ambient/bright collapse (`lighterror`), `blurlms`, alpha.
- `addlight`/`findlights`, `alloclightmap`/`packlightmaps` — atlas packing
  (`PackNode`).
- `generatealpha` — blendmap + layermask bake (see §1.4).
- `shadowray` (`physics.cpp:300`, cached form `:372`) + `genclipplanes` caching.
- `lightcache` / `patchlight` — incremental rebake after edits.
- `dumplms` (`lightmap.cpp:2722`) — dumps each atlas to
  `lightmap_<map>_<i>.png`. **This is our ground-truth oracle.**

---

## 2. Godot mapping decision

We emit an `ArrayMesh` ourselves; we do not build a Godot static-lightmap scene.
Therefore:

- Import the original atlases, sample with a custom `ShaderMaterial`.
- Keep `LightBuilder` dynamic lights as the T0 approximation and as the fallback
  for maps with `hdr.lightmaps == 0`.
- Do **not** assume `StandardMaterial3D` for world surfaces long-term — lit
  surfaces need a lightmap shader. Keep the manifest→material seam open.
- Adopt the **full surface key** `(tex, lmid, layer, envmap, alpha)` in the mesher
  now (only `tex` non-default today). Retrofitting it later is a mesher rewrite.
- Keep vertex arrays extensible: room for `UV2` (lightmap) and a smooth normal /
  per-vertex atlas index.

---

## 3. T1 — import plan

### 3.1 OGZ reader (`World/Ogz/OgzReader.cs`)

Already parsed: `surfaceinfo` (`Lmid0/Lmid1/NumVerts`, `LAYER_*`) and `vertinfo`
including `U/V/Norm`. `OgzMap.Header.Lightmaps` is read. The reader stops **before**
the atlas payload.

Add, after the octree (mirror `worldio.cpp:1217–1233`):

```csharp
for (int i = 0; i < header.Lightmaps; i++) {
    byte type = r.ReadByte();                 // bit 0x80 = unlit texel present
    ushort unlitx = 0, unlity = 0;
    if ((type & 0x80) != 0) { unlitx = r.ReadUInt16(); unlity = r.ReadUInt16(); }
    int lmType = type & 0x0F;                 // 0 diffuse, 1 bump0, 2 bump1
    int bpp = (type & 0x10) != 0 && lmType != 2 ? 4 : 3;
    byte[] data = r.ReadBytes(bpp * 512 * 512);   // LM_PACKW * LM_PACKH
    // -> LightMapData { Type, Bpp, UnlitX, UnlitY, Pixels }
}
```

Ordering: lightmaps come **before** PVS and the blendmap in the stream
(`worldio.cpp:1237–1238`). Version <32 maps use the legacy surfacecompat path
plus `fixlightmapnormals`/`fixrotatedlightmaps`; the reader rejects them today, so
T1 covers v32/33.

### 3.2 Preserve the link in the native format (`World/Save/WorldSnapshot.cs`)

**This is the substantive change.** `WorldSnapshot` currently documents "No render
data — `CubeExt` … is derived … not stored" and `WriteCube` persists only
faces/textures/material/merged/visible. But baked lightmaps are **not derivable**;
`OgzConverter` discards `CubeExt` (which holds `lmid` + `vertinfo.u/v/norm`).

Add an optional per-cube surface section:

```csharp
const byte FlagSurface = 1 << 4;   // baked lightmap / editor verts present
// in WriteCube: if (c.Ext != null && HasLightmappedSurface(c.Ext)) flags |= FlagSurface;
// then write CubeExt.Surfaces[6] + CubeExt.Verts[]
```

- Bump `FormatVersion` to 2.
- Add the lightmap fields to `Hash()` (they are semantic map content).
- Reserved `lmid` (0–5) and `LAYER_*` flags need explicit handling.

### 3.3 Atlas assets (`World/Save/MapStore.cs`, `Level.tscn`)

Write atlases alongside `world.res`:

- **One `Texture2D` per atlas** (`lightmaps/lm_000.res`, …), or
- **One `Texture2DArray`** (`lightmaps.res`) — preferred: one material per vslot,
  per-vertex `CUSTOM0` = slice index, avoids surface explosion.

If one texture per `LightMap`, `offsetx/offsety` are 0 (they only exist for the
runtime repack). `WriteLevelScene` references the set; `MapStore.ContentHash` must
hash it (currently hashes the `materials/` dir the same way).

**Parity nuance:** include atlases + `lmid`/UV in the archive/content hash
(Workshop identity). Decide explicitly whether atlas *pixels* enter the
simulation/parity hash; `lmid`/UV should (semantic), pixels probably only in the
directory hash.

### 3.4 Mesher + material

- `World/Meshing/ArrayMeshBuilder.cs` keys surfaces on `vslot` only and emits one
  UV set. Extend the key to the full `sortkey` and add `Mesh.ArrayType.TexUV2`
  from `VertInfo.U/V`.
- `World/Materials/MaterialBuilder.cs` builds `StandardMaterial3D`; lit surfaces
  need a custom world shader (`albedo * texture(lightmap, UV2)`, plus `lmdir`/
  `lmcolor` + vertex normal for `LM_BUMPMAP0/1`).
- Introduce the smooth-normal channel here — it serves both bump lightmaps and
  envmap (`World/Meshing/OctaFaceWalker.cs`, `MeshFace.cs`, `OctaMesher.cs`).

---

## 4. Export everything in one pass

The metadata sweep runs the original engine once per map (~25 min for the whole
set); the cost is startup + `load_world`, not JSON size. Capture **all resolved
state now** so we never re-run.

The engine patch is `tools/ogztool/patches/ogz-metadata.patch` (adds
`engine/metadata.cpp` + an `exportmapmetadata()` call at the end of `load_world`).
It runs after `startmap()`, so map `.cfg` overrides are resolved.

### 4.1 Output shape

Per map, one `exportmapmetadata()` call writes **one file**:

- `metadata-catalogue/<map>.json` — small, diffable, resolved metadata.

There is **no binary sidecar.** Lightmaps, blendmap and PVS are already inside the
OGZ (`worldio.cpp` writes them in `save_world`, reads them in `load_world`); a
second copy would duplicate map data and create a second source of truth. Parse
them from the map when needed (extend `World/Ogz/OgzReader.cs`), not from a
sidecar. The JSON is the only engine-required export, because it carries what the
OGZ structurally cannot: resolved *indices → asset paths* (the map stores only
slot/vslot/mapmodel/sound indices, which need the engine's exec'd configs to
resolve) plus resolved shader/config state.

### 4.2 Inventory

**Exported** (`metadata.cpp`): mapname/worldsize, geometry stats + FNV
checksum + `textureFaces`, used `slots`, used `vslots`, used `shaders`, mapmodels,
entities, resolved `config`, sounds. Keep used-only. Validated on `earthstation`
(1414 vslots, 98 `normalslms` + 1 `envmap` slots, 8 shaders, 30 config fields) and
`abbey`/`bt_falls` (grass, smooth).

`slots` fields: `index`, `textures` (type/path), `shader`, `shaderType`,
`smooth`, `texmask`, `grass`, `layermask` (`name`/`mode`/`scale`), `params`.

| Field | Source | Why | Status |
| --- | --- | --- | --- |
| `shaderType` | `s.shader->type` | `normalslms`/`envmap`/`option`/`deferred`/`invalid` — removes name→flags guesswork | ✅ |
| `smooth` | `s.smooth` | normal reconstruction / rebake | ✅ (bt_falls) |
| `texmask` | `s.texmask` | which `TEX_*` the shader samples | ✅ |
| `layermask` | `layermaskname`/`mode`/`scale` | layer blending (the baked alpha is in the lightmap pixels; this is the *authoring source* name) | ✅ (unset on all classic maps — no `.cfg` invokes it) |
| `grass` | `s.autograss` | grass texture path | ✅ (abbey) |
| all `sts` types | `s.sts[]` | decal/normal/glow/spec/depth/alpha/envmap paths | ✅ |

`vslots` fields: `index`, `slot`, **`linked`**, **`changed`** (change mask =
authoritative fields), scale/rotation/offset/scroll/layer/alpha/color/glow/params.

**New `shaders[]`**: `name`, `type`, `defaultparams`, `detail`/`variant`/`alt` chain.

**New `config`** — resolved idents read via `getident()` after the map cfg exec'd
(so per-map overrides are included). Read generically, so a missing name is
simply omitted rather than breaking the JSON:

- Lighting: `ambient`, `skylight`, `sunlight`, `sunlightscale`, `sunlightyaw`,
  `sunlightpitch`, `skytexturelight`, `lightprecision`, `lighterror`,
  `bumperror`, `lightlod`, `lmshadows`, `lmaa`, `blurlms`, `blurskylight`,
  `adaptivesample`, `edgetolerance`.
- Sky/atmo: `skybox`, `skyboxcolour`, `cloudbox`, `cloudboxcolour`,
  `cloudlayer`, `cloudcolour`, `fog`, `fogcolour`, `fogdomecolour`.
- Materials (colours only): `watercolour`, `waterfallcolour`, `lavacolour`.
- Meta: `maptitle`.

> **Not** in `config`, deliberately: `matwater`/`matlava`/`matglass`/
> `matnoclip` are material-texture *slot commands*, not idents — export them from
the slot registry if needed, not via `getident()` (which correctly returns
nothing). `mapauthor`/`atmo*` are not idents in r6889.

**Entities** (optional, later): resolved `attached` (spotlight→light), model
path, sound path, decoded attrs.

### 4.3 Why no binary sidecar

The binary payload — lightmaps, blendmap, PVS — is **already in the OGZ**:

| Data | Where in the map | Parser |
| --- | --- | --- |
| Lightmaps | after the octree, before PVS (`worldio.cpp:1219`) | `save_world` writes, `load_world` reads |
| PVS | after the lightmaps (`worldio.cpp:1237`) | `savepvs` / `loadpvs` |
| Blendmap | last section (`worldio.cpp:1238`) | `saveblendmap` / `loadblendmap` |

So there is nothing to capture: the reference dump and `assets/packages/` already
hold every byte, permanently. A `.bin` sidecar would (a) duplicate map data,
(b) become a second source of truth that can drift from the map, and (c) add a
format to version and hash for no capability gain.

`OgzReader` currently stops after the octree; the right fix for any consumer is
to **extend the reader** to parse the PVS/blendmap/lightmap sections it needs.
(T1 reads the lightmap payload this way — see §3.1.) The layer-mask *authoring*
image is an ordinary texture asset in `packages/`, referenced by name in the JSON
catalogue, not a sidecar blob.

### 4.4 Patch shape

The engine patch stays small: `exportmapmetadata()` writes only the JSON.

- `SAUER_METADATA` selects the output path; `SAUER_METADATA_EXIT` quits after.
- Keep `metadata.cpp` compiled with `-fno-finite-math-only` (the `jsonfloat`
  NaN/Inf guard; see the patch's Makefile hunk).
- Add `"schemaVersion"` to the JSON so the importer can negotiate fields without
  a version break.
- Adding a field later is cheap and textual — if we miss one, re-run the exporter
  for that map; there is no large binary to re-bake.

---

## 5. T2 — rebake (deferred)

Needed only once editing / new maps are the primary path (the classic maps come
from T1). Scope:

- Port the `lightmap.cpp` generation core (~1,400 lines): `setupsurface`,
  `generatelumel`/`generatelightmap`/`finishlightmap`, `calcskylight`,
  `findlights`/`addlight`, `alloclightmap`/`packlightmaps`.
- Port `shadowray` + `ShadowRayCache` (`physics.cpp:300/372`) and
  `genclipplanes` caching (`OctaRaycast`/`OctaClipping` are close).
- Port `blend.cpp` + `generatealpha`/layermask for layers.
- Replace the SDL worker/task system with .NET Tasks (not a fidelity issue).
- Integrate with the command system: partial rebake after `WorldCommand` edits =
  `lightcache`/`patchlight`; hook into `WorldChunkRenderer`'s dirty path.

Estimate: a few weeks; one of the densest subsystems in the engine.

---

## 6. Verification

- **A/B oracle:** run `dumplms` in the reference client, compare each
  `lightmap_<map>_<i>.png` against the atlas bytes our importer reads.
- Extend `test/MapFormatTest.cs` (currently
  `OgzConverterRoundTripsChurch51Geometry` — geometry only) with
  `lmid`/UV/normal round-trip assertions and a reserved-`lmid` + bump-pair case.
- The OGZ itself is the single source; `dumplms` is the independent oracle (it is
  the engine's own dump of the same data, not a second export of ours).

---

## 7. File map

Original (`src/`):

| File | Role |
| --- | --- |
| `engine/lightmap.cpp` | bake + alpha + `dumplms`; `lightmap.h` for structs |
| `engine/octa.h` | `vertinfo`, `surfaceinfo`, `LAYER_*`, `LMID_*` |
| `engine/octarender.cpp` | element key, `addcubeverts`, `closestenvmap`, `remapunlit` |
| `engine/renderva.cpp` | binds `lightmaptexs[lmid]` / `lmid+1` |
| `engine/blend.cpp` | blendmap octree + mask lookup |
| `engine/physics.cpp` | `shadowray` + cache |
| `engine/texture.cpp` | `closestenvmap`; `texture.h` for `Slot`/`VSlot`/`Shader` |
| `engine/worldio.cpp` | atlas/blendmap/PVS load (`:1217`, `:1238`), save (`:975`) |
| `data/glsl.cfg` | world shaders: diffuse×lightmap, `lmcolor`/`lmdir`, envmap, blend |

Project:

| File | Change |
| --- | --- |
| `World/Ogz/OgzReader.cs` | read atlas payload (then PVS/blendmap when a consumer needs them) |
| `World/Ogz/OgzMap.cs` | hold lightmaps |
| `World/Ogz/MapRoots.cs` | map path resolution (`assets/packages` → dump) |
| `World/Save/WorldSnapshot.cs` | preserve `CubeExt`; v2; hash |
| `World/Save/MapStore.cs` | atlas assets + content hash |
| `World/Save/OgzConverter.cs` | emit atlases (and manifest) |
| `World/Meshing/ArrayMeshBuilder.cs` | full key + `UV2` |
| `World/Meshing/{OctaFaceWalker,MeshFace,OctaMesher}.cs` | normal channel |
| `World/Materials/MaterialBuilder.cs` | lightmap shader vs `StandardMaterial3D` |
| `World/Materials/MetadataCatalogue.cs` | parse new JSON fields |
| `World/Entities/{LightBuilder,EnvMapProbe}.cs` | T0 fallback; envmap sampling |
| `tools/ogztool/patches/ogz-metadata.patch` | resolved metadata JSON (no binary sidecar) |

---

## 8. Sequencing

T1 (import) comes before T2 (rebake): import the original atlases first — cheap,
and it locks the vertex/surface contract — then rebake last (needs `shadowray`).
