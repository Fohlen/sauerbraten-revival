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
