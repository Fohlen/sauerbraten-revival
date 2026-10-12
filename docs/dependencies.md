# Dependencies & toolchain

What the project needs, and why. Keep this current whenever a tool is added —
especially the dev-only ones we reach for ad hoc (an image diff, a one-off script),
so they don't become mystery requirements later.

## Building & running the game

| Dependency | Version used | Why | Where |
| --- | --- | --- | --- |
| **Godot .NET (mono)** | `4.7.2.stable.mono` | Engine + C# host. The standard (non-mono) build **cannot load C#**. | `/Applications/Godot_mono.app`; override the test runner with `GODOT=/path/to/Godot` |
| **.NET SDK** | targets `net8.0` (built with 10.0.401) | Compiles `SauerbratenRevival.csproj`. | https://dotnet.microsoft.com/download |
| **Chickensoft.GoDotTest** | `2.0.47` (NuGet) | Test runner; restored automatically by `dotnet build`. | `SauerbratenRevival.csproj` |

Run tests with `./run-tests.sh` (builds, then Godot `--headless --run-tests`).

## Content conversion (offline, dev-only)

| Dependency | Why | Notes |
| --- | --- | --- |
| **Python 3** | `tools/model-import/`, `tools/texture-import/`, `tools/refharness/validate_trace.py` | **Stdlib only** — no pip packages required. |
| **assimp** (CLI) | Converts OBJ/MD2/MD3/MD5/IQM mapmodels to glTF. | `brew install assimp` |
| **uv** *(optional)* | Throwaway Python environments for analysis scripts (e.g. Pillow/NumPy image diffing). Not needed for the pipeline. | `brew install uv` |

## Reference engine (optional — only to regenerate traces/metadata)

Only needed to run the A1/A2 harness (`tools/refharness`) or the OGZ tooling
(`tools/ogztool`, the `SAUER_METADATA` exporter and old-map converter); the game
and its tests otherwise read the committed `metadata-catalogue/` and the
read-only dump.

| Dependency | Why |
| --- | --- |
| **C++ toolchain** (`clang`, `make`) | Builds the patched reference client. |
| **SDL2**, **SDL2_image**, **SDL2_mixer** | Reference client libraries (Homebrew: `sdl2-compat`, `sdl2_image`, `sdl2_mixer`). |
| **patch** | Applies `tools/*/patches/*.patch` to a copy of the source. |
| **unzip** | Restores the pristine reference source from the zip. |

```sh
brew install assimp uv sdl2 sdl2_image sdl2_mixer
# then, as needed:
tools/refharness/build_instrumented.sh   # A1/A2 movement tooling
# The probes link only against the instrumented headers (build_instrumented.sh first):
#   tools/octaprobe/build.sh              # octree ground truth
tools/animprobe/build.sh                  # MD5 animation ground truth
tools/ogztool/build.sh                    # OGZ metadata + old-map conversion
```

## Environment variables & settings

| Setting / Variable | Type | Used by | Default |
| --- | --- | --- | --- |
| `sauerbraten/reference_path` | `ProjectSettings` | In-engine resolver (`MapRoots`), converter wizard default | `/Users/fohlen/Downloads/sauerbraten-code-r6889` |
| `SAUER_REF` | Env var (overrides ProjectSetting) | Tests, viewer, import tools, refharness, ogztool | Unset (falls back to ProjectSetting or default) |
| `GODOT` | Env var | `run-tests.sh` | `/Applications/Godot_mono.app/Contents/MacOS/Godot` |
| `SAUER_TRACE` | Env var | A1/A2 instrumented reference client (refharness) | unset (features inert) |
| `SAUER_METADATA` / `SAUER_METADATA_EXIT` / `SAUER_CONVERT_SAVE` | Env var | OGZ tooling client (ogztool) | unset (features inert) |

## Recreating an ad-hoc analysis environment

When a task needs a throwaway Python environment, prefer `uv` over polluting the
system Python:

```sh
uv venv /tmp/analysis
uv pip install --python /tmp/analysis/bin/python pillow numpy
/tmp/analysis/bin/python - <<'PY'
...
PY
```
