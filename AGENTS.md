# AGENTS.md — working notes for this repo

**Sauerbraten Revival** — a port/revival of *Cube 2: Sauerbraten* in **Godot 4
(.NET / C#)**. Goal: recreate the original's movement/feel first, then the in-game
editor (a defining feature), then the game. MVP modes: **insta CTF, effic CTF,
FFA, coop edit**. World is a native runtime-mutable **octree**. Legacy content is
converted **once, offline**; the game **never** loads `.ogz` or CubeScript.

## Source of truth

| Question | Read |
| --- | --- |
| What are we building, in what order, status? | [docs/features.md](docs/features.md) — **wins** on ordering/timing/status |
| Why / scope / non-goals / tech stack / architecture | [docs/vision.md](docs/vision.md) |
| Movement/physics fidelity | [docs/movement-physics.md](docs/movement-physics.md), [docs/adr/0003-movement-verification.md](docs/adr/0003-movement-verification.md) |
| Character animation / MD5 models | [docs/adr/0004-character-animation.md](docs/adr/0004-character-animation.md) | MD5 skeletal models: engine model, why runtime `Skeleton3D` mapping deformed the mesh, and the baked GLB approach. |
| Editor + command system | [docs/editor-and-commands.md](docs/editor-and-commands.md) |
| OGZ import / native format | [docs/ogz-import.md](docs/ogz-import.md) |
| Rendering strategy | [docs/rendering.md](docs/rendering.md) |
| Lighting (runtime) | [docs/adr/0002-radiance-hints.md](docs/adr/0002-radiance-hints.md) |
| Networking model | [docs/authority-and-latency.md](docs/authority-and-latency.md) |
| Toolchain requirements | [docs/dependencies.md](docs/dependencies.md) |
| Known divergences to preserve | [docs/known-bugs.md](docs/known-bugs.md) |

If a design doc disagrees with `features.md` about ordering, timing or status,
**`features.md` wins** — fix the other doc.

## Staging & commit guidelines (authoritative)

Work proceeds in **stages**.

1. **Stop at the end of every stage.** Do not begin the next stage until the
   current one has been reviewed.
2. **The reviewer inspects and commits.** The reviewer commits; the author does
   not move ahead on their own.
3. **One stage per commit.** Keep each commit scoped to a single stage so it can
   be understood, reviewed, and reverted independently.
4. **No generated artifacts.** Build output, CSVs, converted assets and engine
   caches are ignored — see `.gitignore`. Never commit `bin/`, `obj/`, `assets/`,
   `tools/*/out/`, or `.godot/`.
5. **Report findings, including blockers.** If a planned step is impractical
   (missing build deps, etc.), surface it for a decision; do **not** silently work
   around it.

Content generation is not the commit: regenerate ignored artifacts, but only the
scripts/sources that produce them belong in git.

## Repository layout

| Path | Contents |
| --- | --- |
| `docs/` | Design, architecture, roadmap, physics reference |
| `scenes/` | Godot scenes (`Main.tscn` entry; `editor_scene.tscn` dev viewer; `player_scene.tscn` play scene; `converter.tscn`) |
| `scripts/` | Game entry (`Main.cs`) |
| `World/` | World core: octree (`Octa`, `CubeGeometry`), `Ogz/` reader + world/player, `Physics/` movement + collision, `Meshing/`, `Materials/`, `Entities/`, `Commands/`, `Editor/`, `Save/` |
| `test/` | Unit tests, run by GoDotTest (compiled into the game assembly) |
| `metadata-catalogue/` | Engine-exported ground truth for imported maps (committed) |
| `tools/model-import/` | Convert original models → glTF + manifest (git-ignored output) |
| `tools/texture-import/` | Import original textures → Godot Resources (git-ignored output) |
| `tools/refharness/` | A1/A2 movement harness, instrumented client, [reference patches](tools/refharness/patches/README.md) |
| `tools/ogztool/` | OGZ [import pipeline](tools/ogztool/README.md): metadata export + pre-v32 → v33 conversion |
| `tools/octaprobe/`, `tools/rayprobe/` | Engine ground-truth probes for octree / raycube |
| `SauerbratenRevival.csproj` | C# project (Godot .NET) |

## Build & run

Requires the **Godot .NET editor** (`Godot_mono` 4.7); the standard build can't
load C#. Open the project and run, or `dotnet build` the assembly.

`Main.tscn` loads `scenes/player_scene.tscn`, which builds a map and spawns the
player at the map's `ET_PLAYERSTART`. The player uses the **ported original
physics** (`World/Physics/OctaPhysics.cs`), **not** Godot collision: WASD + Space,
mouse look captured (Esc releases, click recaptures). Simulation runs in **fixed
5 ms steps**, caught up by count; the frame's mouse delta is applied once per
rendered frame — matching the original
([movement-physics.md](docs/movement-physics.md)).

## Running tests

Tests use [GoDotTest](https://github.com/chickensoft-games/GoDotTest) and run
inside the Godot runtime (**not** `dotnet test`), because the world core uses
Godot types and lives in the game assembly. Tests are excluded from
`ExportRelease`.

**Always run the entire test suite before considering work done — this is not done
automatically** (no CI hook), so it is on you to run it explicitly:

```sh
./run-tests.sh            # headless; set GODOT=/path/to/Godot if needed
```

If `$GODOT` is unset, both the helper and the commands below fall back to
`/Applications/Godot_mono.app/Contents/MacOS/Godot`.

### Running a single suite or test

GoDotTest filters by suite or suite + method. Pass the name to `--run-tests`
with `=`, **not** as a separate argument (a bare name after the flag is ignored
and the whole assembly runs):

```sh
# One suite
"$GODOT" --headless --path . --run-tests=OctaMeshTest --quit-on-finish

# One method
"$GODOT" --headless --path . --run-tests=EditorClipboardTest.ScrollEditIsUndoable --quit-on-finish
```

Note: `run-tests.sh` itself always runs **everything**; use the direct `$GODOT`
commands (or set `GODOT=...`) to filter.

The suite is compute-bound because many suites build full worlds. Two fixtures
are cached: `TestData.ReadMap()` parses each `.ogz` once and hands out deep
clones, and `EmptyMap` can reuse a built-once seed world (enabled via
`GoTestBase`). Both hand out independent, mutable worlds, so tests stay isolated.
**Run only the suite you are touching** for cheap iteration, but do the full
`./run-tests.sh` pass before calling work done (see above).

## Inspecting a map (dev viewer)

`scenes/editor_scene.tscn` reads a legacy `.ogz` from the reference dump, builds
the runtime octree, chunks it and displays it with diffuse textures resolved from
the committed catalogue (`metadata-catalogue/<map>.json`) plus the cubemap skybox.
Defaults to church51 (`OgzPath` overrides). It is a `[Tool]` scene, so it also
builds in the editor viewport. This is **not** the shipping path — the importer
converts to the native format later.

Import original content once (both outputs git-ignored, both Godot Resources):

```sh
tools/model-import/import_models.py metadata-catalogue/church51.json
tools/texture-import/import_textures.py metadata-catalogue/church51.json
```

- `assets/models/` — models converted to glTF with assimp.
- `assets/textures/` — textures from the catalogues + model skins + engine
  material/particle textures; the viewer prefers these and falls back to reading
  the reference dump.

Flycam: **WASD** flies in the look direction, **mouse** looks/tilts, **Shift**
sprints, **Esc** releases the cursor.

## Reference source (read-only)

The original engine is a pristine snapshot of **Sauerbraten SVN r6889**:

```
/Users/fohlen/Downloads/sauerbraten-code-r6889
```

(also kept as `.../sauerbraten-code-r6889.zip`). It is **not** part of this repo
and is **never modified** — patches are applied to a copy at build time. Use it
for lookup when porting original behaviour. Apply/rollback workflow:
[`tools/refharness/patches/README.md`](tools/refharness/patches/README.md)
(movement harness) and [`tools/ogztool/README.md`](tools/ogztool/README.md)
(OGZ import pipeline).

## Verifying against the original

Prefer writing a **small, focused C++ probe** against the r6889 source over
guessing from reading it. Build it in a scratch copy (never in the pristine
tree), have it dump the exact values/behaviour you need, and turn that output
into a golden fixture or a test assertion. `tools/octaprobe/` and
`tools/rayprobe/` are existing examples of this pattern.

Visual problems can't be validated by a probe or by self-inspection. When a
change is about how something *looks*, do not churn alone guessing — **ask the
user to collaborate in small steps**: make one small change, ask them to look at
it and report back, then adjust. Keep each step minimal so feedback stays
specific.

## Ground rules when changing code

- **Original fidelity is the yardstick** for movement/collision/editor
  algorithms. Port from r6889 and cite it; validate against engine ground truth
  (probes, golden fixtures) rather than eyeballing.
- **All world mutations are commands** (`WorldCommand`), shared by editor, game
  modes and scripts — this powers live editing, crumbling geometry and networked
  world changes ([features.md § dynamic world geometry](docs/features.md#cross-cutting-dynamic-world-geometry)).
- **No CubeScript, no runtime `.ogz`.** Config is Godot-native
  (`ProjectSettings`, `ConfigFile`, `Resource`, `InputMap`).
- **Server-authoritative** networking is a hard requirement (leaderboard) — see
  [authority-and-latency.md](docs/authority-and-latency.md).
- Preserve the engine rules listed in
  [known-bugs.md](docs/known-bugs.md) even where they look like bugs.
- Deferred on purpose (do not "fix" opportunistically): `movable.cpp` (SP-only),
  single-player/bot AI, and the post-MVP fidelity list in
  [features.md § Post-MVP](docs/features.md#post-mvp--deferred-fidelity).
