# Metadata catalogue

Ground-truth metadata for the maps we import into Sauerbraten Revival, exported
from the **original** Sauerbraten client (patched with
[`tools/ogztool/patches/ogz-metadata.patch`](../tools/ogztool/patches/ogz-metadata.patch)).

Only the **JSON** is committed. The legacy `.ogz` files are large binary game
assets and stay in the read-only reference dump; the OGZ reader tests read them
from `$SAUER_REF/packages/base/<map>.ogz`.

Each file holds the resolved `slots`/`vslots`/`mapmodels`/`entities`/`sounds`, plus a
`geometry` block (cube-tree counts, surface/vertex totals, per-vslot face histogram
and a canonical FNV-1a checksum).

| File | Notes |
| --- | --- |
| `church51.json` | 334 entities; the default viewer map and the A2 trace fixture. |
| `bt_falls.json` | 373 entities: 167 mapmodels, 5 `ET_SOUND`, water surfaces; skybox `meister/uzze`. |
| `earthstation.json` | 1503 entities, 38 `ET_ENVMAP` probes (envmap tests). |
| `lost.json` | 1723 entities (mode-entity tests). |
| `crnsp1.json` | 8192-unit map (chunk/mesh tests). |

Only the maps the **tests** exercise are committed. A full catalogue is one
`tools/ogztool/export-all-maps.sh` run away (see below); we deliberately do not
carry 335 entries, because the exporter is still gaining fields and stale
entries (old `geometry` checksums) are noise. Add a map here only when a test
needs it.

The exporter only emits what the map actually uses (geometry-referenced
textures, entity-referenced mapmodels and sounds). See
[`docs/ogz-import.md`](../docs/ogz-import.md).

## Regenerating

The batch exporter runs the ogz tooling client once per map and writes the JSON
directly here. Build the client once, then:

```sh
tools/ogztool/build.sh                       # once
tools/ogztool/export-all-maps.sh             # all maps not yet exported
tools/ogztool/export-all-maps.sh -f church51 bt_falls  # refresh some
```

Existing entries are skipped unless `--force` is passed. See
[`tools/ogztool/README.md`](../tools/ogztool/README.md) for options and
environment.

### Regenerating one entry by hand

```sh
cd /path/to/sauerbraten-code-r6889
SAUER_METADATA=/tmp/church51.json SAUER_METADATA_EXIT=1 \
  /abs/path/to/tools/ogztool/out/sauer_client \
  -q/tmp/sauer-meta-home -lbase/church51

cp /tmp/church51.json /abs/path/to/metadata-catalogue/church51.json
```

`SAUER_METADATA_EXIT` makes the client quit right after writing the JSON (the
batch exporter relies on it); omit it to keep the client open.

The `geometry` block is asserted by
[`test/OgzReaderTest.cs`](../test/OgzReaderTest.cs), which reads the map itself
from `SAUER_REF` (defaults to `/Users/fohlen/Downloads/sauerbraten-code-r6889`).
