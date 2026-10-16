#!/usr/bin/env python3
"""Copy the textures referenced by the metadata catalogues into the project.

The runtime/viewer can then load them as Godot-imported Resources
(`res://assets/textures/...`) instead of reading the reference dump at runtime.

Textures come from three places:
  * catalogue `slots[].textures[]` (diffuse/normal/glow/... layer paths),
  * the model manifest skins/bumps (`assets/models/models.json`),
  * a fixed list of engine defaults that aren't in the catalogue
    (`matwater`/`matlava` and the particle textures).

Usage:
    import_textures.py metadata-catalogue/church51.json metadata-catalogue/bt_falls.json
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys

# Engine defaults not present in the catalogue (matwater/matlava + particles).
EXTRA_TEXTURES = [
    "textures/waterfall.jpg", "textures/watern.jpg", "textures/waterdudv.jpg",
    "textures/waterfalln.jpg", "textures/waterfalldudv.jpg",
    "textures/ow/lava_c.jpg", "dg/floor_lava2.jpg",
    "particles/flames.png", "particles/smoke.png", "particles/steam.png",
    "particles/base.png", "particles/snow.png",
    "particles/bullet.png", "particles/scorch.png", "particles/blood.png",
    "hud/items.png", "hud/damage.png", "hud/hbar.png", "hud/radar.png",
    "crosshairs/default.png", "crosshairs/default_hit.png", "crosshairs/default_teammate.png",
]


def collect_textures(catalogues: list[str], models_manifest: str | None) -> set[str]:
    paths: set[str] = set(EXTRA_TEXTURES)

    for catalogue in catalogues:
        with open(catalogue) as f:
            data = json.load(f)
        for slot in data.get("slots", []):
            for tex in slot.get("textures", []):
                path = tex.get("path")
                if path:
                    paths.add(path)

    if models_manifest and os.path.isfile(models_manifest):
        with open(models_manifest) as f:
            models = json.load(f).get("models", {})
        for info in models.values():
            for key in ("skin", "bump"):
                if info.get(key):
                    paths.add(info[key])

    return paths


def main() -> int:
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    ap = argparse.ArgumentParser()
    ap.add_argument("catalogues", nargs="+")
    ap.add_argument("--ref", default=os.environ.get("SAUER_REF", "/Users/fohlen/Downloads/sauerbraten-code-r6889"))
    ap.add_argument("--out", default=os.path.join(repo_root, "assets", "textures"))
    ap.add_argument("--models", default=os.path.join(repo_root, "assets", "models", "models.json"))
    args = ap.parse_args()

    packages = os.path.join(args.ref, "packages")
    if not os.path.isdir(packages):
        print(f"ERROR: packages dir not found: {packages}", file=sys.stderr)
        return 1

    paths = collect_textures(args.catalogues, args.models)
    copied = missing = 0
    for path in sorted(paths):
        src = os.path.join(packages, path)
        dst = os.path.join(args.out, path)
        if not os.path.isfile(src):
            missing += 1
            continue
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        copied += 1

    print(f"copied {copied} textures to {args.out} ({missing} missing from the dump)")

    # Post-process bullet.png if present: Sauerbraten uses <grey> and overbright (blend with neutral 128).
    # In Godot Decal (alpha blend), convert neutral 128 to alpha=0 so it blends seamlessly.
    bullet_dst = os.path.join(args.out, "particles", "bullet.png")
    if os.path.isfile(bullet_dst):
        convert_overbright_bullet(bullet_dst)

    return 0


def convert_overbright_bullet(path: str) -> None:
    import struct
    import zlib
    with open(path, "rb") as f:
        data = f.read()
    idx = 8
    idat = bytearray()
    ihdr_read = False
    width = height = 0
    while idx < len(data):
        length = struct.unpack(">I", data[idx:idx+4])[0]
        ctype = data[idx+4:idx+8].decode("ascii")
        cdata = data[idx+8:idx+8+length]
        if ctype == "IHDR":
            width, height, bitdepth, colortype = struct.unpack(">IIBB", cdata[:10])
            ihdr_read = True
        elif ctype == "IDAT":
            idat.extend(cdata)
        idx += 12 + length

    if not ihdr_read or colortype == 6:  # already RGBA
        return

    decomp = zlib.decompress(idat)
    bpp = 3 if colortype == 2 else 1
    stride = 1 + width * bpp
    out_decomp = bytearray()
    for y in range(height):
        out_decomp.append(0)
        for x in range(width):
            grey = decomp[y * stride + 1 + x * bpp]
            if x == 0 and y == 0:
                grey = 0
            # Alpha is the bullet mark brightness, color is white
            out_decomp.extend([255, 255, 255, grey])

    def chunk(ctype: bytes, cdata: bytes) -> bytes:
        return struct.pack(">I", len(cdata)) + ctype + cdata + struct.pack(">I", zlib.crc32(ctype + cdata))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    png_data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(out_decomp)) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png_data)



if __name__ == "__main__":
    raise SystemExit(main())
