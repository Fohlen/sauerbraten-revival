# Lighting: Radiance Hints (L1 SH probe volume)

Design and staging for the **replacement runtime lighting path**. The world is
lit by a world-space volume of spherical-harmonic irradiance probes (the
classic "radiance hints" representation), not by imported, per-surface baked
lightmaps.

Status: **Stage 3 implemented (Stage 0, 1, 2, 3 complete).** This doc supersedes the
baked-lightmap plan in [lighting.md](lighting.md) as the chosen runtime path.
[lighting.md](lighting.md) is retained as the reference for the original
engine's model and as the *removal inventory* for T1 (see §9).

Related: [rendering.md](rendering.md) (mesher/material seams),
[features.md](features.md) (status/ordering), [ogz-import.md](ogz-import.md)
(OGZ pipeline), [architecture.md](architecture.md) §5 (staged fidelity),
[lighting.md](lighting.md) (original model + T1 removal inventory).

---

## TL;DR / decision

- **Drop crisp pre-baked world lighting.** T1 (import the original lightmap
  atlases and sample them in `lightmap.gdshader`) and T2 (`calclight` rebake) are
  incompatible with real-time world editing: a lightmap is bound to a surface's
  unwrap and atlas, so any geometry/texture/layer edit invalidates it, and
  rebaking is the densest subsystem in the engine.
- **Runtime lighting = a world-space probe volume of L1 SH irradiance.** Each
  probe stores the irradiance function's constant + directional terms, so *any*
  point and normal can be lit in O(1) by sampling the volume.
- **Soft, diffuse-only world lighting is accepted.** Probe resolution bounds the
  sharpness of direct shadows (softness ≈ cell size). No crisp contact shadows
  from the world volume; that is a deliberate trade for editability. If we later
  want sharper direct light, add a screen-space/dynamic-light term *on top* —
  never a return to baked atlases.
- **Representation: one `Rgbah` (RGBA half-float) 3D-texture texel per probe.**
  `rgb` = directional irradiance vector `c1` (colored), `a` = scalar ambient
  `c0`. Shading is `irr = skyTint*a + dot(c1, N)` clamped to ≥ 0. (Single texel
  ⇒ ambient is scalar; see §2.4 for the colored-ambient upgrade.)
- **Grid spans the map's geometry bounds**, not `worldsize`. Default **32³**
  (Stage 0); 48³/64³ are quality knobs.
- **Full bake is async on load; edits trigger an incremental region rebake**,
  budgeted exactly like the mesher's dirty chunks.
- Godot's own lights are **off for world geometry** (no double count). Entity
  lights can keep using Godot lights.

---

## 1. Why (the conflict with real-time editing)

The original engine bakes direct irradiance per lumel into shared 512×512
atlases ([lighting.md](lighting.md) §1). That model assumes the world is static:

| Edit | Lightmap impact |
| --- | --- |
| Move a cube face | its unwrap *and* every face sharing its atlas page is stale |
| Change a texture/vslot | the face's `sortkey(tex, lmid, ...)` changes; atlas repack |
| Move/add a light | `calclight` for every affected lumel (`lmaa` supersampling) |
| Change a layer/blendmap | the atlas alpha channel is the blend mask (§1.4) |

Even T2 ("rebake") is a partial `lightcache`/`patchlight` on top of a full
`calclight` port — weeks of work, and still per-lumel. A world-space probe
volume decouples lighting from surface topology: geometry can change and only
the probes whose rays the change actually altered need re-evaluating. That is
what makes live editing tractable.

The cost is softness. A probe volume cannot reproduce a crisp shadow edge; the
blur is on the order of a cell. Accepted (see TL;DR).

---

## 2. Representation

### 2.1 L1 spherical harmonics

Project incoming radiance `L(ω)` onto the L1 SH basis:

```
Y00  = 0.282095
Y1-1 = 0.488603 * ω.y
Y10  = 0.488603 * ω.z
Y11  = 0.488603 * ω.x
```

The diffuse irradiance (cosine-lobe convolved) at a surface normal `n` is

