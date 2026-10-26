# ADR 0005: Bullet Decals on World Geometry

## Status

Accepted. Implemented.

---

## Context

When hitscan weapons (Rifle, Shotgun, Chaingun, Pistol) hit map geometry in Sauerbraten (`src/fpsgame/weapon.cpp:shoteffects` and `raydamage`), bullet hole marks (`DECAL_BULLET`) are placed on surfaces:
- `fpsgame/weapon.cpp:610, 623, 656, 752, 772` triggers `adddecal(DECAL_BULLET, to, dir, radius)` with `radius = 3.0f` for Rifle and `2.0f` for other hitscan guns.
- Decals persist for `decalfade = 10000ms` (10 seconds) and then fade out over `fadeouttime = 1000ms` (1 second).
- Sauerbraten's original engine (`engine/decal.cpp`) clipped decal polygons directly against octree cube faces using `gentris`.

When implementing this in Godot 4 (.NET), two primary constraints shaped the architecture:

1. **Custom-Lit World Shader (Godot lighting bypassed):**
   Our map geometry uses [`World/Materials/radiance.gdshader`](../../World/Materials/radiance.gdshader), which evaluates radiance hints and ambient irradiance directly. It specifies `render_mode ambient_light_disabled, cull_back;` and an empty `void light() {}` (see [ADR 0002](0002-radiance-hints.md)), so Godot's dynamic lights do not contribute to world surfaces.
   In Godot 4 (Forward+ / Clustered renderer), built-in `Godot.Decal` projector nodes only run during the clustered PBR lighting pass. Because world geometry produces no Godot lighting output, built-in `Decal` projections are completely bypassed and do not render on map walls.

2. **Original Texture Blend Mode (`DF_OVERBRIGHT`)**:
   Sauerbraten's `packages/particles/bullet.png` was originally an RGB texture rendered with `DF_OVERBRIGHT` (`glBlendFunc(GL_DST_COLOR, GL_SRC_COLOR)`), where neutral 128 meant no change, darker values darkened, and brighter values brightened. Under standard alpha blending, using raw values without transparency resulted in opaque grey quad backgrounds.

---

## Decision

We chose **Surface Quad Mesh Decals** managed by a fixed-size ring buffer:

### 1. Mesh-Based Quad Decals
- We use a pool of 256 `MeshInstance3D` nodes carrying a shared 1x1 `QuadMesh`.
- Each quad uses `StandardMaterial3D` with:
  - `ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded` (rendering correctly over custom radiance-lit world geometry).
  - `Transparency = BaseMaterial3D.TransparencyEnum.Alpha`.
  - `CullMode = BaseMaterial3D.CullModeEnum.Disabled`.
  - `DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always`.

### 2. Texture Pre-processing (Alpha Transparency)
- In [`tools/texture-import/import_textures.py`](../../tools/texture-import/import_textures.py), `bullet.png` is converted to RGBA at import time.
- The bullet impact mark brightness is mapped directly to the alpha channel:
  - Background / outer areas have `alpha = 0` (100% transparent).
  - The impact mark itself is white (`RGB = 255, 255, 255`) with variable alpha matching the pixel intensity.
- At runtime, [`WeaponEffectsRenderer`](../../World/Effects/WeaponEffectsRenderer.cs) safely loads the texture with a fallback to direct disk loading via `ImageTexture`.

### 3. Surface Alignment & Pooling
- Decals are placed at the ray impact point with a tiny offset (`0.05` units) along the surface normal to prevent Z-fighting.
- An orthonormal right-handed basis is constructed from the surface normal:
  - `normal = dir.Normalized()`
  - `tangent = normal.Cross(Vector3.Right)` (or `Vector3.Up` if aligned with X)
  - `bitangent = normal.Cross(tangent)`
- Random rotation (`DF_ROTATE`) is applied around `normal`.
- The quad's basis is scaled by `diameter = radius * 2.0f`.
- Active decals are faded out via `entry.Material.AlbedoColor.A` once `now > FadeStartTime`, recycling slots circularly once the 256-decal budget is reached.

---

## Consequences

- **Positive**: Bullet decals are visible on all map walls and surfaces without relying on Godot's PBR lighting passes.
- **Positive**: No runtime polygon clipping against the octree is needed; simple quads are cheap and efficient.
- **Positive**: Decal positions, rotations, sizes, and fade lifetimes match Sauerbraten (`r6889`).
- **Limitation**: Flat quad decals do not bend around sharp 90-degree corners or cube edges like volumetric projectors; however, because bullet hole radii are small (2 to 3 Sauerbraten units), the visual fidelity is excellent and closely matches the original particle spats.
