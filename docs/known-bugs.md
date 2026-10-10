# Known bugs & fidelity gaps

A running list of behaviours we **know** diverge from Sauerbraten (SVN r6889),
with the engine reference and the intended fix. Names mirror engine tokens so
entries stay greppable against the C++ source. Add an entry whenever we find a
divergence we are not fixing right now.

---

## Water & waterfalls

### 1. Waterfall drawn twice, offset by one cube (the "−1 X" artifact)

**Symptom.** In `bt_falls` the waterfalls render, but ~1 cube shifted in −X; the
top waterfall pokes into the room although the original keeps it outside.

**Cause.** `bt_falls`' water cubes are two different things, and both our meshers
handle both:

- **Solid cubes flagged with a material** — the waterfalls. In the map they read
  `faces = 80808080,80808080,80808080`, `material = MAT_WATER` (i.e.
  `IsEntirelySolid() == true`). The engine's `visibleface()`
  (`engine/octa.cpp:1013`) **hides** these from the material path
  (`faceedges(c,orient)==F_SOLID && touchingface(c,orient)`); they are solid
  geometry (`classifyface`, `octa.cpp:1059`) drawn with the waterfall texture.
- **Purely-liquid cubes** — the lake surface, a material volume that is *not*
  fully solid. The engine's `classifyface()` drops their geometry via
  `collapsedface()` (`octa.cpp:972`), and `genmatsurfs()`
  (`engine/material.cpp:214`) draws them through the material path.

Our meshers ignore the distinction:

- `OctaMesher` meshes every `!IsEmpty()` cube, so it draws the purely-liquid cubes
  whose engine geometry is collapsed.
- `MaterialMesher` meshes every `(material & MATF_VOLUME) != MAT_AIR` cube, so it
  draws the fully-solid waterfall cubes too.

Every water cube is therefore drawn **twice** — once as a flat full face
(`CubeGeometry.SolidFaceVerts`) and once edge-shaped (`Cube.GenFaceVerts`) — and
the two surfaces disagree by up to a cube. That overlap is the −1 cube offset.

**Intended fix** (developed, then reverted in `8b306cc`; re-apply when we return
to water):

- `MaterialMesher`: skip a face when its own cube is fully solid —
  `if (face.Cube.IsEntirelySolid()) return false;` (the `visibleface()` early-out).
- `OctaMesher` include:
  `!c.IsEmpty() && ((c.Material & MATF_VOLUME) == 0 || c.IsEntirelySolid())` —
  drop purely-liquid cubes, which `MaterialMesher` draws instead.

Evidence from the experiment: bt_falls' material mesh went 996 → 335 faces (all
horizontal — only the lake), church51's solid mesh 686392 → 622570 triangles.

### 3. Residual shimmer inside liquid/glass volumes from per-cube material faces

**Symptom.** After the obvious duplicate-draw fix, some shimmer remains at the
interface between water/glass and solid cubes, and in the interior of the
material volume itself. The surface is much closer to the reference, but the
seams still show as faint “cubeish” flicker where a material face should have
been hidden by the exact engine occlusion tests.

**Cause.** The engine does not merely hide same-material faces or boundary faces
at a solid neighbour. It also applies the exact per-face `visibleface()` /
`classifyface()` / `occludesface()` occlusion path, which tests the polygon of
both faces against each other and suppresses any face fully enclosed by the same
volume or hidden by a neighbouring surface. Our material pass is still a
conservative boundary mesh: it emits each surviving face as a standalone quad,
without the engine's exact polygon clipping and per-face visibility tests, so a
fully hidden voxel face can still leak as shimmering internal cubes.

**Current status.** We have fixed the obvious duplicate-draw offset, but the
remaining shimmer is the deeper “exact culling” bug. It should be solved by
re-using the engine's `visibleface()` / `occludesface()` semantics (the
`clipfacevecs`/`insideface` path) when deciding whether to emit a material
surface, rather than the simplified same-material/solid-neighbour heuristic.

**Likely fix direction.** Use the exact `OctaVisible` logic to reject material
faces when they are occluded by a neighbour's face polygon or by a larger solid
volume. This is required for both water-vs-glass and glass-vs-solid transitions,
and for the interior case where one material cube's own hidden faces should never
be emitted at all.

### 2. Waterfalls use a static texture instead of the waterfall shader

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

---

## Player/monster animation

### 1. Driving Godot `Skeleton3D` bones from MD5 clips deforms the mesh

**Symptom.** The player body animates, but the **hands and right foot are visibly
deformed** (stretched/broken), while the rest of the body looks correct.

**Cause.** The engine's MD5 skeleton has **75 bones**; Godot's glTF importer
**collapses bones with no skinning influence** (`Root`, `Torso`, `SpineIK*`,
`FingerNull*`, …) into a **50-bone `Skeleton3D`** and re-parents survivors. Our
runtime load (`GltfDocument.GenerateScene`) additionally discards the bone *nodes*.
The engine's per-bone `frame[k]` is parent-relative in the **engine** hierarchy, so
no formula maps it exactly onto Godot's collapsed hierarchy — the hands/fingers
(whose engine parent is a dropped `FingerNull`) and the right foot land wrong.

**Intended fix.** Stop driving reconstructed `Skeleton3D` bone poses. Animate the
glTF **bone nodes** instead — either bake animated `.glb`s offline (assimp loads one
sibling `<meshbase>.md5anim` per export) and let Godot's importer own the model, or
keep the full node hierarchy and emit node-path tracks (as Godot's importer does).
Full analysis, the exact engine math, and the reusable-verified pieces are in
[character-animation.md](character-animation.md).
