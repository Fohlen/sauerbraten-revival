# Vision & Architecture

Sauerbraten Revival — project vision, scope, non-goals, and core technical decisions.
For movement/physics details, see [movement-physics.md](movement-physics.md) and
[ADR 0003](adr/0003-movement-verification.md). For live tracking, see [features.md](features.md).

---

## 1. High-level goal

Take **Cube 2: Sauerbraten** much further than a port: build a **Garry's Mod style
sandbox** — a fully customizable game in **Godot 4 (.NET / C#)** — on top of the
nostalgic Sauerbraten arena FPS feel.

The base layer is non-negotiable: reproduce the original **movement and feel** as
faithfully as possible. If unmodified movement feels wrong, the sandbox has no
foundation to build on, regardless of how good everything else looks.

Above that base, **everything is meant to be changed** — physics, weapons, game
modes and content — through Godot-native data and GDScript. The **in-game, live
editor** is a defining feature, not a nice-to-have, and we deliberately go beyond
the original by making the world **mutable at runtime** (crumbling walls,
collapsing floors, geometry created by game modes).

The target experience is the original's: public lobbies and LAN parties with
friends.

## 2. Scope & MVP modes

- Port the original movement/physics first, proven against the original
  ([movement-physics.md § 5](movement-physics.md#5-verification--validation-plan-a) / [ADR 0003](adr/0003-movement-verification.md)).
- Reuse the original engine only as a **one-time offline source** for content
  conversion — never as a runtime dependency.
- Keep an **editable, dynamic octree** world so live editing and runtime geometry
  changes share one mechanism.
- Make physics, weapons and game modes **moddable**: Godot **Resources** plus
  **GDScript**, never CubeScript.
- Ship through **Steam** using **Steamworks** for networking, lobbies and
  matchmaking, content delivery, and Workshop mods.

### MVP modes

Four modes, all suited to public lobbies and LAN parties with friends: **CTF**,
**Rugby**, **Prop Hunt**, and **Racing**. **Coop edit** is the shared sandbox layer
they sit on. What is done and in what order is tracked in [features.md](features.md),
which wins over this doc on status.

## 3. Non-goals

- Not rewriting the original renderer. Godot renders (Forward+).
- Not re-authoring art from scratch. Original packages → glTF / our format,
  converted offline.
- **No CubeScript, and no classic-map converter.** Classic maps ship as original
  `.ogz` and are read at load time; maps we author use our own format and
  Godot-native configuration. See [ADR 0006](adr/0006-ogz-import.md).
- Not moving the fidelity-critical core into GDScript. GDScript is the modding and
  gameplay surface; the octree, meshing and movement step stay in C#.
- Not targeting 1:1 byte-level map-format compatibility.

## 4. Guiding principles

- Mirror the original implementation's numbers and formulas exactly; when in
  doubt, prefer the original code over a reimplementation.
- One mutation mechanism (serializable commands) for editing, dynamic geometry,
  undo, and networking.
- **Moddable by default**: gameplay-facing behaviour (physics, weapons, modes,
  entities) is data + GDScript, not hardcoded. The engine core stays C# where
  fidelity and determinism demand it.

---

## 5. Technical decisions summary

| Concern | Decision |
| --- | --- |
| Engine | Godot 4 (.NET / C#, Forward+, Jolt) |
| World representation | Native **octree**, **dynamic at runtime** (C#/.NET) |
| Original octree code | Reuse `octa.cpp`/`octa.h` **core**; port editor *algorithms*; do **not** copy `octaedit.cpp` plumbing |
| Rendering | Per-chunk `ArrayMesh` (one surface per vslot, Godot materials); Forward+; see [rendering.md](rendering.md) |
| Collision & movement | Ported octree traversal (`move`/`collide`/steps) in fixed 5 ms steps, validated against the original |
| Level format | Classic maps: shipped `.ogz`, read at load time. Editor maps: `Level.tscn` (refs + config) + external binary `Resource` world blob. **No CubeScript** ([ADR 0006](adr/0006-ogz-import.md)) |
| Editor | Runtime, in-game, Godot UI + **command system** |
| World mutations | All are **serializable commands** (editor, game modes, network) |
| Config | Godot-native: `ProjectSettings`, `ConfigFile`, `Resource`, `InputMap` |
| Networking | Steamworks; replicate movement + commands; **server-authoritative** (see [authority-and-latency.md](authority-and-latency.md)) |
| Modding | **Resources + GDScript** over a versioned API; Workshop packs via `load_resource_pack` |

---

## 6. World model: a dynamic octree

Sauerbraten's octree is a good structure — it was never the problem. `.ogz` and
CubeScript were. We keep octree-style editing and collision, but:

- **We never write `.ogz`.** Classic maps are read from the shipped `.ogz` at load
  time ([ADR 0006](adr/0006-ogz-import.md)); maps we author are saved as a Godot
  binary `Resource`.
- **No `remip`-only statics assumption.** The octree is a *live, mutable* world
  structure, not just an editor-time one.

Rationale: the octree simultaneously serves editing (variable-size cubes),
collision (log-time traversal), and compact storage of large solid regions.
Nothing else gives all three as cleanly for cube geometry.

### Runtime mutability (the "crumbling walls" requirement)

Cube 2's world is effectively static geometry at play time; only a few entity
types move. We explicitly want to overcome this. The world must support
**real-time structural changes**:

- A **race** mode (an MVP mode) where walls crumble, floors fall away, and new
  geometry appears.
- Any game mode can mutate the world through the same command API the editor uses.

Consequences:
- **Mesh updates are incremental** — only dirty chunks are re-meshed, within a
  per-frame budget to avoid hitches.
- **Collision is live** — the movement code always queries the current octree,
  so structural changes affect players immediately.
- **Determinism** — a world mutation is a command; replaying the same command
  stream reconstructs the same world.

---

## 7. World-command system (editor + game modes + network)

Every world mutation is expressed as a `WorldCommand` — there is no "edit
command" distinct from a "game command", only different **sources**:

```
WorldCommand { op, args, source }   // the inverse is derived, not always stored
CommandSource = Editor | GameMode | Script
```

- **Editor UI** submits commands (`CommandSource.Editor`): add cube, delete cube,
  texture face, fill, remip, paste.
- **Game modes** submit commands (`CommandSource.GameMode`; e.g. `crumbling_wall`
  schedules repeated cube removals; `spawn_platform` adds geometry). Modes get a
  safe, data-driven API — not raw memory access.
- **One sink, one log, one total order.** All sources go through the same
  [`WorldCommandSink`](editor-and-commands.md#2-the-worldcommandsink--one-seam-many-drivers-and-sources),
  because they mutate the same octree and races must resolve identically on every
  peer.
- **Undo/redo** resubmits an inverse command (`ApplyInverse`) through the same
  sink — it is *not* a local-only path. In a shared world you may only undo your
  own **editor** commands (`CommandSource.Editor`): another peer's edits and any
  game-mode/script geometry are never undoable. Conflicting undos are rejected
  rather than merged. Full model:
  [editor-and-commands.md §5.5](editor-and-commands.md#55-undo-in-a-shared-world).
- **Networking** separates *world mutations* (reliable, ordered — the edit
  channels; any source) from *game actions* (movement/fire/use — `GAMEPLAY`
  channel). Value edits carry a `ReplaceBlock` payload; **derivation-only ops
  (e.g. `Remip`) are pass-through** — peers re-execute the deterministic
  algorithm, so optimisation patches are tiny. See
  [editor-and-commands.md §3.2](editor-and-commands.md#32-channels--reliability).
- **Authority:** the server is authoritative for edits, movement and combat;
  clients predict for responsiveness. This diverges from Cube 2's
  client-authoritative model deliberately — see
  [authority-and-latency.md](authority-and-latency.md) for why (Steam +
  leaderboard), the latency trade-offs, and the prediction-scope rule it forces.

This one mechanism covers live editing, dynamic geometry, undo, and coop-edit
sync. It is also the foundation for multiplayer and Workshop content.

---

## 8. Reusing the original octree code (feasibility)

| Original file | LOC | GL refs | Action |
| --- | --- | --- | --- |
| `octa.cpp` core | 1880 | **0** | ✅ **Ported to C#** (`World/Octa.cs`, `World/CubeGeometry.cs`) behind a small shim |
| `octa.h` (structs) | — | — | ✅ Ported/adapted to C# (`Cube`, `CubeExt`) |
| `octaedit.cpp` | 3003 | 19 | **Port the algorithms**, reimplement UI/undo/selection plumbing (todo) |
| `octarender.cpp` | 1803 | 45 | ✅ **Replaced** by the ArrayMesh mesher (`World/Meshing/`); see [rendering.md](rendering.md) |
| `worldio.cpp` | 1388 | — | ✅ **Ported for classic-map loading** (`World/Ogz/OgzReader.cs`); see [ADR 0006](adr/0006-ogz-import.md). Maps we author use `Resource`. |
| octree traversal (`physics.cpp`) | ~500 | 0 | ✅ **Ported to C#** (`World/Physics/`: `move`/`collide`/`trystepup`/`trystepdown` + clipping/visibility); see [movement-physics.md](movement-physics.md) and [ADR 0003](adr/0003-movement-verification.md) |

`octa.cpp` depends on only a handful of globals (`worldroot`, `worldsize`,
`worldscale`, `allocnodes`, `lu`, `isempty`, `isentirelysolid`, `octastep`,
`lookupmaterial`) — shimmable. `octaedit.cpp` is entangled with rendering, GUI,
undo, VBOs and compression; copying it would drag in much of the engine. We
therefore port its algorithms and rebuild the plumbing Godot-native.

Language: **C# (.NET)** for the engine core and built-in game — octree, meshing,
collision queries, save/load, command application, editor, HUD, and the default
simulation. C# `int` is 32-bit and `float` is 32-bit, which matches the original
C++ better than GDScript, and the movement port's bit-exactness depends on it.

**GDScript** is the supported **modding surface**: every gameplay-facing seam
(mode rules, weapon behaviour, physics overrides, entity logic) must be reachable
from Resources + GDScript, so a mod can replace it without rebuilding the game.
The A1 reference harness stays C++ as an offline dev tool; see
[ADR 0003](adr/0003-movement-verification.md).

---

## 9. Level format

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
- **History**: the command log (see §7), versioned with the world blob.
- **Entities**: materialized at load as **runtime projections** (`Node.Owner ==
  null`), not serialized into the scene. Their authority is the command log —
  they are a view of it, like render chunks are a view of the octree. Hand-editing
  entities is editor/command-path only.
- **Material manifest**: face/material index → `ShaderMaterial`. No texture-name
  resolution from scripts.

Saved/loaded by the world layer (C#), versioned for forward migration.

This format is what the editor writes and what mods ship. The **unit of distribution, hashing, and multiplayer parity is the archive**
(`.zip`/`.tar`) containing the whole directory.

---

## 10. Material & lighting fidelity (staged)

We work up from approximate to exact, keeping the data model stable so we never
have to rewrite world data to improve rendering:

1. **Approximate**: base texture + tint, single layer, dynamic Godot lighting.
2. **Blended**: Sauerbraten multi-layer material blending and per-face texture
    coordinates.
3. **Exact**: original envmaps, decals, and the full surface model. World
    lighting is a world-space L1 SH probe volume (radiance hints), independent of
    surface topology — see [adr/0002-radiance-hints.md](adr/0002-radiance-hints.md).

The material manifest/shader is the pluggable seam; world data does not change
between stages.

---

## 11. Config, the Godot-native way

| Need | Mechanism |
| --- | --- |
| Input, keybinds | `InputMap` (runtime-remappable) |
| User settings | `ConfigFile` in `user://` |
| Content defs (weapons, modes, entities) | typed `Resource` (`.tres`) |
| Server / game rules | `ConfigFile` / JSON + project overrides |
| Build/editor options | `ProjectSettings` |

No CubeScript, no interpreter, no cross-map ambiguity.

### Keybinds: defaults, remapping, and what is (not yet) bound

Editor/game actions are `InputMap` actions, **not** hardcoded keys, so they can be
rebound. The state today and the intended shape:

- **Defaults** live in `project.godot`'s `[input]` section (editor actions are
  `editor_*`, e.g. `editor_flip`, `editor_mod_face`). This is the Godot-native,
  inspector-visible location.
- **Player remaps are implemented**; the rebinding UI lands with M4e / options
  work. `scripts/InputBindings.cs` owns `user://input.cfg`:
  1. On startup, `Main` loads `InputBindings` and applies each saved action via
     `InputMap.ActionEraseEvents` then `InputMap.ActionAddEvent`.
  2. On rebind, `InputBindings.SaveAction(action)` captures the action's live
     `InputEvent`s and rewrites the file.
  Actions absent from the file keep the `project.godot` defaults; a saved action
  with an empty event list is deliberately unbound. Verified by
  `test/InputBindingsTest.cs` (and `test/EditorInputMapTest.cs`, which rebinds an
  action to prove the mechanism works).

**What is deliberately *not* an `InputMap` action:** mouse-button gestures in the
editor (`MOUSE1/2/3` drag, wheel, `MOUSE6/7`). They need press *and* release with
cursor position, and trackpad scroll is analog (`InputEventPanGesture.factor` /
`.delta`), none of which an `InputMap` action can carry. Cube2 made these
`editbind`s too; if we want them remappable, the shape is a small action→`InputEvent`
table consulted in `_UnhandledInput`, not a pure action lookup. Deferred.

**Trackpad note (macOS):** trackpad / Magic-Mouse scrolling arrives as
`InputEventPanGesture`, never `MouseButton.WheelUp/Down`; both paths are handled
(`EditorController`). `PanNotch` tunes sensitivity.

---

## 12. Modding & Workshop

**Everything gameplay-facing is moddable** — physics, weapons, game modes,
entities, materials and maps — through Godot-native **Resources** and
**GDScript**. There is no bespoke interpreter and no CubeScript.

| Surface | Contents | Notes |
| --- | --- | --- |
| Data (`Resource` / `.tres`) | maps, materials, models, sounds, entity defs, weapon stats, mode parameters, physics tuning | Default; safe to share |
| Script (`GDScript`) | mode rules, weapon/gameplay behaviour, physics overrides, entity logic | Runs in-process — opt in per pack; trusted authors |

- **Fidelity baseline:** the unmodded game is the reference implementation. A mod
  may change physics, but movement parity and the A1–A4 validation always measure
  the unmodified baseline.
- **API stability:** scripts attach to a **versioned API**; the engine core
  (octree, meshing, movement step) exposes stable hooks and validates inputs so a
  mod cannot corrupt the world or the command stream.
- **Package**: a `.pck` (or folder) with a `mod.json` manifest: `id`, `name`,
  `author`, `version`, `api_version`, `dependencies`, `load_priority`, `contents[]`.
- **Loading**: enumerate subscribed Workshop content at startup, validate the
  manifest/API version, load in deterministic order via `load_resource_pack`.
- **UGC loop**: edit a map live in-game → save our format → upload to Workshop →
  subscribers download and play. Maps made in coop edit become Workshop content.
- **Multiplayer parity**: content hashes/IDs in the manifest let matchmaking
  require identical mods/maps. This is also the runtime **map-hash gate**: a
  client whose map hash differs from the server's is refused before play, which
  is what makes prediction-scope a near-non-issue in game mode
  ([authority-and-latency.md §7](authority-and-latency.md#7-the-rule-this-forces-prediction-scope-decided)).

---

## 13. Movement verification (Plan A)

Full detail in [movement-physics.md § 5](movement-physics.md#5-verification--validation-plan-a) and [ADR 0003](adr/0003-movement-verification.md). Summary:

- **A1** — standalone C++ harness running the *original* movement functions at
  fixed 5 ms steps with scripted inputs; emits golden CSV traces.
- **A2** — instrument the original client to log full-precision per-frame state + inputs.
- **A3** — input-replay: one input file drives both engines; diff traces.
- **A4** — determinism check on the original.

All stages (A1–A4) are implemented, verified, and deviation is zero. The port runs
at a fixed 200 Hz step matching the harness and original engine bit-for-bit on
event-free trajectories. Movement fidelity is the foundation; the sandbox is the
product.
