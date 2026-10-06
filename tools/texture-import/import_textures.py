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
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
