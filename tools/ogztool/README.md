# OGZ tooling client

One-time, **offline** pipeline that turns classic Sauerbraten maps into data the
revival importer can consume. It builds a patched copy of the reference engine
(r6889) and drives it over the map set.

This is deliberately **separate** from [`tools/refharness`](../refharness). Both
build a patched copy of the same reference dump, but they solve unrelated
problems and carry unrelated patch sets:

| Tool | Concern | Env hooks |
| --- | --- | --- |
| [`tools/refharness`](../refharness) | A1/A2 **movement** verification (physics traces, input replay) | `SAUER_TRACE`, `SAUER_INPUTTRACE`, `SAUER_REPLAY`, … |
| **`tools/ogztool`** (this) | **OGZ import**: metadata export + format conversion | `SAUER_METADATA`, `SAUER_METADATA_EXIT`, `SAUER_CONVERT_SAVE` |

They carry separate patch sets and build into separate directories so neither can
break the other. `macos-arm64.patch` is intentionally **duplicated** in both patch
directories: each tool must build standalone on this Mac.

## Build

```sh
tools/ogztool/build.sh
# optional: SAUER_REF=/path/to/dump tools/ogztool/build.sh
```

Copies `$SAUER_REF/src/` into `tools/ogztool/out/build/`, applies every patch in
`patches/` (filename order), builds, and installs the client at
`tools/ogztool/out/sauer_client`. **The reference dump is never modified.**

## Patches

| Patch | Purpose |
| --- | --- |
| `macos-arm64.patch` | Native arm64 build against Homebrew SDL2 (shared with refharness). |
| `ogz-metadata.patch` | Adds `engine/metadata.cpp`: `SAUER_METADATA` exports resolved slots/vslots/mapmodels/entities/sounds as JSON; `SAUER_METADATA_EXIT` quits right after. |
| `ogz-metadata2-convert.patch` | Adds `SAUER_CONVERT_SAVE`: after load, run `calclight()` then `save_world()`, re-writing the map at the current `MAPVERSION`. Applies **after** `ogz-metadata.patch`. |

All patches are `-p1`, applied from within `src/`, and inert unless their env var
is set. `ogz-metadata2-convert.patch` must sort after `ogz-metadata.patch` (it
edits a region the latter adds).

## Two jobs

### 1. Export import metadata

Runs the client once per map; writes `metadata-catalogue/<map>.json` (resolved
texture/vslot/mapmodel/entity/sound data the importer needs).

```sh
tools/ogztool/build.sh
tools/ogztool/export-all-maps.sh            # all maps
tools/ogztool/export-all-maps.sh church51   # one map
```

### 2. Convert old maps to v33

Maps older than v32 use a structurally different, legacy on-disk format
(`surfacecompat`/`convertoldsurfaces`) that the revival runtime deliberately does
**not** implement. The reference engine already owns that whole decode ladder, so
instead of porting it we let the client load each old map, **recalculate** its
lighting, and re-save it as v33.

```sh
tools/ogztool/convert-all-maps.sh           # every sub-v32 map (58 of 335)
tools/ogztool/convert-all-maps.sh campo     # one map
tools/ogztool/convert-all-maps.sh --dry-run # list without running
```

Output lands in `assets/converted/<map>.ogz` (git-ignored; the `base/` package is
always the same, so it is flattened away). Point the importer at `assets/converted/`
first, then the reference dump.

**Why `calclight` is mandatory.** `SAUER_CONVERT_SAVE` runs `calclight(0)` before
saving. `calclight` is what regenerates the modern `surfaceinfo`/`vertinfo` link
and a fresh atlas set that are *consistent with each other*. A converted map
saved without it would carry stale/garbage lightmap links. The recalc also means
the geometry is preserved (recalc must not change it) while the lighting is
re-baked by the reference's own light code — so converted maps light identically
under the T1 import path.

Only the v32→v33 difference is a plain field-width change (material byte → ushort,
9 maps); everything below v32 is the legacy surface format (49 maps). Both go
through the same convert step.

## Requirements

- **patch** and a C++ toolchain (to build).
- A **display + OpenGL context** — the reference client is not headless.
- The reference dump (`$SAUER_REF`, default `~/Downloads/sauerbraten-code-r6889`).
- Python 3 (stdlib only) — the batch drivers read OGZ headers with it.

See [`docs/ogz-import.md`](../../docs/ogz-import.md) for the import pipeline this
feeds and [`docs/lighting.md`](../../docs/lighting.md) for the lightmap model.
