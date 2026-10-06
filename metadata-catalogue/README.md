# Metadata catalogue

Ground-truth metadata for the maps we import into Sauerbraten Revival, exported
from the **original** Sauerbraten client (patched with
[`tools/refharness/patches/ogz-metadata.patch`](../tools/refharness/patches/ogz-metadata.patch)).

Only the **JSON** is committed. The legacy `.ogz` files are large binary game
assets and stay in the read-only reference dump; the OGZ reader tests read them
from `$SAUER_REF/packages/base/<map>.ogz`.

Each file holds the resolved `slots`/`vslots`/`mapmodels`/`entities`/`sounds`, plus a
`geometry` block (cube-tree counts, surface/vertex totals, per-vslot face histogram
and a canonical FNV-1a checksum).

| File | Notes |
| --- | --- |
| `church51.json` | 334 entities; the A2 trace fixture (`session1.csv`) was captured here. |
| `bt_falls.json` | 373 entities: 167 mapmodels, 5 `ET_SOUND`, water surfaces; skybox `meister/uzze`. |

The exporter only emits what the map actually uses (geometry-referenced textures,
entity-referenced mapmodels and sounds). See [`docs/ogz-import.md`](../docs/ogz-import.md).

## Regenerating

Build the patched reference client once:

```sh
tools/refharness/build_instrumented.sh
```

Then load the map from the reference dump root, writing the export to `/tmp`
(never into the repo), and copy the JSON in manually:

```sh
cd /path/to/sauerbraten-code-r6889
SAUER_METADATA=/tmp/church51.json \
  /abs/path/to/tools/refharness/instrumented/build/sauer_client \
  -q/tmp/sauer-meta-home -lbase/church51

cp /tmp/church51.json /abs/path/to/metadata-catalogue/church51.json
```

The `geometry` block is asserted by
[`test/OgzReaderTest.cs`](../test/OgzReaderTest.cs), which reads the map itself
from `SAUER_REF` (defaults to `/Users/fohlen/Downloads/sauerbraten-code-r6889`).
