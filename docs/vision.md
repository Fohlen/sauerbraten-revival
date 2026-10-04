# Vision

## High-level goal

Recreate **Cube 2: Sauerbraten** as a playable, shippable game (a "port" /
revival) in **Godot 4**. The single most important thing is to reproduce the
original **movement and feel** as faithfully as possible. If movement feels
wrong, the project has failed regardless of how good everything else looks.

The **in-game, live editor** is a defining feature, not a nice-to-have — and we
deliberately go beyond the original by making the world **mutable at runtime**
(crumbling walls, collapsing floors, geometry created by game modes).

## Scope

- Port the original movement/physics first, and prove it against the original
  (see [movement-verification.md](movement-verification.md)).
- Reuse the original engine only as a **one-time offline source** for content
  conversion — never as a runtime dependency.
- Keep an **editable, dynamic octree** world so live editing and runtime geometry
  changes share one mechanism.
- Ship through **Steam** using **Steamworks** for networking, content delivery,
  and Workshop mods.

## MVP modes

Exactly four: **insta CTF**, **effic CTF**, **FFA**, and **coop edit**.

## Non-goals

- Not rewriting the original renderer. Godot renders.
- Not re-authoring art from scratch.
- **No runtime `.ogz` loading and no CubeScript.** The original file format and
  scripting language are dropped; we define our own format and use Godot-native
  configuration. Old content is converted once, offline.
- Not targeting 1:1 byte-level map-format compatibility.

## Tech stack

| Area | Choice |
| --- | --- |
| Engine | Godot 4 (Forward+, Jolt) |
| World/collision/meshing | C# (.NET) — reusing original octree algorithms |
| Editor UI / game logic | C# (.NET) |
| Networking / content | Steamworks |
| Assets | Original packages → our format / glTF, converted offline |

## Guiding principles

- Mirror the original implementation's numbers and formulas exactly; when in
  doubt, prefer the original code over a reimplementation.
- One mutation mechanism (serializable commands) for editing, dynamic geometry,
  undo, and networking.
- Data-first modding; code mods are an explicit, opt-in tier.
