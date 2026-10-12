# ADR 0001: Baked Lightmaps, Layers & Envmap

## Status

**Superseded** by [ADR 0002: Radiance Hints](0002-radiance-hints.md).

Runtime rendering uses an L1 SH probe volume rather than imported per-surface lightmaps, as pre-baked lightmaps are fundamentally incompatible with real-time octree geometry editing.

This document is retained as a concise technical reference for Cube 2: Sauerbraten's original lightmap model (SVN r6889) and as context for the T1 lightmap removal inventory in ADR 0002.

---

## Context & Original Architecture

Sauerbraten r6889 bakes direct per-lumel irradiance with no GI bounce into shared atlases (`engine/lightmap.cpp`, `octarender.cpp`, `renderva.cpp`). World geometry links to these atlases via per-face and per-vertex structures.

### 1. Lightmap Atlases
- Stored as shared **512×512 RGB(A)** pages (`LM_PACKW` × `LM_PACKH`).
- Per-surface lightmap allocation size ranges from $2\times2$ (`LM_MINW`/`LM_MINH`) to $128\times128$ (`LM_MAXW`/`LM_MAXH`).
- Header flags per atlas:
  - `LM_TYPE (0x0F)`: `0` = diffuse, `1` = `LM_BUMPMAP0` (color), `2` = `LM_BUMPMAP1` (direction).
  - `LM_ALPHA (0x10)`: atlas carries an alpha channel (4 bpp), used as a blend mask for layers.
  - `0x80`: indicates presence of unlit 1×1 fallback texel coordinates (`unlitx`, `unlity`) storing ambient/skylight.

### 2. Geometry Links (`surfaceinfo` and `vertinfo`)
- `vertinfo`: `ushort x, y, z, u, v, norm`
  - Normalized atlas UVs: $\text{UV} = \frac{(u, v)}{65536.0}$.
  - `norm`: encoded smooth normal (present for bump lightmaps and envmaps).
- `surfaceinfo`: `uchar lmid[2]; uchar verts, numverts;`
  - `lmid[0]`: primary/top-layer atlas index.
  - `lmid[1]`: bottom-layer atlas index (for blended faces).
  - Values $< 6$ (`LMID_RESERVED`) are engine constants: `LMID_AMBIENT (0)`, `LMID_AMBIENT1`, `LMID_BRIGHT`, `LMID_BRIGHT1`, `LMID_DARK`, `LMID_DARK1`. Real atlas indices are `lmid - 6`.
  - `numverts` carries layer flags: `LAYER_TOP (0x20)`, `LAYER_BOTTOM (0x40)`, `LAYER_DUP (0x80)`.

### 3. Layers & Envmap Couplings
- **Layer blending:** Faces with `vslot.layer` generate a second lightmap with `LM_ALPHA`. The alpha channel is baked from the blendmap (`engine/blend.cpp`) or layer masks. The original shader blends layers directly using `texture2D(lightmap, texcoord0).a`.
- **Envmap:** `ET_ENVMAP` entities are live cubemap probes resolved per-face via `closestenvmap(orient, co, size)`. Shaders sampling envmaps require the per-vertex smooth normal channel.

### 4. Storage in OGZ
All lighting data is stored directly in the `.ogz` file:
1. Octree
2. Lightmap atlases (`hdr.lightmaps` count, header flags, raw pixel data)
3. PVS (`savepvs`/`loadpvs`)
4. Blendmap (`saveblendmap`/`loadblendmap`)

Original maps require no binary sidecar; only metadata (slot texture paths, vslot parameters, shader definitions, and cfg variables) needs extraction via the JSON metadata catalogue.

---

## Evaluation & Supersession Rationale

The original plan evaluated three tiers:
- **T0**: Dynamic Godot lights (`LightBuilder`) as an initial approximation.
- **T1**: Importing baked lightmaps from OGZ and sampling in custom shaders.
- **T2**: Porting the original CPU baker (`calclight`, ~2,700 LOC, shadow ray tracing) for in-game edits.

### Why Baked Lightmaps Were Abandoned
1. **Dynamic World Incompatibility:** In-game real-time octree mutation (coop edit, crumbling geometry) invalidates per-face unwraps, atlas packing, and neighboring lumels.
2. **Rebaking Cost:** Porting `calclight` requires complex 2D quad unwrapping and raytracing pipelines that cannot run within interactive frame budgets during editing.
3. **Godot Limitations:** Godot's `LightmapGI` expects static scenes, whereas Sauerbraten Revival generates dynamic `ArrayMesh` surfaces from an octree.

**Resolution:** Replaced by [ADR 0002](0002-radiance-hints.md) with an L1 Spherical Harmonics 3D probe volume (Radiance Hints), providing $O(1)$ surface-independent irradiance evaluation and fast local updates.

---

## Key Source Files (SVN r6889 Reference)

| Original File | Role |
| --- | --- |
| `src/engine/lightmap.h` | `LightMap`, `PackNode`, `LM_*` constants |
| `src/engine/lightmap.cpp` | `calclight`, surface unwraps, atlas packing, `dumplms` |
| `src/engine/octa.h` | `vertinfo`, `surfaceinfo`, `LAYER_*`, `LMID_*` |
| `src/engine/octarender.cpp` | Surface element keys, `addcubeverts`, `remapunlit` |
| `src/engine/renderva.cpp` | Texture unit binding for diffuse and bump lightmap pairs |
| `src/engine/blend.cpp` | Blendmap octree and layermask generation |
| `src/engine/worldio.cpp` | Lightmap, blendmap, and PVS serialization |
| `data/glsl.cfg` | Original world shaders (`stdworld`, bump variants, `blendworld`) |

