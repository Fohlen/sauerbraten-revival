#!/usr/bin/env python3
"""Copy the game sounds referenced by data/sounds.cfg and map catalogues into assets/sounds.

Usage:
    tools/sound-import/import_sounds.py [metadata-catalogue/*.json]
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys


def parse_sounds_cfg(sounds_cfg_path: str) -> list[str]:
    sound_names: list[str] = []
    if not os.path.isfile(sounds_cfg_path):
        return sound_names
    with open(sounds_cfg_path, "r", encoding="utf-8", errors="ignore") as f:
        for line in f:
            parts = line.strip().split()
            if not parts:
                continue
            cmd = parts[0].lower()
            if cmd in ("registersound", "altsound", "mapsound"):
                name = parts[1].strip('"\'')
                sound_names.append(name)
    return sound_names


def parse_catalogues(catalogue_paths: list[str]) -> list[str]:
    sound_names: list[str] = []
    for cat in catalogue_paths:
        if not os.path.isfile(cat):
            continue
        with open(cat, "r", encoding="utf-8") as f:
            data = json.load(f)
        for s in data.get("sounds", []):
            if isinstance(s, dict) and "path" in s:
                sound_names.append(s["path"])
            elif isinstance(s, str) and s:
                sound_names.append(s)
    return sound_names


def resolve_file(base_dir: str, name: str) -> str | None:
    for ext in ("", ".ogg", ".wav"):
        candidate = os.path.join(base_dir, name + ext)
        if os.path.isfile(candidate):
            return candidate
    return None


def main() -> int:
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    ap = argparse.ArgumentParser()
    ap.add_argument("catalogues", nargs="*", help="Optional metadata catalogue JSON files")
    ap.add_argument("--ref", default=os.environ.get("SAUER_REF", "/Users/fohlen/Downloads/sauerbraten-code-r6889"))
    ap.add_argument("--out", default=os.path.join(repo_root, "assets", "sounds"))
    args = ap.parse_args()

    sounds_dir = os.path.join(args.ref, "packages", "sounds")
    sounds_cfg = os.path.join(args.ref, "data", "sounds.cfg")

    if not os.path.isdir(sounds_dir):
        print(f"ERROR: reference sounds directory not found: {sounds_dir}", file=sys.stderr)
        return 1

    sound_names = parse_sounds_cfg(sounds_cfg)
    if args.catalogues:
        sound_names.extend(parse_catalogues(args.catalogues))

    # Deduplicate while preserving order
    unique_names = list(dict.fromkeys(sound_names))
    copied = 0
    missing = 0

    for name in unique_names:
        src = resolve_file(sounds_dir, name)
        if not src:
            missing += 1
            continue
        rel = os.path.relpath(src, sounds_dir)
        dst = os.path.join(args.out, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        copied += 1

    print(f"copied {copied} sounds to {args.out} ({missing} missing)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
