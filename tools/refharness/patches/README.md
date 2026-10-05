# Patches to the reference source

We keep the original Sauerbraten source as a **read-only reference**. We never
edit the extracted dump: patches live in this repo and are applied to a **copy**
at build time. If anything ever goes wrong, the pristine **zip archive** restores
everything.

## Pristine source (the reset button)

```
/Users/fohlen/Downloads/sauerbraten-code-r6889.zip      # original revision r6889
/Users/fohlen/Downloads/sauerbraten-code-r6889/         # extracted dump (reference)
```

The archive's internal prefix is `sauerbraten-code-r6889/`.

## Patches in this directory

| Patch | Applies to | Purpose |
| --- | --- | --- |
| `a2-trace.patch` | `src/engine/physics.cpp`, `src/fpsgame/{game.h,entities.cpp,fps.cpp}` | Adds the `SAUER_TRACE` per-5 ms CSV logger, plus `SAUER_TRACE.events` (map name, jump pads, teleports). fpsgame calls are `#ifndef STANDALONE` so the server build is unaffected. |
| `macos-arm64.patch` | `src/Makefile` | Native arm64 build against Homebrew SDL2 (the Makefile otherwise forces x86_64, which can't run here without Rosetta). |
| `ogz-metadata.patch` | `src/engine/{engine.h,sound.cpp,worldio.cpp,metadata.cpp}`, `src/Makefile` | Adds the `SAUER_METADATA` map-metadata exporter via a new `engine/metadata.cpp`: on map load it writes resolved slots/vslots/mapmodels/entities/sounds as JSON, filtered to what the map actually uses (geometry-referenced slots/vslots, entity-referenced mapmodels/sounds). Inert unless `SAUER_METADATA` is set. |

All patches are `-p1` and are meant to be applied **from within `src/`** (paths
are `engine/physics.cpp`, `Makefile`). Most touch different files;
`macos-arm64.patch` and `ogz-metadata.patch` both edit `Makefile` in disjoint
regions, so any order works. They are made against **r6889 exactly**; a
different revision may need them regenerated.

## Apply (preferred: copy-based, dump untouched)

```sh
tools/refharness/build_instrumented.sh
# optional: SAUER_REF=/path/to/dump tools/refharness/build_instrumented.sh
```

This copies `$SAUER_REF/src/` into `tools/refharness/instrumented/build/`,
applies **every `*.patch` in this directory** (in filename order) there, and
builds a single client that supports all of the tooling below. **The dump is
never modified.**

## Apply (manual, to a copy)

```sh
SRC=/Users/fohlen/Downloads/sauerbraten-code-r6889/src
DST=/tmp/sauer-src
rm -rf "$DST" && cp -R "$SRC" "$DST" && cd "$DST"
patch -p1 < /path/to/repo/tools/refharness/patches/a2-trace.patch
patch -p1 < /path/to/repo/tools/refharness/patches/macos-arm64.patch
patch -p1 < /path/to/repo/tools/refharness/patches/ogz-metadata.patch
make
```

## Running the metadata export

The single client built above carries all patches. `SAUER_TRACE` enables the A2
movement log (see [`../README.md`](../README.md)), and `SAUER_METADATA`
enables the metadata export below; both are inert when unset.

`ogz-metadata.patch` is inert unless `SAUER_METADATA` is set to an output file
path. Build the patched client, then load a map (from the in-game menu, or with
`-l<map>` / the console `map` command); the JSON is written when the map finishes
loading. The client must run from the reference dump root so it can find
`data/`, `packages/` and the map `.cfg`:

```sh
tools/refharness/build_instrumented.sh   # builds the patched client

cd /Users/fohlen/Downloads/sauerbraten-code-r6889
SAUER_METADATA=/tmp/church51.json \
  /Users/fohlen/GodotProjects/sauerbraten-revival/tools/refharness/instrumented/build/sauer_client \
  -q/tmp/sauer-meta-home -lbase/church51

python3 -m json.tool /tmp/church51.json | head
```

`-lbase/church51` loads `packages/base/church51.ogz`; `-q` points the client at a
throwaway home dir so it does not touch `~/.sauerbraten`. Running the client
needs a display and a working OpenGL context (it will not run headless).

The output is consumed by the offline importer; see
[`docs/ogz-import.md`](../../../docs/ogz-import.md).

## Roll back

1. **Copy-based build (normal case)** — nothing to undo in the dump; just delete
   the copy:

   ```sh
   rm -rf tools/refharness/instrumented/build
   ```

2. **Patched in place** — reverse the patches (reverse order, `-R`):

   ```sh
   cd /Users/fohlen/Downloads/sauerbraten-code-r6889/src
   patch -R -p1 < /path/to/repo/tools/refharness/patches/a2-trace.patch
   patch -R -p1 < /path/to/repo/tools/refharness/patches/macos-arm64.patch
   patch -R -p1 < /path/to/repo/tools/refharness/patches/ogz-metadata.patch
   ```

3. **Nuclear reset from the zip** — restore just the patched files:

   ```sh
   cd /Users/fohlen/Downloads
   unzip -o sauerbraten-code-r6889.zip \
     "sauerbraten-code-r6889/src/engine/physics.cpp" \
     "sauerbraten-code-r6889/src/engine/engine.h" \
     "sauerbraten-code-r6889/src/engine/sound.cpp" \
     "sauerbraten-code-r6889/src/engine/worldio.cpp" \
     "sauerbraten-code-r6889/src/Makefile" \
     -d /Users/fohlen/Downloads
   # ogz-metadata.patch also adds a new file; remove it explicitly:
   rm -f /Users/fohlen/Downloads/sauerbraten-code-r6889/src/engine/metadata.cpp
   ```

   …or re-extract the whole thing (≈1 GB):

   ```sh
   cd /Users/fohlen/Downloads
   rm -rf sauerbraten-code-r6889
   unzip -q sauerbraten-code-r6889.zip
   ```

## Verify a file is pristine

Compare the working copy against the archive without extracting:

```sh
unzip -p /Users/fohlen/Downloads/sauerbraten-code-r6889.zip \
  "sauerbraten-code-r6889/src/engine/physics.cpp" | shasum -a 256
shasum -a 256 /Users/fohlen/Downloads/sauerbraten-code-r6889/src/engine/physics.cpp
```

The two hashes must match. (Known-good hash for r6889 `engine/physics.cpp`:
`2a9228dee960d86b87208d9ccdc68cc699db277ff8ac72dc47f78ef2497b6b39`.)

## Generating a new patch

Edit a **copy**, then diff with `a/`/`b/` prefixes so it applies with `-p1`:

```sh
# from the copy's src/ directory, with the original alongside as ORIG/
diff -u --label a/engine/physics.cpp --label b/engine/physics.cpp ORIG/physics.cpp engine/physics.cpp \
  > /path/to/repo/tools/refharness/patches/<name>.patch
```
