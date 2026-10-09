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
| `a2b-input-trace.patch` | `src/engine/{engine.h,physics.cpp,rendergl.cpp,main.cpp}` | Adds the `SAUER_INPUTTRACE` input/timing logger: raw mouse delta + resulting yaw/pitch (`mousemove`), the main-loop timeline (`elapsedtime`/`scaledtime`/`curtime`/`timeerr`) and the physics scheduler output (`diff`/`physframetime`/`physsteps`). Applied **after** `a2-trace.patch` (it also edits `physics.cpp`); inert unless `SAUER_INPUTTRACE` is set. |
| `a2c-input-replay.patch` | `src/engine/physics.cpp` | Adds the `SAUER_REPLAY` deterministic input replay: reads an A2 trace back as inputs, seeds the row-0 state, then free-runs the original physics. Deterministic (A4) and gives a regenerable reference fixture. Applied **after** `a2b-input-trace.patch`; inert unless `SAUER_REPLAY` is set. |
| `a2d-weapon-events.patch` | `src/fpsgame/{weapon.cpp,game.h}` | Logs weapon recoil (`kick`) and impact push (`hitpush`, also covers being shot) to `*.events`, so A3 can resync at rocket jumps. Applied **after** `a2c-input-replay.patch`; inert unless `SAUER_TRACE` is set. |
| `a2e-move-trace.patch` | `src/engine/physics.cpp` | Adds `SAUER_MOVETRACE=<path>`: dumps the local player's per-substep `move()`/`trystepup()` decision chain (`collide` wall, stepup branch, `findfloor`, smoothdir/scale, clipplane hits) for diffing against the port's `OctaPhysics.MoveTrace`. Applied **after** `a2c-input-replay.patch`; inert unless `SAUER_MOVETRACE` is set. |
| `macos-arm64.patch` | `src/Makefile` | Native arm64 build against Homebrew SDL2 (the Makefile otherwise forces x86_64, which can't run here without Rosetta). |

> The OGZ metadata/conversion patches (`ogz-metadata*.patch`) and the metadata
> batch driver live in [`tools/ogztool`](../ogztool), a separate tool with its own
> patch set and build — they are unrelated to the movement harness. The
> `macos-arm64.patch` above is intentionally duplicated there so each tool builds
> standalone.

All patches are `-p1` and are meant to be applied **from within `src/`** (paths
are `engine/physics.cpp`, `Makefile`). They are made against **r6889 exactly**; a
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
make
```

The client built here carries the A1/A2 movement tooling only. For the OGZ
metadata export and old-map conversion, build [`tools/ogztool`](../ogztool)
instead (a separate patch set and build dir).

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
   ```

3. **Nuclear reset from the zip** — restore just the patched files:

   ```sh
   cd /Users/fohlen/Downloads
   unzip -o sauerbraten-code-r6889.zip \
     "sauerbraten-code-r6889/src/engine/physics.cpp" \
     "sauerbraten-code-r6889/src/engine/engine.h" \
     "sauerbraten-code-r6889/src/engine/worldio.cpp" \
     "sauerbraten-code-r6889/src/Makefile" \
     -d /Users/fohlen/Downloads
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
