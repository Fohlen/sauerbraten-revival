# Metadata catalogue

Ground-truth metadata for the maps we import into Sauerbraten Revival, exported
from the **original** Sauerbraten client (patched with
[`tools/refharness/patches/ogz-metadata.patch`](../tools/refharness/patches/ogz-metadata.patch)).

Each map gets its own directory:

| File | Contents |
| --- | --- |
| `<map>.ogz` | The legacy map file — input to the importer and the OGZ reader tests. |
| `<map>.json` | Resolved `slots`/`vslots`/`mapmodels`/`entities`/`sounds`, plus a `geometry` block used as ground truth by the OGZ reader tests. |

The exporter only emits what the map actually uses (geometry-referenced textures,
entity-referenced mapmodels and sounds). See [`docs/ogz-import.md`](../docs/ogz-import.md).

## Regenerating

Build the patched reference client once:

```sh
tools/refharness/build_instrumented.sh
```

Then load the map from the reference dump root with `SAUER_METADATA` pointing
straight at the catalogue file:

```sh
cd /path/to/sauerbraten-code-r6889
SAUER_METADATA=/abs/path/to/metadata-catalogue/church51/church51.json \
  /abs/path/to/tools/refharness/instrumented/build/sauer_client \
  -q/tmp/sauer-meta-home -lbase/church51
```

The `geometry` block (cube-tree counts, per-vslot face histogram and a canonical
FNV-1a checksum) is asserted byte-for-byte by
[`test/OgzReaderTest.cs`](../test/OgzReaderTest.cs).
