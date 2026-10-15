# ADR 0002: Radiance Hints (L1 SH Irradiance Probe Volume)

## Status

Accepted. Stage 4 implemented (Stages 0–4 complete; Stage 5 remaining).
Supersedes the baked-lightmap design in [docs/adr/0001-lighting.md](0001-lighting.md).

## Context

Sauerbraten SVN r6889 bakes direct irradiance per lumel into shared 512×512 atlases (`engine/lightmap.cpp`). This model assumes static geometry:
- Moving cube faces invalidates their unwraps and any faces sharing the atlas page.
- Changing textures/vslots alters sort keys and requires atlas repacking.
- Adding or moving lights triggers `calclight` over all affected lumels.
- Blendmaps/layers rely on the atlas alpha channel as a blend mask.

In Sauerbraten Revival, real-time in-game world editing (coop edit, runtime geometry mutation) is a core requirement. Rebaking lightmaps via a ported `calclight` is prohibitive at runtime and couples lighting to surface topology. Furthermore, Godot's built-in `LightmapGI` cannot be used because world geometry is dynamically generated into an `ArrayMesh` from a mutable octree.

## Decision

1. **Abandon per-surface baked lightmaps:** T1 (importing original lightmap atlases) and T2 (`calclight` port) are discarded for runtime rendering.
2. **Adopt world-space L1 Spherical Harmonics (SH) probe volume (Radiance Hints):**
   - Decouples lighting from surface unwraps and mesh topology.
   - Enables O(1) lighting evaluation per vertex or fragment for arbitrary positions and normals.
   - Supports incremental localized rebaking when geometry or lights change.
3. **Accept soft, diffuse-only world lighting:** Direct shadow softness is bounded by cell size (~20–50 units). Crisp contact shadows from the static world are traded away for runtime editability.
4. **World geometry ignores Godot lights:** World surfaces are unshaded by Godot's standard lighting pipeline; entity lights are baked into the probe volume.

## Technical Details

### 1. Representation & SH Mathematics

Incoming radiance $L(\omega)$ is projected onto the L1 SH basis:
```
Y00  = 0.282095
Y1-1 = 0.488603 * ω.y
Y10  = 0.488603 * ω.z
Y11  = 0.488603 * ω.x
```

Diffuse irradiance (cosine-lobe convolved) at surface normal $n$ is:
$$E(n) = 0.886227 \cdot L_{00} + 1.023327 \cdot (L_{1,-1} n_y + L_{1,0} n_z + L_{1,1} n_x) = c_0 + c_1 \cdot n$$
where $c_0 = 0.886227 \cdot L_{00}$ (scalar ambient) and $c_1 = 1.023327 \cdot (L_{1,1}, L_{1,-1}, L_{1,0})$ (colored directional vector).

Cosine-lobe constants are folded directly into the probe accumulation during baking, leaving the runtime shader evaluation as a single dot product:
$$E(n) = \max(\text{sky}_\text{tint} \cdot c_0 + c_1 \cdot n, 0)$$

### 2. GPU Storage & Shader Interface

- **Texture format:** A single 3D texture (`ImageTexture3D`) using `Image.Format.Rgbah` (RGBA 16-bit half-float, 8 bytes/probe).
  - `rgb`: Directional irradiance vector $c_1$ (signed, colored).
  - `a`: Scalar ambient irradiance $c_0$.
- **Filtering:** Hardware trilinear filtering natively interpolates L1 SH coefficients across space without boundary popping.
- **Shader (`radiance.gdshader`):**
  - Uses `render_mode unshaded`.
  - World position is computed in `vertex()`: `world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz`.
  - Evaluated against tangent-space normal maps:
    ```glsl
    vec3 dirv = texture(dir_tex, uvw).rgb;
    float amb = texture(dir_tex, uvw).a;
    vec3 irr  = max(sky_tint * amb + dirv * NORMAL, vec3(0.0));
    ALBEDO    = albedo * irr;
    ```
- **Uniform binding:** Bound globally via `RenderingServer.GlobalShaderParameterSet` to feed all vslot materials.

### 3. Grid Topology & Coordinate Space

- **Bounding Box:** Computed from the AABB of non-empty geometry (`GeometryBounds`), expanded by one cell margin, and cubified (equal dimensions along all axes) to guarantee cubic cells.
- **Probe coordinates:** Probes sit at cell centers:
  $$\text{uvw} = \frac{\text{world}_\text{pos} - min(\text{grid})}{|grid|}$$
- **Coordinate transformation:** The octree is Sauerbraten Z-up, while Godot nodes are rotated −90° about X. Sauerbraten $(x, y, z)$ maps to Godot $(x, z, -y)$. The grid is stored directly in Godot world space so shaders sample using unmodified world coordinates.
- **Solid rejection:** Probes embedded within solid cubes (`LeafCube.IsEntirelySolid()`) are excluded from raycasting and baking to prevent sampling artifacts.

### 4. Bake Passes

All bake passes accumulate into the probe's L1 SH coefficients:

1. **Sky & Sun (Stage 1):**
   - **Sky:** Casts $R$ cosine-weighted hemisphere rays (golden-angle spiral around $+Z$). Unoccluded rays sample map sky parameters (`skylight`, `skyboxcolour`, `skytexturelight`, or cubemap).
   - **Sun:** Single shadow ray toward `sunlightyaw`/`sunlightpitch`. If unoccluded, adds `sunlight * sunlightscale`.
