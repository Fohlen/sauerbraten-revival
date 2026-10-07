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
| [dependencies.md](docs/dependencies.md) | Required toolchain (Godot .NET, .NET SDK, Python, assimp, uv, SDL2, …) and why. |
| [known-bugs.md](docs/known-bugs.md) | Known divergences from the original (waterfall double-draw, waterfall shader) and engine rules to preserve. |

## Repository layout

| Path | Contents |
| --- | --- |
| `docs/` | Design, architecture, roadmap, physics reference |
| `scenes/` | Godot scenes (entry `Main.tscn`; `editor_scene.tscn` dev viewer, `player_scene.tscn` play scene) |
| `scripts/` | Game entry (`Main.cs`) |
| `World/` | World core: octree (`Octa`, `CubeGeometry`), [`Ogz/`](World/Ogz) reader + world/player, [`Physics/`](World/Physics) movement + collision, [`Meshing/`](World/Meshing), [`Materials/`](World/Materials), [`Entities/`](World/Entities) |
| `test/` | Unit tests, run by GoDotTest |
| `metadata-catalogue/` | Engine-exported ground truth for imported maps |
| `tools/model-import/` | Converts original models to glTF + a manifest (git-ignored `assets/models/`) |
| `tools/texture-import/` | Imports original textures to Godot Resources (git-ignored `assets/textures/`) |
| `tools/refharness/` | A1 harness, A2 instrumented client, and [reference patches](tools/refharness/patches/README.md) |
| `SauerbratenRevival.csproj` | C# project (Godot .NET) |

## Running the game

Requires the **Godot .NET editor** (`Godot_mono` 4.7) — the standard build can't
load C#. Open the project and run; `dotnet build` also builds the assembly.
`Main.tscn` loads `scenes/player_scene.tscn`, which builds a map and spawns the
player at the map's `ET_PLAYERSTART`.

The player uses the **ported original physics** (`World/Physics/OctaPhysics.cs`),
not Godot collision: WASD + Space, mouse look is captured (Esc releases, click
recaptures). Simulation runs in **fixed 5 ms steps**, caught up by count, and the
frame's mouse delta is applied once per rendered frame — matching the original
(see [movement-verification.md](docs/movement-verification.md)).

## Running tests

Unit tests use [GoDotTest](https://github.com/chickensoft-games/GoDotTest) and run
the game's C# unit tests inside the Godot runtime (not `dotnet test`):

```sh
./run-tests.sh            # headless; set GODOT=/path/to/Godot if needed
```

This is the GoDotTest runner rather than a `dotnet test` adapter because the
world core uses Godot types and lives in the game assembly. Tests are excluded
from `ExportRelease` builds.

## Inspecting a map

`scenes/editor_scene.tscn` is a dev tool: it reads a legacy `.ogz` from the
reference dump, builds the runtime octree, chunks it and displays the geometry
with **diffuse textures** resolved from the committed metadata catalogue
(`metadata-catalogue/<map>.json`) and the map's **cubemap skybox**. It defaults to church51 (`OgzPath` overrides)
and is a `[Tool]` scene, so it also builds in the editor viewport. This is not
the shipping path — the importer converts maps to the native format later.

The original textures and mapmodels are imported once into the project (both
git-ignored, both Godot-imported as Resources):

```sh
tools/model-import/import_models.py metadata-catalogue/church51.json
tools/texture-import/import_textures.py metadata-catalogue/church51.json
```

- `assets/models/` — models converted to glTF with assimp (the viewer loads the
  imported scenes, so they render in the editor too).
- `assets/textures/` — the textures referenced by the catalogues + model skins +
  the engine's material/particle textures; the viewer prefers these Resources and
  falls back to reading the reference dump.

While running, the camera is Sauerbraten's coop-edit flycam: **WASD** flies in the
look direction (look down and you descend), the **mouse** looks/tilts, **Shift**
sprints, **Esc** releases the cursor (click to recapture).

## Reference source

The original engine source is a read-only snapshot of **Sauerbraten SVN r6889**,
kept as the pristine zip archive
`/Users/fohlen/Downloads/sauerbraten-code-r6889.zip` (extracted alongside it for
use). It is not part of this repo and is **never modified** — patches are applied
to a copy at build time. See
[`tools/refharness/patches/README.md`](tools/refharness/patches/README.md) for the
apply/rollback workflow.