```
E(n) = 0.886227 * L00
     + 1.023327 * (L1-1 * n.y + L10 * n.z + L11 * n.x)
     = c0 + dot(c1, n)
```

with `c0 = 0.886227 * L00` (constant/ambient) and
`c1 = 1.023327 * (L11, L1-1, L10)` (directional). **Fold the cosine-lobe
constants into the bake**, so the shader is literally a dot product: no SH
basis evaluation at runtime.

### 2.2 Storage — one texel per probe

| Channel | Contents |
| --- | --- |
| `rgb` | `c1` — directional irradiance vector, signed |
| `a` | `c0` — scalar ambient irradiance |

- Godot resource: `ImageTexture3D` (`sampler3D` in a spatial shader).
- Format: `Image.Format.Rgbah` (RGBA 16-bit half float) — 8 bytes/probe, signed,
  and filterable by hardware (trilinear interpolation is the whole point).
- Linear filtering interpolates SH coefficients, which is correct for L1 (unlike
  blending *discrete* light directions, which pops at cell boundaries).

### 2.3 Evaluation

```glsl
vec3 dirv = texture(dir_tex, uvw).rgb;   // c1
float amb = texture(dir_tex, uvw).a;     // c0
vec3 irr = max(sky_tint * amb + dirv * NORMAL, vec3(0.0));
ALBEDO   = albedo * irr;
```

`c1` can be negative; `Rgbah` preserves sign, so no 0–1 remap is needed.

### 2.4 Known L1 limitation / upgrade path

One texel is 4 floats, so it cannot hold a *colored* ambient (`c0.rgb`) plus the
directional vector. v1 stores scalar `c0` and tints it by a global `sky_tint`
(correct when ambient is dominated by skylight). If colored bounce matters, add a
second `Rgbah` texture holding `c0.rgb` — the shader only gains one fetch and the
grid code is unchanged. Do **not** widen to the discrete-lobe layout in the
earlier spike notes; those directions cannot be interpolated.

---

## 3. Grid

- **Extents:** the AABB of all non-empty geometry (the walk in
  `test/RadianceBakeProbe.cs:GeometryBounds` is the reference), expanded by one
  cell, made into a **cube** of the max extent so cells are cubic.
- **Probes:** cell centers. `uvw = (world_pos - grid_min) / (grid_size)`.
- **Resolution:** cells per axis, default **32** (measured in §7). Church51's
  built region is ~640³ → 20u cells; earthstation ~1696³ → 53u cells.
- **Coordinates:** the octree/`raycube` are Sauerbraten Z-up; the world node is
  rotated −90° about X, so a Sauer point `(x,y,z)` appears in Godot world space
  at `(x, z, -y)` (`MapWorld.Centre` uses the same mapping). **Store the grid in
  Godot world space** so the shader samples by plain world position without an
  inverse model matrix (chunk `MeshInstance3D`s sit under the rotated root).
- **Probes embedded in solid geometry** are skipped (`LeafCube(...)
  .IsEntirelySolid()`); a raycast starting inside a solid returns distance 0, so
  they must be excluded, not left to bake garbage.

---

## 4. Bake passes

All passes write into the same SH accumulator per probe; a pass is a source of
radiance samples.

### 4.1 Sky + sun (Stage 1)

- Cast `R` cosine-weighted hemisphere rays (golden-angle spiral around +Z;
  `CosineHemisphere` in the Stage 0 probe). Cosine weighting matches the
  diffuse lobe, so each unoccluded ray contributes its radiance directly.
- Sky radiance comes from the catalogue `config`: `skylight`, `skyboxcolour`,
  `skytexturelight`, and optionally the `SkyBuilder` cubemap sampled per ray
  direction. The original's `calcskylight` is the reference (17 fixed rays).
- Sun: one shadow ray toward `sunlightyaw`/`sunlightpitch`; if unoccluded add
  `sunlight * sunlightscale`. This is the strongest directional term and gives
  the volume a real gradient.
- Occluded rays optionally contribute a one-bounce term (§4.3).

### 4.2 Direct entity lights (Stage 2)

- Sources: `ET_LIGHT` entities (radius = `attr1`, 0 → infinite); `ET_SPOTLIGHT`
  attaches to the nearest light (`LightBuilder.AttachSpotlights`) and adds a cone
  test.
