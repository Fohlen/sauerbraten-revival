# Lightmap golden fixtures (git- LFS)

These are **oracle** artifacts, not inputs we generate: each is an atlas dumped by
the **reference engine's own** `dumplms` command (r6889). Our OGZ lightmap reader
must reproduce them byte-for-byte — see `test/OgzLightmapTest.cs`.

Because `load_world` only calls `LightMap::finalize()` (which clears the pack tree,
not the pixels), a dumped atlas equals the raw bytes stored in the map file.
`dumplms` writes with `savepng(..., flip=true)`, so the PNG rows are vertically
mirrored versus the stored buffer; the test unflips before comparing.

## Files

`lightmap_frag-lab_<i>.png` — 13 atlases for `base/frag-lab` (v33, RGB + one RGBA
`LM_ALPHA` atlas at index 3). Chosen for covering both bpp paths.

## Regenerate

From the reference dump root, with the ogz tooling client
(`tools/ogztool/build.sh`):

```sh
printf 'dumplms\nquit\n' > /tmp/dump.cfg
sauer_client -lbase/frag-lab -x"exec /tmp/dump.cfg"   # writes lightmap_frag-lab_*.png
cp lightmap_frag-lab_*.png <repo>/test/golden/lightmaps/
```

(`-x"exec <file>"` runs the script **after** the map loads; the client is not
headless, so this needs a display.)

## LFS

The `*.png` under `test/golden/**` are tracked by **git-lfs** (see
`.gitattributes`). A fresh clone needs `git lfs install` once; otherwise the files
check out as small pointer text and the tests fail. Run `git lfs pull` if clones
were made without LFS.
