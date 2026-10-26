# ADR 0007: World Rendering — Chunked ArrayMesh Streamed to RenderingServer

## Status

Accepted. Core world rendering is implemented.

Related decisions: lighting is [ADR 0002](0002-radiance-hints.md), bullet decals are
[ADR 0005](0005-decals.md), and classic-map loading is [ADR 0006](0006-ogz-import.md).
Grass, specular maps, blended layers / blendmaps and the waterfall shader are
**deferred fidelity work**, not core rendering; they do not change this decision.

---

## Context

Sauerbraten Revival's world is a native, runtime-mutable octree. Live editing,
coop edit, crumbling geometry and game-mode-created geometry all mutate the same
structure. Rendering must therefore be derived from the octree and stay correct
incrementally — it cannot be a static bake.

The original engine (`octarender.cpp`) couples rendering to the octree's mutable
representation: it walks the octree into per-surface VBOs and bakes lighting into
per-surface lightmap atlases. That model assumes static geometry, and its editor
paths are entangled with VBO/lightmap/atlas bookkeeping.

We neither need nor want to reimplement that renderer. Godot 4's Forward+ renderer
already provides frustum/occlusion culling, shadows, materials, shaders and
reflection probes. The questions this ADR settles are therefore:

- how octree geometry becomes Godot geometry;
- how edits update that geometry without rebuilding the world;
- how Sauerbraten materials map to Godot materials; and
- where lighting, collision and LOD sit relative to rendering.

---

## Decision

### 1. The octree is the source of truth; meshes are derived views

The octree stores geometry and surface metadata only. No VBOs, GPU state, chunk
buffers or lighting atlases live in the world data. A mesher walks the octree and
emits Godot geometry. A mesh is always reproducible from the octree and its
material set, so replaying a command stream reconstructs the same world.

### 2. Mesh in chunks, rebuild incrementally

The world is divided into cubic chunks. The initial build meshes every non-empty
chunk; subsequent octree mutations mark only the chunks overlapping the changed
AABB dirty. The renderer rebuilds at most `BudgetPerFrame` dirty chunks per frame.

- `WorldChunkRenderer` owns chunk lifecycle, dirty tracking and the per-frame
  budget.
- `OctaMesher.BuildChunkBuilder` / `OctaFaceWalker.WalkRegion` bound the rebuild to
  the affected region instead of re-walking the whole octree.
- Invalidation is deliberately conservative: a change marks every chunk its AABB
  overlaps, expanded by the cube margin, because a changed cube can alter culling
  of neighbouring faces (which live in their own chunks) and merged/T-jointed
  geometry can spill across the edit boundary. Over-marking is safe; under-marking
  leaves stale geometry.

Chunk size is load-bearing, not a tuning afterthought. The renderer's fallback
default is **128**, but the load paths set their own values; these are the values
the game actually runs today:

| Load path | Chunk size | Budget/frame |
| --- | --- | --- |
| Classic maps / play (`OgzMapViewer` → `MapWorld`) | **512** | 4 |
| Native editor maps (`MapLevel`) | **512** | 4 |
| Empty editor map (`EmptyMap`) | **128** | 4 |
| `WorldChunkRenderer` fallback | 128 | 4 |

Measured on **church51** (107,356 triangles in the final mesh):

| Chunk size | Per-chunk rebuild |
| --- | --- |
| 512 | ~108 ms |
| 256 | ~46 ms |
| 128 | ~1.4 ms |

A full rebuild costs ~660 ms regardless of chunk size. At the current 512-unit
load-path setting, one chunk rebuild is ~108 ms, so the 4-chunk budget allows up
to ~432 ms of meshing in a frame — large edits can hitch. The ~1.4 ms measurement
at 128 is why the renderer fallback is 128; reaching that edit latency in the
actual load paths requires lowering their `ChunkSize` to 128 (and gives ~6 ms
worst case on church51 at budget 4).

