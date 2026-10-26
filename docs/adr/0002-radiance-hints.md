# ADR 0002: Radiance Hints (L1 SH Irradiance Probe Volume)

## Status

Accepted. GPU Compute Stages 1–4 implemented (Stage 5 blendmaps remaining).
Supersedes the baked-lightmap design in [docs/adr/0001-lighting.md](0001-lighting.md).

## Context

Sauerbraten SVN r6889 bakes direct irradiance per lumel into shared 512×512 atlases (`engine/lightmap.cpp`). This model assumes static geometry:
- Moving cube faces invalidates their unwraps and any faces sharing the atlas page.
- Changing textures/vslots alters sort keys and requires atlas repacking.
- Adding or moving lights triggers `calclight` over all affected lumels.
- Blendmaps/layers rely on the atlas alpha channel as a blend mask.

In Sauerbraten Revival, real-time in-game world editing (coop edit, runtime geometry mutation) is a core requirement. Rebaking lightmaps via a ported `calclight` is prohibitive at runtime and couples lighting to surface topology. Furthermore, Godot's built-in `LightmapGI` cannot be used because world geometry is dynamically generated into an `ArrayMesh` from a mutable octree.

Initial CPU-based L1 SH probe raycasting (`RayCube`) validated the lighting model, but required 2–6s for $32^3$ full bakes and 14–60s for $64^3$ full bakes. Moving the raymarching and SH projection onto GPU compute shaders (`RenderingDevice`) eliminates CPU-to-GPU probe upload stalls and brings both $32^3$ and $64^3$ bakes down to a few milliseconds.

## Decision

1. **Abandon per-surface baked lightmaps:** T1 (importing original lightmap atlases) and T2 (`calclight` port) are discarded for runtime rendering.
2. **Adopt world-space L1 Spherical Harmonics (SH) probe volume (Radiance Hints):**
   - Decouples lighting from surface unwraps and mesh topology.
   - Enables O(1) lighting evaluation per vertex or fragment for arbitrary positions and normals.
   - Supports incremental localized rebaking when geometry or lights change.
3. **Bake on the GPU via Compute Shaders (`GpuRadianceBaker`) with CPU Fallback:**
   - Extract a dense 3D voxel occupancy + albedo representation (`VoxelGrid`, $128^3$ `RGBA8`) from the octree and trace rays on the GPU using a 3D Amanatides & Woo DDA raymarcher.
   - Bind GPU output directly to world materials via zero-copy `Texture3DRD` handles (`radiance_tex` and `radiance_amb_tex`), falling back to CPU `ImageTexture3D` in headless environments or `StaticAmbient` mode.
4. **Accept soft, diffuse-only world lighting with `ET_ENVMAP` cubemap reflections:** Direct shadow softness is bounded by cell size (~10–50 units). Crisp contact shadows from the static world are traded away for runtime editability.
5. **World geometry ignores Godot `Light3D` nodes while sampling `ReflectionProbe`s:** World surfaces use `render_mode ambient_light_disabled, cull_back;` with an empty `void light() {}` function so dynamic `OmniLight3D` / `SpotLight3D` / `DirectionalLight3D` nodes only light dynamic entities (`MapPlayer`, mapmodels, pickups) while `SHADER_ENVMAP` world surfaces still sample `EnvMapProbe` (`ReflectionProbe`) cubemaps.

## Technical Details

### 1. Representation & Full-RGB SH Mathematics

Incoming radiance $L(\omega)$ is projected onto the L1 SH basis:
```
Y00  = 0.282095
Y1-1 = 0.488603 * ω.y
Y10  = 0.488603 * ω.z
Y11  = 0.488603 * ω.x
```

Diffuse irradiance (cosine-lobe convolved) at surface normal $n$ is:
$$E(n) = 0.886227 \cdot L_{00} + 1.023327 \cdot (L_{1,-1} n_y + L_{1,0} n_z + L_{1,1} n_x) = c_0 + c_1 \cdot n$$

To preserve full RGB color for colored entity lights, sunlight, skylight, and single-bounce indirect GI without requiring 4 separate 3D textures, we store two `RGBA16F` 3D textures per volume:
- `radiance_tex` (`probe_final_tex`): `rgb` = scalar-luminance directional vector $c_1 \in \mathbb{R}^3$, `a` = scalar ambient irradiance $c_0$.
- `radiance_amb_tex` (`probe_final_amb_tex`): `rgb` = colored ambient irradiance $c_{0,\text{rgb}} \in \mathbb{R}^3$, `a` = sky visibility ($V_\text{sky} \in [0, 1]$).

