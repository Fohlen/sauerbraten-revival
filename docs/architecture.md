# Architecture

Sauerbraten Revival — core technical decisions. See [movement-physics.md](movement-physics.md)
for the ported movement model and [movement-verification.md](movement-verification.md)
for how we prove movement fidelity.

## Decision summary

| Concern | Decision |
| --- | --- |
| World representation | Native **octree**, **dynamic at runtime** (C#/.NET) |
| Original octree code | Reuse `octa.cpp`/`octa.h` **core**; port editor *algorithms*; do **not** copy `octaedit.cpp` plumbing |
| Rendering | Per-chunk `ArrayMesh` (one surface per vslot, Godot materials); Forward+; RenderingServer streaming is a later optimisation; see [rendering.md](rendering.md) |
| Collision & movement | Our own ported octree traversal (`move`/`collide`/steps), validated against the original |
| Level format | `Level.tscn` (refs + config) + external binary `Resource` world blob. **No `.ogz`, no CubeScript** |
| Editor | Runtime, in-game, Godot UI + **command system** |
| World mutations | All are **serializable commands** (editor, game modes, network) |
| Config | Godot-native: `ProjectSettings`, `ConfigFile`, `Resource`, `InputMap` |
| Networking | Steamworks; replicate movement + commands; server-authoritative world |
| Modding | Data-first Resources + versioned API; Workshop packs via `load_resource_pack` |

> **Status:** [features.md](features.md) is the live inventory; 
> [rendering.md](rendering.md#implementation-status) describes what is built today.

---

## 1. World model: a dynamic octree

Sauerbraten's octree is a good structure — it was never the problem. `.ogz` and
CubeScript were. We keep octree-style editing and collision, but:

- **No octree serialization to `.ogz`.** The octree lives in memory and is saved
  as a Godot binary `Resource`.
- **No `remip`-only statics assumption.** The octree is a *live, mutable* world
  structure, not just an editor-time one.

Rationale: the octree simultaneously serves editing (variable-size cubes),
collision (log-time traversal), and compact storage of large solid regions.
Nothing else gives all three as cleanly for cube geometry.

### Runtime mutability (the "crumbling walls" requirement)

Cube 2's world is effectively static geometry at play time; only a few entity
types move. We explicitly want to overcome this. The world must support
**real-time structural changes**:

- A race mode where walls crumble, floors fall away, and new geometry appears.
- Any game mode can mutate the world through the same command API the editor uses.

Consequences:
- **Mesh updates are incremental** — only dirty chunks are re-meshed, within a
  per-frame budget to avoid hitches.
- **Collision is live** — the movement code always queries the current octree,
  so structural changes affect players immediately.
- **Determinism** — a world mutation is a command; replaying the same command
  stream reconstructs the same world.

---

## 2. Command system (editor + game modes + network)

Every world mutation is expressed as a command:

```
Command { op, args }        // the inverse is derived, not always stored
```

- **Editor UI** issues commands (add cube, delete cube, texture face, fill, remip, paste).
- **Game modes** issue commands (e.g. `crumbling_wall` schedules repeated cube
  removals; `spawn_platform` adds geometry). Modes get a safe, data-driven API —
  not raw memory access.
- **Undo/redo** resubmits an inverse command (`ApplyInverse`) through the same
  sink — it is *not* a local-only path. In a shared world you may only undo your
  own edits, and conflicting undos are rejected rather than merged. Full model:
  [editor-and-commands.md §5.5](editor-and-commands.md#55-undo-in-a-shared-world).
- **Networking** replicates the command stream. Value edits carry a
  `ReplaceBlock` payload; **derivation-only ops (e.g. `Remip`) are pass-through**
  — peers re-execute the deterministic algorithm, so optimisation patches are
  tiny. See [editor-and-commands.md §5.4](editor-and-commands.md#54-remip-and-determinism).
  A server-authoritative world plus client prediction for the local player.

This one mechanism covers live editing, dynamic geometry, undo, and coop-edit
sync. It is also the foundation for multiplayer and Workshop content.

---

## 3. Reusing the original octree code (feasibility)

| Original file | LOC | GL refs | Action |
| --- | --- | --- | --- |
| `octa.cpp` core | 1880 | **0** | ✅ **Ported to C#** (`World/Octa.cs`, `World/CubeGeometry.cs`) behind a small shim |
| `octa.h` (structs) | — | — | ✅ Ported/adapted to C# (`Cube`, `CubeExt`) |
| `octaedit.cpp` | 3003 | 19 | **Port the algorithms**, reimplement UI/undo/selection plumbing (todo) |
| `octarender.cpp` | 1803 | 45 | ✅ **Replaced** by the ArrayMesh mesher (`World/Meshing/`); see [rendering.md](rendering.md) |
| `worldio.cpp` | 1388 | — | ✅ **Ported for OGZ import only** (`World/Ogz/OgzReader.cs`); see [ogz-import.md](ogz-import.md). Native format is `Resource`. |
| octree traversal (`physics.cpp`) | ~500 | 0 | ✅ **Ported to C#** (`World/Physics/`: `move`/`collide`/`trystepup`/`trystepdown` + clipping/visibility); see [movement-verification.md](movement-verification.md) |

`octa.cpp` depends on only a handful of globals (`worldroot`, `worldsize`,
`worldscale`, `allocnodes`, `lu`, `isempty`, `isentirelysolid`, `octastep`,
`lookupmaterial`) — shimmable. `octaedit.cpp` is entangled with rendering, GUI,
undo, VBOs and compression; copying it would drag in much of the engine. We
therefore port its algorithms and rebuild the plumbing Godot-native.

Language: **C# (.NET)** for everything — octree, meshing, collision queries,
save/load, command application, editor, game logic, HUD. C# `int` is 32-bit and
`float` is 32-bit, which matches the original C++ better than GDScript. (The A1
reference harness stays C++ as an offline dev tool; see
[movement-verification.md](movement-verification.md).)

---

## 4. Level format

The map is a **directory of external resources**, addressed by a scene that holds
only scene-level concerns. See [editor-and-commands.md §7](editor-and-commands.md#7-map-format-versioned-command-history)
for the full rationale; the storage tiers (`res://` vs `user://`) and archive
packaging live there too.

```
map/
├── Level.tscn            # refs + config: environment, game-mode settings
├── world.res             # base octree snapshot (Godot binary Resource)
├── history.res           # appended edit commands since world.res (optional)
└── materials/            # material manifest
```

- `Level.tscn`: references the world `Resource` and material manifest as
  **external resources** (by path); holds game-mode settings and environment.
  Editing geometry/entities never rewrites this scene.
- **World blob**: a Godot binary `Resource` (`worldroot` octree + per-face
  material indices + surface data + version field).
- **History**: the command log (see §2), versioned with the world blob.
- **Entities**: materialized at load as **runtime projections** (`Node.Owner ==
  null`), not serialized into the scene. Their authority is the command log —
  they are a view of it, like render chunks are a view of the octree. Hand-editing
  entities is editor/command-path only.
- **Material manifest**: face/material index → `ShaderMaterial`. No texture-name
  resolution from scripts.

Saved/loaded by the world layer (C#), versioned for forward migration.

This format is what the editor writes, what the converter produces, and what mods
ship. The **unit of distribution, hashing, and multiplayer parity is the archive**
(`.zip`/`.tar`) containing the whole directory.

---

## 5. Material & lighting fidelity (staged)

We work up from approximate to exact, keeping the data model stable so we never
have to rewrite world data to improve rendering:

1. **Approximate**: base texture + tint, single layer, dynamic Godot lighting.
2. **Blended**: Sauerbraten multi-layer material blending and per-face texture
   coordinates.
3. **Exact**: original lightmaps, envmaps, decals, and the full surface model.

The material manifest/shader is the pluggable seam; world data does not change
between stages.

---

## 6. Config, the Godot-native way

| Need | Mechanism |
| --- | --- |
| Input, keybinds | `InputMap` (runtime-remappable) |
| User settings | `ConfigFile` in `user://` |
| Content defs (weapons, modes, entities) | typed `Resource` (`.tres`) |
| Server / game rules | `ConfigFile` / JSON + project overrides |
| Build/editor options | `ProjectSettings` |

No CubeScript, no interpreter, no cross-map ambiguity.

---

## 7. Modding & Workshop

**Content mods are easy** — Resources, ConfigFiles, our world format, and model
imports cover maps, materials, entities, weapon stats, and mode parameters.

**Two tiers, explicitly:**

| Tier | Contents | Safety |
| --- | --- | --- |
| Data (default) | maps, materials, models, sounds, entity defs, mode *parameters* | Safe |
| Code (opt-in) | C# compiled against the game assembly — runtime loading TBD | Arbitrary code execution — trusted authors only |

- **Package**: a `.pck` (or folder) with a `mod.json` manifest: `id`, `name`,
  `author`, `version`, `api_version`, `dependencies`, `load_priority`, `contents[]`.
- **Loading**: enumerate subscribed Workshop content at startup, validate the
  manifest/API version, load in deterministic order via `load_resource_pack`.
- **UGC loop**: edit a map live in-game → save our format → upload to Workshop →
  subscribers download and play. Maps made in coop edit become Workshop content.
- **Multiplayer parity**: content hashes/IDs in the manifest let matchmaking
  require identical mods/maps.

---

## 8. Movement verification (Plan A)

Full detail in [movement-verification.md](movement-verification.md). Summary:

- **A1** — standalone C++ harness running the *original* movement functions at
  fixed 5 ms steps with scripted inputs; emits golden CSV traces.
- **A2** — instrument the original client to log full-precision per-frame state + inputs.
- **A3** — input-replay: one input file drives both engines; diff traces.
- **A4** — determinism check on the original.

The port must be run at a fixed 200 Hz step and match the harness. This is
non-negotiable: movement is the product.