### 3. Port the octree's *surface* rules, not its renderer

The mesher preserves the parts of `octarender.cpp` that determine *which* geometry
exists and how it is shaped:

- exact per-face occlusion culling (`visibletris`, including partial single-triangle
  faces and diagonal flips);
- face merges (`genmerges`) and T-joint resolution (`findtjoints` / `addtris`);
- bottom-layer geometry for `vslot.layer` faces (`LAYER_BOTTOM`);
- `MAT_ALPHA` transparency, kept in separate surfaces and never culled against
  opaque neighbours in either direction;
- water/lava/glass boundary surfaces generated from material volumes.

`MeshFace` owns the winding/normal convention. Positions are emitted in
Sauerbraten coordinates (Z-up); the world scene node applies the Z-up → Y-up
rotation. World faces are back-face culled.

### 4. Stream chunk meshes directly to `RenderingServer`

World geometry bypasses `MeshInstance3D` scene-tree nodes. `WorldChunkRenderer`
owns one mesh RID and one visual-instance RID per non-empty chunk:

- create the mesh/instance RIDs once;
- on rebuild, clear and re-add the mesh's surfaces in place;
- free the RIDs when a chunk becomes empty;
- attach instances to the `World3D` scenario and keep transforms/visibility in
  sync through node notifications.

This keeps the scene tree free of thousands of world-geometry nodes while still
using Godot's renderer and culling. `ArrayMeshBuilder.UploadTo` is the single
surface-upload seam; prebuilt meshes (test seeds, warm starts) are also bound
directly, and a prebuilt chunk allocates its own mesh RID on first edit so shared
meshes are never mutated.

### 5. Map Sauerbraten materials to Godot materials

`ArrayMeshBuilder` groups triangles into one surface per material key (the vslot,
plus an alpha bit), because one Godot surface carries one material.

- Classic maps resolve slots/vslots through the committed
  `metadata-catalogue/<map>.json`; native maps carry an equivalent manifest.
- `MaterialBuilder` ports `calctexgen` and emits per-face world-space UVs, building
  Godot materials with diffuse/tint, normal maps and glow/emission.
- `AlphaMaterial` reproduces the engine's premultiplied, two-sided alpha path and
  `alphafront`/`alphaback` behaviour.
- `MaterialTextures` supplies the unshaded water/lava/glass shader with animated
  UV scrolling.
- `SkyBuilder` turns the map's `skybox` cubemap into a `shader_type sky` material.
- `ITextureSource` is the texture seam: imported Godot Resources in normal runs,
  with a runtime reference-dump fallback.

World materials sample the L1 SH radiance volume from
[ADR 0002](0002-radiance-hints.md); `SHADER_ENVMAP` surfaces reflect `EnvMapProbe`
cubemaps. Baked lightmaps are not part of the rendering path.

### 6. Rendering and collision are decoupled

Collision and movement query the octree directly with the ported `physics.cpp`
algorithms. They never use Godot physics shapes and never wait for a mesh rebuild.
Godot physics is reserved for entities (players, projectiles, mapmodels). The
octree therefore serves collision, meshing and spatial queries simultaneously,
with edits taking effect in collision immediately.

### 7. No LOD for world geometry

World geometry deliberately has no LODs:

- LOD generation would run per surface on every chunk rebuild, i.e. on the
  measured ~1.4 ms edit path;
- what survives exact culling and merges is mostly large coplanar polygons, so
  there is almost nothing to simplify;
- independently simplified chunks crack seams and undo T-joints;
- the distance bottleneck is draw calls and occlusion, which mesh LOD does not fix.

Imported mapmodels keep the glTF importer's baked LODs. If a 4K profile ever shows
world geometry dominating GPU time, the intended lever is an **octree-native**
coarser level (ported `remip`), not per-chunk mesh simplification.

---

## Alternatives considered