- **Spatial index required at high light counts.** Church51 has 119 lights,
  earthstation 884, crnsp1 695. The Stage 0 measurement shows the naive
  `O(probes × lights)` distance loop dominates the direct pass on earthstation
  (50s at 128³ Debug). Build a per-chunk light list (radius-overlap) or a
  coarse light grid; query only candidate lights per probe.
- One shadow ray per candidate light. `RayCube` with mode `0` is a correct
  shadow ray; the unported `RAY_SHADOW` flag is only an early-out.

### 4.3 Indirect bounce + ambient occlusion (Stage 3)

- For each hemisphere ray that *hits* geometry, add the hit surface's albedo ×
  the sky radiance reaching it (single bounce). Surface albedo from the vslot
  tint / average texture color; fall back to a neutral gray.
- AO is implicit: a probe in a corner receives fewer unoccluded sky rays, so `c0`
  drops. Optionally expose a per-probe visibility scalar for a shader AO term.

---

## 5. Shader & material

World materials change from `StandardMaterial3D` to a `ShaderMaterial`
(`World/Materials/radiance.gdshader`) — the same kind of seam the old
`lightmap.gdshader` occupied. This is deliberately a client-side-only change;
world data does not change.

- `render_mode unshaded` and compute final color ourselves:
  `ALBEDO = albedo * max(skyTint*c0 + dot(c1, N), 0)`.
- World position in `vertex()` via
  `world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz`, passed as a `varying`
  (in `fragment()`, `VERTEX` is view-space — do not sample the volume with it).
- Keep the normal map (shade against the mapped normal) and the emission/glow
  layer; the vslot tint multiplies albedo.
- Bind the 3D texture as a **global shader uniform** (or per-material parameter)
  via `RenderingServer.GlobalShaderParameterSet`, so a single volume feeds every
  vslot material.
- Water/lava/glass (`MaterialMesher`/`MaterialTextures`) already use an unshaded
  `ShaderMaterial`; decide per-material whether to add the radiance term, but the
  world faces come first.

`MaterialBuilder.Build` becomes the place that resolves the vslot shader
material; the `lightmap` binding path is removed (§9).

---

## 6. Incremental updates (Stage 4)

The invalidation seam already exists for geometry:

- `EditorController.InvalidateRegion(sel)` computes the edit AABB and calls
  `WorldChunkRenderer.MarkDirty(aabb, margin)`.
- Add `RadianceVolume.MarkDirty(aabb)` beside it, and mirror it on the
  `MapLevel` / `EmptyMap` paths (the dev/play `MapWorld` builds chunks directly
  and does not use `WorldChunkRenderer`, so it needs its own hook).

Rules:

- **Conservative region + margin.** A probe's value depends on geometry anywhere
  along its rays, so a single edit can change lighting far away. Rebake the edit
  AABB expanded by a margin (start with one chunk) and accept the approximation;
  over-rebaking is safe, under-rebaking leaves stale light.
- **Budget per frame**, mirroring `WorldChunkRenderer.BudgetPerFrame`; queue
  dirty probe ranges and process a bounded amount each frame.
- **GPU upload:** `ImageTexture3D.Update(Array<Image>)` replaces the *whole*
  texture in Godot 4.7 (no exposed sub-region update), so rebuild the `depth`
  slice `Image`s from the CPU buffer and reupload. At 32³ `Rgbah` that is 0.5 MB;
  throttle to at most one upload per frame.
- **Async full bake** on load: the world renders unlit/ambient until the first
  volume is ready (Stage 0 shows a 2–6 s Debug full bake at 32³; treat it as a
  load-time cost, not a hitch).

---

## 7. Stage 0 findings (measured)

`test/RadianceBakeProbe.cs` (run:
`"$GODOT" --headless --path . --run-tests=RadianceBakeProbe --quit-on-finish`).
Numbers are **Debug** — GoDotTest runs under `#if DEBUG` in `Main.cs`, so a
Release measurement needs a separate probe path. Treat these as an upper bound.

