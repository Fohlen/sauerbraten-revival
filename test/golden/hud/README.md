# HUD icon atlas fixture (git-LFS)

`items.png` is the **original** engine HUD icon atlas from the r6889 reference
dump (`packages/hud/items.png`), copied verbatim so tests can exercise the real
4x4 icon grid without depending on the git-ignored, generated runtime copy under
`assets/textures/` (produced by `tools/texture-import/import_textures.py`).

Used by `test/StatusBarRendererTest.cs` via `ItemsAtlasTextureSource`; see
`StatusBarRenderer.GetIconUv` for the 4x4 -> 16-icon (64x64 cell) mapping.

This directory is covered by the `test/golden/**/*.png` git-LFS filter (see the
top-level `.gitattributes`), so a fresh clone needs `git lfs install` / `git lfs
pull`.

## Regenerate

```sh
cp /path/to/sauerbraten-code-r6889/packages/hud/items.png <repo>/test/golden/hud/items.png
```