| Alternative | Why rejected |
| --- | --- |
| Reimplement Sauerbraten's renderer | Massive maintenance cost; loses Godot's culling, materials and future renderer work. |
| Rebuild the whole world mesh per edit | ~660 ms per edit on church51 (~1.5 fps while dragging), and every mutation would hitch. |
| Use large chunks to reduce instance count | A single 512-unit chunk costs ~108 ms to rebuild; the per-chunk latency is the constraint, not instance count. |
| Keep world geometry as `MeshInstance3D` nodes | Scene-tree overhead for thousands of geometry nodes; direct RID streaming is simpler for bulk world geometry. |
| Use Godot physics/collision shapes for the world | Loses movement fidelity and requires shape rebuilds on every mutation. |
| Import original lightmaps | Incompatible with runtime geometry and per-face unwraps; see [ADR 0002](0002-radiance-hints.md). |
| Add LODs to world chunks | Pays generation cost on the edit path, creates seams, and does not target the real bottleneck. |

---

## Consequences

**Positive**

- World edits are incremental and bounded by a per-frame chunk budget; collision
  updates instantly.
- No custom renderer, VBO management or lightmap atlas maintenance.
- Godot supplies culling, materials, shaders and reflection probes.
- The octree remains small and serializable; all render state is derived.
- Exact `visibletris`, merges and T-joints keep the mesh small: church51's final
  mesh is 107,356 triangles (from 560,314 unculled), so world LOD is unnecessary.

**Trade-offs**

- Chunk rebuilds pay a mesh upload. The budget spreads large edits across frames.
  The current load paths use 512-unit chunks (~108 ms per rebuild); the measured
  128-unit setting is what makes one rebuild fit comfortably in a frame.
- Invalidation is conservative, so some chunk rebuilds are redundant. This is
  intentional: under-invalidation is a correctness bug, over-invalidation is only
  work.
- Lighting is soft, diffuse-only radiance-hint lighting rather than the original's
  baked per-surface lightmaps. This is the price of runtime editability; see
  [ADR 0002](0002-radiance-hints.md).
- Multi-layer blending needs custom shader work; the bottom layer is supported,
  the blendmap-driven blend is deferred.
- Sauerbraten slot/vslot semantics require a one-time mapping to Godot materials.

**Deferred fidelity**

Grass, specular maps, blended layers/blendmaps and the waterfall shader are not
part of the core rendering decision and are tracked in
[features.md](../features.md#post-mvp--deferred-fidelity) /
[known-bugs.md](../known-bugs.md).

---

## Implementation

- `World/Meshing/OctaMesher.cs` — octree → surfaces; chunk and whole-world builds.
- `World/Meshing/ArrayMeshBuilder.cs` — per-vslot surface accumulation, build and
  direct `RenderingServer` upload.
- `World/Meshing/OctaFaceWalker.cs` — shared traversal, region-bounded walking.
- `World/Meshing/OctaMerges.cs`, `OctaTJoints.cs`, `MeshFace.cs` — merges, T-joints,
  winding.
- `World/Meshing/WorldChunkRenderer.cs` — chunk RIDs, dirty tracking, budgeted
  incremental rebuild, radiance invalidation.
- `World/Meshing/MaterialMesher.cs` — water/lava/glass boundary surfaces.
- `World/Materials/` — catalogue/manifest resolution, `calctexgen` UVs, material
  construction, alpha and material-surface shaders, skybox, texture sources.
- `World/Runtime/Formats/Ogz/MapWorld.cs` and
  `World/Runtime/Formats/Save/MapLevel.cs` — compose the renderer with a loaded
  world.

Asserted by `test/OctaMeshTest.cs`, `test/IncrementalMeshTest.cs`,
`test/AlphaMeshTest.cs`, `test/LayerMeshTest.cs`, `test/MaterialBuilderTest.cs`,
`test/EditorViewSceneTest.cs` and the map round-trip tests.
