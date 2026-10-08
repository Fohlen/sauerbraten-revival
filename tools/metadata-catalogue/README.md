# Metadata catalogue exporter

Batch driver that runs the instrumented reference client over **every** `.ogz` in
the reference dump and writes one JSON entry per map into
[`metadata-catalogue/`](../../metadata-catalogue/) — the resolved texture/vslot/
mapmodel/sound data the OGZ importer needs (see
[`docs/ogz-import.md`](../../docs/ogz-import.md)).

This is the "do all maps" companion to the single-map manual recipe: it discovers
maps, runs the client once per map, and reports a summary. It does **not** build
the client and does **not** touch the reference dump (maps are loaded read-only).

## Prerequisites

```sh
tools/refharness/build_instrumented.sh   # builds the patched client once
```

The client needs a display and a working OpenGL context (it is not headless), and
takes ~5 s per map — a full run over ~335 maps is ~30 minutes.

## Usage

```sh
# export everything not yet in the catalogue
tools/metadata-catalogue/export-all-maps.sh

# show what would be exported
tools/metadata-catalogue/export-all-maps.sh --dry-run

# (re)export specific maps
tools/metadata-catalogue/export-all-maps.sh --force church51 bt_falls

# force a full refresh
tools/metadata-catalogue/export-all-maps.sh --force
```

| Option | Meaning |
| --- | --- |
| `-n`, `--dry-run` | list the maps that would be exported, then exit |
| `-f`, `--force` | re-export maps whose JSON already exists |
| `-t SECONDS` | per-map timeout (default 120) |
| `-o DIR` | output directory (default `<repo>/metadata-catalogue`) |
| `-h`, `--help` | show help |

Map names may be bare (`church51`) or package-qualified (`base/church51`). With no
arguments, every `*.ogz` under `$SAUER_REF/packages` is exported.

## Environment

| Variable | Default |
| --- | --- |
| `SAUER_REF` | `~/Downloads/sauerbraten-code-r6889` |
| `SAUER_HOME` | `/tmp/sauer-meta-home` (throwaway client home) |
| `CLIENT` | `tools/refharness/instrumented/build/sauer_client` |

## How it works

For each map the driver runs, from the reference dump root:

```sh
SAUER_METADATA=<tmp>.json SAUER_METADATA_EXIT=1 \
  <client> -q$SAUER_HOME -l<package>/<map>
```

`SAUER_METADATA` makes the patched client write the JSON on map load;
`SAUER_METADATA_EXIT` makes it quit immediately afterwards so the batch can move
on without a human closing each window. The output is written atomically (`.tmp`
then rename), so a killed or failed run never leaves a truncated `.json` behind.
Existing entries are skipped unless `--force` is given.

## Regenerating a single entry by hand

The underlying one-map recipe still works if you want to inspect a single load:

```sh
cd "$SAUER_REF"
SAUER_METADATA=/tmp/church51.json SAUER_METADATA_EXIT=1 \
  tools/refharness/instrumented/build/sauer_client -q/tmp/sauer-meta-home -lbase/church51
```

See [`metadata-catalogue/README.md`](../../metadata-catalogue/README.md) for what
the JSON contains and how the committed catalogue is consumed.