2. **Direct Entity Lights (Stage 2):**
   - Consumes `ET_LIGHT` (radius `attr1`) and `ET_SPOTLIGHT` (cone angle attenuation).
   - **Spatial Index (`LightSpatialIndex`):** Probes query lights through a coarse 3D spatial index to avoid an $O(\text{probes} \times \text{lights})$ bottleneck.
   - Traces shadow rays via `RayCube` (mode 0) against the octree.
3. **Indirect Bounce & AO (Stage 3):**
   - Hemisphere rays that hit geometry accumulate surface albedo $\times$ incident sky radiance (single bounce).
   - Ambient occlusion is implicit: geometry corners receive fewer unoccluded rays, naturally darkening $c_0$.

### 5. Incremental & Runtime Updates (Stage 4)

- **Region Invalidation:** Geometry edits (`EditorController.InvalidateRegion`) mark the affected AABB expanded by a margin (one chunk) via `RadianceVolume.MarkDirty(aabb)`.
- **Budgeting:** Rebaking is throttled per frame using a probe budget, matching `WorldChunkRenderer`.
- **Texture Upload:** Rebuilds depth-slice `Image` buffers and updates `ImageTexture3D` at most once per frame (~0.5 MB at $32^3$).
- **Map Load:** Full bake runs asynchronously in the background; the scene renders with ambient fallback until completion.

### 6. Measurements & Benchmarks (Stage 0 Findings)

Measured on Debug builds with `test/RadianceBakeProbe.cs`:

| Map | Grid Size | Cell Size | Volume Memory | Full Sky Bake | Per-Edit 128u (Sky/Direct) |
| --- | --- | --- | --- | --- | --- |
| church51 (640³, 119 lights) | $32^3$ | 20u | 0.5 MB | 6.2s | 49 ms / 2.0 ms |
| church51 | $64^3$ | 10u | 4.0 MB | 60.0s | 482 ms / 19.0 ms |
| earthstation (1696³, 884 lights) | $32^3$ | 53u | 0.5 MB | 1.9s | 0.8 ms / 0.5 ms |
| earthstation | $64^3$ | 26u | 4.0 MB | 14.0s | 6.0 ms / 3.0 ms |

- **$32^3$ resolution is the default:** Meets frame-budget targets for incremental updates while keeping full load bakes between 2–6s (Debug).
- **$64^3$ resolution:** Stretch quality knob.
- **$128^3$ resolution:** Rejected (prohibitive bake times: 104s–459s).
- Geometry-bounded grids avoid wasted probe allocation compared to full `worldsize`.

### 7. Quality Mode & User Configuration

Two lighting quality modes exist (`RadianceQualityMode`):

- **`RadianceHints`** (default): the full L1 SH probe volume (sky, sun, direct
  lights, indirect bounce).
- **`StaticAmbient`**: skips probe raycasts and 3D texture lookups, shading
  geometry with a constant ambient colour — for low-end hardware or fast editing.

The mode is a **persistent user setting**, not a hard-coded constant. It is read
from the shared Godot `ConfigFile` at `user://settings.cfg` under
`lighting_mode` (tokens `radiance_hints` / `static_ambient`) and applied when a
world bakes its volume (`UserSettings`, docs/vision.md §11).

## Technical Roadmap & Staged Plan

| Stage | Scope | Status |
| --- | --- | --- |
| **Stage 0** | Measurement probe; geometry bounds; grid sizing | Complete (`test/RadianceBakeProbe.cs`) |
| **Stage 1** | `RadianceVolume` (sky + sun), `Rgbah` 3D texture, `radiance.gdshader`, world material integration | Complete |
| **Stage 2** | Direct `ET_LIGHT` / `ET_SPOTLIGHT` into volume; spatial light index (`LightSpatialIndex`) | Complete |
| **Stage 3** | Single bounce indirect light + AO / sky visibility | Complete |
| **Stage 4** | Incremental region rebake wired to editor dirty path (`WorldChunkRenderer` + `MapWorld`) | Complete |
| **Stage 5** | Quality/perf knobs, fallback paths (no catalogue), complete T1 lightmap removal, docs | Remaining |

## T1 Removal Inventory

T1 lightmap support is replaced and will be phased out across stages:
1. **R1 (Done):** World materials switch to `radiance.gdshader`.
2. **R2 (Pending):** Delete `lightmap.gdshader`, `BuildLightmapMaterial`, `SurfaceKey.Lmid`, `TexUV2` emission in mesher, and lightmap mesh tests.
3. **R3 (Pending):** Remove `OgzReader.ReadLightmaps` (skip byte payload), remove `test/golden/lightmaps/`.
4. **R4 (Pending):** Format version bump to drop `CubeExt` lightmap links if unused by subsequent systems.

## Key Source Files

- `World/Lighting/RadianceVolume.cs`: Core controller, bake passes, dirty queue.
- `World/Lighting/RadianceGrid.cs`: Probe storage, 3D coordinate mapping, `ImageTexture3D` GPU upload.
- `World/Lighting/RadianceProbe.cs`: L1 SH data structures and evaluation.
- `World/Lighting/LightSpatialIndex.cs`: Coarse 3D grid indexing entity lights.
- `World/Lighting/RadianceSettings.cs`: Performance and quality presets.
- `scripts/UserSettings.cs`: Typed record for the user settings file (`user://settings.cfg`), including the lighting mode.
- `World/Materials/radiance.gdshader`: Spatial shader evaluating the 3D probe volume.
