# Adding & converting classic maps

Short, end-to-end recipe for turning an original Sauerbraten map into a shipped
classic map for Sauerbraten Revival. Background and rationale:
[ADR 0006](../../docs/adr/0006-ogz-import.md). Tool internals:
[`tools/ogztool/README.md`](README.md).

There is **no converter**. The game reads `.ogz` directly; this document is about
preparing the original files and their metadata once, before shipping.

## What ships

| Artifact | Committed? | Written by |
| --- | --- | --- |
| `assets/packages/<pkg>/<map>.ogz` | no (asset pack) | copied / normalised |
| `assets/models/`, `assets/textures/` | no (asset pack) | `tools/model-import`, `tools/texture-import` |
| `metadata-catalogue/<map>.json` | **yes** | `tools/ogztool/export-all-maps.sh` |

The runtime resolves a map through [`MapRoots`](../../World/Ogz/MapRoots.cs):
`assets/packages/` first, then the reference dump. Only the first tree ships.

## 1. Build the reference tooling once

The metadata exporter and the old-format normaliser both live in a patched copy
of the reference client. Build it once:

```sh
tools/ogztool/build.sh
```

This copies `$SAUER_REF/src/` into `tools/ogztool/out/`, applies the patches in
`patches/`, and installs `tools/ogztool/out/sauer_client`. The reference dump is
never modified. See [`tools/ogztool/README.md`](README.md#build) for details.

## 2. Copy the original map into the project tree

```sh
cp "$SAUER_REF/packages/<pkg>/<map>.ogz" assets/packages/<pkg>/
```

If the map is already **v32+**, you are done with this step. It can be read as-is.

## 3. Normalise old maps (pre-v32)

Maps older than v32 use the legacy `surfacecompat` format, which the revival
runtime deliberately does not implement. Normalise them with the reference
engine, which recalculates lighting and re-saves at v33:

```sh
tools/ogztool/convert-all-maps.sh <map>       # one map
tools/ogztool/convert-all-maps.sh             # every sub-v32 map
tools/ogztool/convert-all-maps.sh --dry-run   # list without running
```

Output lands in `assets/packages/<pkg>/<map>.ogz`. **`calclight` is mandatory**
and the script runs it before saving, so the `surfaceinfo`/`vertinfo` link and
atlas set are self-consistent. The geometry is preserved.

> Version check: only v32→v33 (material byte → ushort, 9 maps) is a plain
> field-width change; everything below v32 is the legacy surface format. Both go
> through the same step.

## 4. Export the metadata catalogue

The `.ogz` stores indices, not paths; the catalogue is the resolved data the
runtime needs (textures, models, sounds, lighting config, geometry ground truth).
Export it **after** normalising, because `calclight` remips surfaces and the
catalogue's `geometry` block must describe the map the reader actually loads.

```sh
tools/ogztool/export-all-maps.sh -f <map>     # one map
tools/ogztool/export-all-maps.sh -m -f        # the committed test set
```

`<map>.json` is written straight into `metadata-catalogue/`; commit it with the
map. Only the maps the tests exercise are committed (see
[`metadata-catalogue/README.md`](../../metadata-catalogue/README.md)).

## 5. Import the map's models and textures

```sh
tools/model-import/import_models.py  map metadata-catalogue/<map>.json
tools/texture-import/import_textures.py map metadata-catalogue/<map>.json
```

Both write git-ignored output under `assets/`. Use the `static` / `all-maps`
subcommands for the shared built-ins.

## 6. Verify

```sh
"$GODOT" --headless --path . --run-tests=OgzReaderTest --quit-on-finish
./run-tests.sh
```

`OgzReaderTest` asserts the committed catalogue's counts and geometry checksum
against the map; run the full suite before shipping.