| map | grid | cell | mem | ns/ray | sky | full sky bake | per-edit 128u (sky/direct) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| church51 (640³, 119 lights) | 32³ | 20u | 0.5 MB | 6.8µs | 61% | **6.2s** | **49 / 2.0ms** |
| | 64³ | 10u | 4 MB | 8.3µs | 62% | 60s | 482 / 19ms |
| | 128³ | 5u | 32 MB | — | — | 459s | 3672 / 133ms |
| earthstation (1696³, 884 lights) | 32³ | 53u | 0.5 MB | 1.9µs | 89% | **1.9s** | **0.8 / 0.5ms** |
| | 64³ | 26u | 4 MB | — | — | 14s | 6 / 3ms |
| | 128³ | 13u | 32 MB | — | — | 104s | 45 / 22ms |

Conclusions:

- **32³ is the default.** The full bake is a load-time cost (must be async);
  incremental edits are frame-budget-friendly.
- **64³ is a stretch goal**, viable only with fewer rays and an async bake
  (church51 per-edit is already 0.5s Debug). **128³ is rejected.**
- Ray cost is geometry-dependent (church51 ≈ 4× earthstation for the same code,
  denser walls → more clip-plane work); measure per map.
- Bound the grid by geometry, not `worldsize` — that alone changed church51's sky
  fraction from 98% to 61% and in-range lights from ~0 to ~1.1.
- Direct lights are cheap at church's 119 but the `O(probes × lights)` loop is
  the entire direct cost at earthstation's 884 → spatial index before Stage 2 is
  dense.
- 16 sky rays instead of 32 roughly halves the dominant cost; do that first.

---

## 8. Staged plan

| Stage | Scope | Exit |
| --- | --- | --- |
| **0 ✅** | Measurement probe; geometry bounds; grid sizing | done (`test/RadianceBakeProbe.cs`, c824776) |
| **1 ✅** | `RadianceVolume` (L1 SH bake: sky+sun), `Rgbah` 3D texture, `radiance.gdshader`, world material; dev viewer | done (`107dd14`, `20e540c`) |
| **2 ✅** | Direct `ET_LIGHT`/`ET_SPOTLIGHT` into the volume; spatial light index (`LightSpatialIndex`) | done |
| **3** | Single bounce + AO/sky visibility | A/B |
| **4** | Incremental region rebake wired to the editor dirty path (renderer + `MapWorld`) | edit within frame budget |
| **5** | Quality/perf knobs, fallback (no catalogue), retire T1 (§9), docs | full suite + Release numbers |

Each stage is one reviewable commit (AGENTS.md). Stop at every stage.

---

## 9. T1 / `lightmap.gdshader` removal plan

T1 is **replaced**, not extended. Sequencing matters: remove rendering first,
data-model second, format last, so the build and golden tests stay green.

### R1 — stop rendering through lightmaps (Stage 1 of §8)

Switch `MaterialBuilder`/`MaterialSet` world surfaces to `radiance.gdshader`.
Keep every parsing/link artifact intact. No format or hash change; the lightmap
path simply stops being bound.

### R2 — delete the render plumbing

| Artifact | Action |
| --- | --- |
| `World/Materials/lightmap.gdshader` | delete |
| `MaterialBuilder.LightmapShaderPath`, `BuildLightmapMaterial` | delete |
| `MaterialSet`: `SurfaceKey.Lmid`, `SurfaceKey.Unlit`, `_lightmapped`, `AddLightmapped`, lightmapped branch of `MaterialFor(SurfaceKey)` | revert to plain `int vslot` keying |
| `Meshing/ArrayMeshBuilder.cs`: `SurfaceKey` grouping, `TexUV2` emission | drop UV2 |
| `Meshing/OctaMesher.cs`: UV2 from `CubeExt.Verts` | keep `CubeExt` traversal only if still needed; otherwise simplify |
| `Meshing/MeshFace.cs`: `Quad(SurfaceKey, UV2)` | plain quad |
| `Meshing/MaterialMesher.cs`: `SurfaceKey.Unlit` | plain material key |
| tests `OctaLightmapMeshTest`, `LightmapMaterialTest` | delete; fold the geometry-preservation assertions into `OctaMeshTest`/`MaterialBuilderTest` |