At fragment evaluation time, the directional contribution `dot(c1, norm)` is chromaticity-scaled by $c_{0,\text{rgb}} / c_0$:
$$\text{chroma} = \begin{cases} c_{0,\text{rgb}} / c_0 & \text{if } c_0 > 10^{-4} \\ \text{sky\_tint} & \text{otherwise} \end{cases}$$
$$E_\text{rgb}(n) = \max\left(c_{0,\text{rgb}} + \text{chroma} \cdot (c_1 \cdot n), 0\right)$$
This reconstructs exact per-channel L1 SH irradiance for colored lights while keeping $c_1$ as a single 3D spatial vector.

### 2. GPU Compute Pipeline & Shader Interface

- **Voxel Grid (`VoxelGrid`):**
  - $128^3$ `RGBA8` (`R8G8B8A8Unorm`, 8 MB VRAM) aligned to the cubic `RadianceGrid` bounds.
  - `rgb` = average surface diffuse albedo (`vslot.Color * avgTextureColor`, or `0.5` fallback).
  - `a` = `1.0` if solid or partially-solid octree cell (`!IsEmpty`), `0.0` if open air.
- **Pass 1 (`World/Shaders/radiance_direct.glsl`, workgroup `4×4×4`):**
  - Rejects probes inside solid voxels (writes zero so trilinear interpolation darkens cleanly at walls).
  - Casts 1 sun shadow ray (`raymarch_dda`), $R$ cosine-weighted golden-spiral hemisphere sky rays, and shadow rays to in-range lights from the `LightBuffer` SSBO.
  - Writes `probe_direct_tex` (`vec4(c1, c0)`) and `probe_direct_amb_tex` (`vec4(c0_rgb, sky_vis)`).
- **Pass 2 (`World/Shaders/radiance_bounce.glsl`, workgroup `4×4×4`):**
  - Casts $R$ hemisphere rays; on voxel hits, samples `probe_direct_sampler` and `probe_direct_amb_sampler` at `hit_pos + hit_normal * cell_size * 0.75`, evaluates outgoing RGB bounce irradiance `hit_albedo * surf_irr_rgb * (1/π)`, and accumulates single-bounce indirect GI into `probe_final_tex` and `probe_final_amb_tex`.
- **World Shader (`World/Materials/radiance.gdshader`):**
  - Uses `render_mode ambient_light_disabled, cull_back;` with an empty `void light() {}`.
  - Perturbs world-space normal via a screen-space cotangent frame (`dFdx`/`dFdy`) when `use_normal` is enabled, transforming tangent-space `normal_tex` into world space before evaluating `dot(c1, norm)` and updating view-space `NORMAL` for cubemap reflections.
  - Outputs diffuse radiance + glow to `EMISSION`.
  - When `use_envmap` is enabled (`SHADER_ENVMAP`: `envworld`, `bumpenv*world`), sets `ALBEDO = env_tint`, `METALLIC = 1.0`, `SPECULAR = env_intensity`, and `ROUGHNESS = env_roughness` (from slot/vslot `envscale`) so Godot's Forward+ reflection pass blends `EnvMapProbe` cubemaps additively over `EMISSION`. `EnvMapProbe` sets `AmbientMode = Disabled` to prevent double-counting ambient irradiance.

### 3. Grid Topology & Coordinate Space

- **Bounding Box:** Computed from the AABB of non-empty geometry (`GeometryBounds`), expanded by one cell margin, and cubified (equal dimensions along all axes) to guarantee cubic cells.
- **Probe coordinates:** Probes sit at cell centers:
  $$\text{uvw} = \frac{\text{world}_\text{pos} - \min(\text{grid})}{|\text{grid}|}$$
- **Coordinate transformation:** The octree is Sauerbraten Z-up, while Godot nodes are rotated −90° about X. Sauerbraten $(x, y, z)$ maps to Godot $(x, z, -y)$. Both `RadianceGrid` and `VoxelGrid` are indexed in Godot world space so compute and fragment shaders sample using unmodified world coordinates.

### 4. Incremental & Runtime Updates

- **Region Invalidation:** Geometry edits (`EditorController.InvalidateRegion`) and light edits (`RadianceVolume.UpdateLights`) call `RadianceVolume.MarkDirty(sauerAabb)`.
- **GPU Sub-Region Dispatch:** When `GpuBaker` is active, `MarkDirty` re-extracts only the affected voxel sub-box (`VoxelGrid.ExtractRegion`), uploads the updated voxel texture, expands the dirty box by `2 * CellSize` to cover 1-bounce GI bleed, and dispatches Pass 1 and Pass 2 only over `[dirty_min..dirty_max]`.
- **EnvMap Refresh:** Any registered `EnvMapProbe` whose `SauerBounds` overlaps the dirty AABB has its `UpdateMode` pulsed to `ReflectionProbe.UpdateModeEnum.Once` (`RadianceVolume.RefreshOverlappingEnvMaps`).
- **CPU Fallback Budgeting:** When running on the CPU fallback path, dirty probes are queued and rebaked incrementally using `BudgetPerFrame`.

