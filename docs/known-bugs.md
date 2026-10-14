# Known bugs & fidelity gaps

A running list of behaviours we **know** diverge from Sauerbraten (SVN r6889),
with the engine reference and the intended fix. Names mirror engine tokens so
entries stay greppable against the C++ source. Add an entry whenever we find a
divergence we are not fixing right now.

---

## Water & waterfalls

### Waterfalls use a static texture instead of the waterfall shader

The engine renders waterfalls with dedicated animated geometry and shaders
(`engine/material.cpp:87 renderwaterfall`, the `MAT_WATER` case at
`material.cpp:685`, `engine/water.cpp` `renderwater`), including:

- the `ends` top/bottom "wave" extension (`WATER_OFFSET`,
  `WATER_AMPLITUDE`, computed in `material.cpp:367-410`),
- the scrolling `waterfalltexgen` UVs (`material.cpp:771`), and
- `waterfallenv` / `waterfallrefract` variants.

We draw the solid waterfall cubes with their **static** vslot texture (chunk
mesher) and the liquid surfaces with the generic scrolling water shader
(`MaterialTextures`). A faithful waterfall needs per-surface `ends` detection,
the animated shader + wave extension, and (optionally) the `vertwater`
vertex displacement.

---

## Weapons & hitscan effects

### Hitscan bullet display & tracer effects diverge from engine

The engine implements hitscan shot visuals in `src/fpsgame/weapon.cpp:592 shoteffects`
using particle systems, billboard tape ribbons, dynamic lights, and muzzle flashes.
Our current `WeaponEffectsRenderer` (`World/Effects/WeaponEffectsRenderer.cs`) uses a
placeholder implementation with the following divergences:

- **Tracer geometry & texture**: Original tracers (`PART_STREAK`, texture `packages/particles/flare.jpg`)
  are rendered via `taperenderer` (`engine/renderparticles.cpp:569 genpos<PT_TAPE>`), generating
  camera-facing billboarded textured quads with width (`size = 0.28f`) and additive/glare blending.
  We currently draw 1-pixel untextured `Mesh.PrimitiveType.Lines`, ignoring width and texture.
- **Shot origin offset (`hudgunorigin`)**: Original tracers do not originate from the camera center
  (`from = Entity.O`). In first person, `hudgunorigin(gun, from, to, d)` (`fpsgame/render.cpp:490`)
  offsets the streak origin down and right (or to the model's `muzzle` attachment tag) so beams
  emerge from the gun barrel. We currently emit straight from the player eye position (`Entity.O`),
  causing beams to shoot out of the camera center.
- **Rifle smoke trail (`PART_SMOKE`)**: The rifle fires a smoke trail of small puffs
  (`particle_trail(PART_SMOKE, 500, hudgunorigin, to, 0x404040, 0.6f, 20)`) with random spatial
  jitter (`renderparticles.cpp:1147`) rather than a single solid beam. We currently draw a static
  fading line segment.
- **Impact sparks (`PART_SPARK`)**: Hitscan impacts in the original spawn spark splashes
  (`particle_splash(PART_SPARK, ..., to, 0xB49B4B, 0.24f)` in `weapon.cpp:608, 619, 652` —
  200 particles for rifle/chaingun/pistol, 20 per pellet for shotgun). We do not spawn impact sparks.
- **Muzzle flash & dynamic light**: Hitscan shots trigger a camera-tracked muzzle flash sprite
  (`particle_flare(..., PART_MUZZLE_FLASH1..3)`) and a momentary dynamic light
  (`adddynlight(..., DL_FLASH)`). We do not spawn muzzle flashes or weapon firing lights.

---

## Correctness notes (engine rules worth preserving)

Ported faithfully already — keep these in mind when touching the meshers; several
were the source of the waterfall confusion:

- **`visibleface()`** (`octa.cpp:1013`): a material face is hidden when its own
  cube is fully solid, when its neighbour is solid or the same volume, or when the
  neighbour is outside the world — `neighbourcube()` (`octa.cpp:263`) returns the
  same cube for out-of-world, so the engine never draws world-boundary faces.
- **`classifyface()` / `collapsedface()`** (`octa.cpp:1059`, `octa.cpp:972`): a
  cube with no solid geometry contributes no chunk geometry.
- **`genfacevecs(solid = true)`** (`octa.cpp:827`): material surfaces are
  axis-aligned **full** cube faces; the liquid level affects only the solid
  geometry path (`genfaceverts`, edge-aware).
- **`OctaStep`** (`octa.h:269`): bit0 = X, bit1 = Y, bit2 = Z. The OGZ geometry
  checksum (`World/Ogz/OgzGeometry.cs`) walks cubes in index order and does **not**
  hash coordinates, so an axis/child-order mistake would pass the checksum — worth
  a coordinate-aware assertion if geometry ever looks shifted.
