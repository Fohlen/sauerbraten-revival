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
    import_textures.py static [--ref /path/to/sauerbraten-code-r6889] [--out assets/textures]
    import_textures.py map metadata-catalogue/church51.json
    import_textures.py all-maps [--catalogues-dir metadata-catalogue]
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


def _extract_model_textures(info: dict, paths: set[str]) -> None:
    for key in ("skin", "bump"):
        if info.get(key):
            paths.add(info[key])
    for s in info.get("skins", []):
        if isinstance(s, dict):
            for key in ("diffuse", "mask"):
                if s.get(key):
                    paths.add(s[key])
    for part in info.get("parts", []):
        if isinstance(part, dict):
            _extract_model_textures(part, paths)


def collect_textures(catalogues: list[str], models_manifest: str | None) -> set[str]:
    paths: set[str] = set(EXTRA_TEXTURES)

    for catalogue in catalogues:
        if not os.path.isfile(catalogue):
            continue
        with open(catalogue, encoding="latin-1") as f:
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
            if isinstance(info, dict):
                _extract_model_textures(info, paths)

    return paths


def _build_arg_parser(repo_root: str) -> argparse.ArgumentParser:
    default_ref = os.environ.get("SAUER_REF", "/Users/fohlen/Downloads/sauerbraten-code-r6889")
    default_out = os.path.join(repo_root, "assets", "textures")
    default_models = os.path.join(repo_root, "assets", "models", "models.json")
    default_cat_dir = os.path.join(repo_root, "metadata-catalogue")

    ap = argparse.ArgumentParser(description="Copy textures into the project.")
    sub = ap.add_subparsers(dest="command")

    # subcommand: static
    p_static = sub.add_parser("static", help="Import static engine textures (water, lava, particles, HUD, model skins/bumps)")
    p_static.add_argument("--ref", default=default_ref, help="Path to reference Sauerbraten r6889 tree")
    p_static.add_argument("--out", default=default_out, help="Output directory for textures")
    p_static.add_argument("--models", default=default_models, help="Path to models.json manifest")

    # subcommand: map
    p_map = sub.add_parser("map", help="Import static textures and slot textures for specific map(s)")
    p_map.add_argument("catalogues", nargs="+", help="Path(s) to map metadata catalogue JSON file(s)")
    p_map.add_argument("--ref", default=default_ref, help="Path to reference Sauerbraten r6889 tree")
    p_map.add_argument("--out", default=default_out, help="Output directory for textures")
    p_map.add_argument("--models", default=default_models, help="Path to models.json manifest")

    # subcommand: all-maps
    p_all = sub.add_parser("all-maps", help="Import static textures and slot textures across all maps in metadata-catalogue/")
    p_all.add_argument("--catalogues-dir", default=default_cat_dir, help="Directory containing metadata catalogue JSON files")
    p_all.add_argument("--ref", default=default_ref, help="Path to reference Sauerbraten r6889 tree")
    p_all.add_argument("--out", default=default_out, help="Output directory for textures")
    p_all.add_argument("--models", default=default_models, help="Path to models.json manifest")

    return ap


def main(argv: list[str] | None = None) -> int:
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if argv is None:
        argv = sys.argv[1:]

    # Backwards compatibility: if first arg is not a subcommand (or help flag), treat as 'map'
    if argv and argv[0] not in ("static", "map", "all-maps", "-h", "--help"):
        if argv[0].endswith(".json") or os.path.isfile(argv[0]):
            argv = ["map"] + argv

    ap = _build_arg_parser(repo_root)
    args = ap.parse_args(argv)

    if not args.command:
        ap.print_help()
        return 1

    packages = os.path.join(args.ref, "packages")
    if not os.path.isdir(packages):
        print(f"ERROR: packages dir not found: {packages}", file=sys.stderr)
        return 1

    catalogues: list[str] = []
    if args.command == "map":
        catalogues = args.catalogues
    elif args.command == "all-maps":
        if os.path.isdir(args.catalogues_dir):
            import glob
            catalogues = sorted(glob.glob(os.path.join(args.catalogues_dir, "*.json")))
        else:
            print(f"ERROR: catalogues directory not found: {args.catalogues_dir}", file=sys.stderr)
            return 1

    paths = collect_textures(catalogues, args.models)
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
