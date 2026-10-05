# Sauerbraten Revival

A revival/port of **Cube 2: Sauerbraten** built in **Godot 4 (.NET / C#)**. The
goal is to recreate the original's iconic movement and feel first, then the in-game
editor (a defining feature) and the game on top of it.

- MVP modes: **insta CTF**, **effic CTF**, **FFA**, **coop edit**.
- World: a native, **runtime-mutable octree** (live editing + dynamic geometry).
- Legacy content is converted **once, offline**; the game never loads `.ogz` or
  interprets CubeScript.

## Project guidelines

> **This is the project guideline.** It is authoritative for all contributors —
> human or LLM — and should be read before acting.

### Staged work, reviewed commits

Work proceeds in **stages**.

1. **Stop at the end of every stage.** Do not begin the next stage until the
   current one has been reviewed.
2. **The reviewer inspects and commits.** The reviewer is the one who commits;
   the author does not move ahead on their own.
3. **One stage per commit.** Keep each commit scoped to a single stage so it can
   be understood, reviewed, and reverted independently.
4. **No generated artifacts.** Build output, CSVs, and engine caches are ignored
   (see `.gitignore`).
5. **Report findings, including blockers.** If a planned step turns out to be
   impractical (missing build deps, etc.), surface it for a decision instead of
   silently working around it.

## Documentation

| Document | Contents |
| --- | --- |
| [vision.md](docs/vision.md) | High-level goal, scope, non-goals, tech stack. |
| [architecture.md](docs/architecture.md) | Core technical decisions: dynamic octree, command system, format, rendering, modding. |
| [features.md](docs/features.md) | Feature inventory and roadmap (MVP modes). |
| [rendering.md](docs/rendering.md) | ArrayMesh + RenderingServer rendering strategy; collision decoupling; LOD. |
| [ogz-import.md](docs/ogz-import.md) | One-time OGZ import pipeline; metadata export patch; native format. |
| [movement-physics.md](docs/movement-physics.md) | Reverse-engineered original movement/physics (SVN r6889), with source citations. |
| [movement-verification.md](docs/movement-verification.md) | Plan A: how we prove movement fidelity against the original (A1–A4). |

## Repository layout

| Path | Contents |
| --- | --- |
| `docs/` | Design, architecture, roadmap, physics reference |
| `scenes/` | Godot scenes (`test_level.tscn`, `player.tscn`) |
| `scripts/` | C# (movement, and later world/game logic) |
| `SauerbratenRevival.csproj` | C# project (Godot .NET) |
| `tools/refharness/` | A1 reference harness running the original movement code |
| `tools/refharness/patches/` | Patches to the reference source + [apply/rollback workflow](tools/refharness/patches/README.md) |

## Running the prototype

Requires the **Godot .NET editor** (`Godot_mono` 4.7) — the standard build can't
load C#. Open the project and run; `dotnet build` also builds the assembly. Input
is WASD + Space; mouse look is captured (Esc releases). Physics runs at 200 Hz to
match the original.

## Reference source

The original engine source is a read-only snapshot of **Sauerbraten SVN r6889**,
kept as the pristine zip archive
`/Users/fohlen/Downloads/sauerbraten-code-r6889.zip` (extracted alongside it for
use). It is not part of this repo and is **never modified** — patches are applied
to a copy at build time. See
[`tools/refharness/patches/README.md`](tools/refharness/patches/README.md) for the
apply/rollback workflow.