### 5. Measurements & Benchmarks

#### GPU Compute Baker (Apple M1, Metal 4.0 Forward+, $128^3$ Voxel Grid, 16 Sky Rays)

| Map | Probe Grid | Probes | Lights | Voxel Extract ($128^3$) | Full GPU Bake (Pass 1 + 2) | Per-Edit 128u Voxel Extract | Per-Edit 128u GPU Bake |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `church51` (640³) | $32^3$ | 32,768 | 119 | 5.6 ms | **1.5–2.1 ms** | 1.3–2.1 ms | **1.8–2.4 ms** |
| `church51` (640³) | $64^3$ | 262,144 | 119 | 6.5 ms | **2.5–3.2 ms** | 0.5 ms | **1.8–2.5 ms** |
| `earthstation` (1696³) | $32^3$ | 32,768 | 884 | 5.1 ms | **1.8–2.0 ms** | 0.15 ms | **1.7–2.3 ms** |
| `earthstation` (1696³) | $64^3$ | 262,144 | 884 | 6.6 ms | **1.7–1.8 ms** | 0.14 ms | **2.1–2.8 ms** |

- **2,000×–18,000× faster than CPU `RayCube`:** Full $32^3$ bakes drop from 1.9–6.2s to **~2 ms**, and full $64^3$ bakes drop from 14–60s to **~2–3 ms**.
- **Single-frame coop-edit updates:** Incremental 128u geometry edits complete in **~2–4 ms** total (voxel sub-extract + GPU compute dispatch), well within a 16.6 ms (60 fps) frame budget at both $32^3$ and $64^3$.

### 6. Quality Mode & User Configuration

Two lighting quality modes exist (`RadianceQualityMode`):

- **`RadianceHints`** (default): the full L1 SH probe volume (sky, sun, direct lights, indirect bounce).
- **`StaticAmbient`**: skips probe raycasts and 3D texture lookups, shading geometry with a constant ambient colour — for low-end hardware or fast editing.

The mode is a **persistent user setting** read from `user://settings.cfg` under `lighting_mode` (`radiance_hints` / `static_ambient`) and applied when a world bakes its volume (`UserSettings`, docs/vision.md §11).

## Technical Roadmap & Staged Plan

| Stage | Scope | Status |
| --- | --- | --- |
| **Stage 1** | `VoxelGrid` ($128^3$ `RGBA8` occupancy + albedo) & CPU extraction (`Extract` / `ExtractRegion`) | Complete |
| **Stage 2** | `GpuRadianceBaker` (`RenderingDevice`), `radiance_direct.glsl` + `radiance_bounce.glsl`, `Texture3DRD` zero-copy binding | Complete |
| **Stage 3** | Incremental sub-region GPU updates (`dirty_min`/`dirty_max`), dynamic light SSBO updates, `EnvMapProbe` refresh | Complete |
| **Stage 4** | Full RGB color preservation (`radiance_amb_tex`), world-space normal mapping, `SHADER_ENVMAP` + `EnvMapProbe` polish, $32^3$/$64^3$ benchmarks | Complete |
| **Stage 5** | Blendmap (`layer != 0`) multi-layer support & legacy lightmap cleanup | Remaining |

## Key Source Files

- `World/Lighting/VoxelGrid.cs`: $128^3$ `RGBA8` voxel occupancy + surface albedo extractor.
- `World/Lighting/GpuRadianceBaker.cs`: `RenderingDevice` compute pipeline, SSBO/texture manager, and `Texture3DRD` output.
- `World/Shaders/radiance_direct.glsl`: Pass 1 compute shader (3D DDA raymarcher, sun, sky hemisphere, entity light SSBO).
- `World/Shaders/radiance_bounce.glsl`: Pass 2 compute shader (single-bounce indirect GI + sky visibility).
- `World/Lighting/RadianceVolume.cs`: Core controller, GPU/CPU bake orchestration, dirty region & `EnvMapProbe` refresh.
- `World/Lighting/RadianceGrid.cs`: Probe storage, 3D coordinate mapping, CPU `ImageTexture3D` fallback.
- `World/Lighting/RadianceProbe.cs`: L1 SH data structures (`Dir`, `Amb`, `AmbColor`) and evaluation.
- `World/Lighting/EnvMapProbe.cs`: `ET_ENVMAP` `ReflectionProbe` node with `AmbientMode = Disabled` and `SauerBounds`.
- `World/Materials/radiance.gdshader`: Spatial shader evaluating full-RGB L1 SH irradiance, world-space normal maps, glow, and `SHADER_ENVMAP` cubemap reflections.
- `World/Materials/MaterialBuilder.cs`: Builds world `ShaderMaterial`s and binds `radiance_tex`, `radiance_amb_tex`, normal maps, and `envscale` parameters.