### R3 — drop atlas parsing

| Artifact | Action |
| --- | --- |
| `Ogz/OgzReader.ReadLightmaps`, `OgzMap.OgzLightMap`, `OgzMap.Lightmaps` | delete, but **skip** `hdr.lightmaps` bytes so the reader can still reach the blendmap/PVS sections if/when parsed |
| `test/OgzLightmapTest.cs`, `test/golden/lightmaps/`, `test/WorldSnapshotTest.Church51LightmapLinkSurvivesRoundTrip` | delete |
| `ogztool` metadata exporter | keep `config` lighting fields (`ambient`/`skylight`/`sunlight`/…) — the radiance bake consumes them |

### R4 — drop the per-face link from the native format (optional, last)

`WorldSnapshot` v2 persists `CubeExt` (`lmid`/`uv`/`norm`) so baked links survive
a native save. If nothing else uses the per-face vertex/normal channel
(bump/envmap/layers are themselves unported):

- bump `WorldSnapshot.FormatVersion` to **3**, drop the `FlagSurface`/`SurfaceInfo`/
  `VertInfo` section and its `Hash()` mix;
- update `Ogz/OgzGeometry.cs` (currently hashes the lightmap link), which changes
  the geometry/parity checksum for every legacy map — call that out in
  [known-bugs.md](known-bugs.md) or the parity doc;
- update `WorldSnapshotTest` round-trip/hash expectations.

Do **not** do R4 while envmap/layer work might still want `norm`; it is a
once-only format break.

### Docs to update with the removal

`lighting.md` (banner → superseded, point here), `rendering.md` (material
pipeline + deferred list), `features.md` (lighting bullet, remaining list and T1
checklist), `ogz-import.md` §Lightmap handling, `architecture.md` §5. (All but
the `lighting.md` banner are already updated as of this doc's introduction.)

---

## 10. Verification

- **No engine oracle.** Unlike movement/collision, radiance hints have no r6889
  counterpart, so the acceptance criteria are *visual* and *performance*, not a
  golden fixture. This is a deliberate divergence from the "port and validate"
  rule; record it.
- **Visual work is collaborative** (AGENTS.md): one small change, ask the user to
  look, adjust. Do not self-judge lighting appearance.
- **Headless tests:** `RadianceBakeProbe` for cost; an SH math unit test
  (`E(n)` for a known single direction and its negation); an
  `ImageTexture3D` create/update smoke test; "dirty region rebakes exactly N
  probes"; a perf guard (probes × rays stays within budget).
- **Perf budgets:** full load bake async; per-edit rebake inside the frame budget
  at 32³; one texture upload/frame max.

---

## 11. File map

Project (planned):

| File | Role |
| --- | --- |
| `World/Lighting/RadianceVolume.cs` | grid, L1 SH accumulator, bake passes, dirty queue |
| `World/Lighting/RadianceGrid.cs` | storage, `ImageTexture3D` upload, world↔grid mapping |
| `World/Materials/radiance.gdshader` | world shader: sample volume, `c0 + dot(c1, N)` |
| `World/Materials/MaterialBuilder.cs` | build vslot `ShaderMaterial`s against the volume |
| `World/Meshing/WorldChunkRenderer.cs` | call `RadianceVolume.MarkDirty` on rebuild |
| `World/Editor/EditorController.cs` | call `RadianceVolume.MarkDirty` in `InvalidateRegion` |
| `World/Ogz/MapWorld.cs`, `World/Save/MapLevel.cs` | own/initialize the volume; async first bake |
| `test/RadianceBakeProbe.cs` | Stage 0 measurement (exists) |
| `test/RadianceVolumeTest.cs` | SH math + dirty-region rebake |

Original (r6889) reference for the bake sources: `engine/lightmap.cpp`
(`calcskylight`, `generatelumel`), `engine/physics.cpp` (`shadowray`),
`engine/octarender.cpp` (`closestenvmap`), `data/glsl.cfg` (original shaders).
Our `raycast` (`World/Physics/OctaRaycast.cs`) is the ported primitive.
