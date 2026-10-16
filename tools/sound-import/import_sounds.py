#!/usr/bin/env python3
"""Copy the game sounds referenced by data/sounds.cfg and map catalogues into assets/sounds.

Usage:
    import_sounds.py static [--ref /path/to/sauerbraten-code-r6889] [--out assets/sounds]
    import_sounds.py map metadata-catalogue/church51.json
    import_sounds.py all-maps [--catalogues-dir metadata-catalogue]
"""
from __future__ import annotations

import argparse
import glob
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


def _build_arg_parser(repo_root: str) -> argparse.ArgumentParser:
    default_ref = os.environ.get("SAUER_REF", "/Users/fohlen/Downloads/sauerbraten-code-r6889")
    default_out = os.path.join(repo_root, "assets", "sounds")
    default_cat_dir = os.path.join(repo_root, "metadata-catalogue")

    ap = argparse.ArgumentParser(description="Copy game sounds into assets/sounds.")
    sub = ap.add_subparsers(dest="command")

    # subcommand: static
    p_static = sub.add_parser("static", help="Import built-in static sounds from data/sounds.cfg")
    p_static.add_argument("--ref", default=default_ref, help="Path to reference Sauerbraten r6889 tree")
    p_static.add_argument("--out", default=default_out, help="Output directory for sounds")

    # subcommand: map
    p_map = sub.add_parser("map", help="Import static sounds and sounds for specific map(s)")
    p_map.add_argument("catalogues", nargs="+", help="Path(s) to map metadata catalogue JSON file(s)")
    p_map.add_argument("--ref", default=default_ref, help="Path to reference Sauerbraten r6889 tree")
    p_map.add_argument("--out", default=default_out, help="Output directory for sounds")

    # subcommand: all-maps
    p_all = sub.add_parser("all-maps", help="Import static sounds and sounds across all maps in metadata-catalogue/")
    p_all.add_argument("--catalogues-dir", default=default_cat_dir, help="Directory containing metadata catalogue JSON files")
    p_all.add_argument("--ref", default=default_ref, help="Path to reference Sauerbraten r6889 tree")
    p_all.add_argument("--out", default=default_out, help="Output directory for sounds")

    return ap


def main(argv: list[str] | None = None) -> int:
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if argv is None:
        argv = sys.argv[1:]

    # Backwards compatibility: if first arg is not a subcommand (or help flag), handle old invocation
    if argv and argv[0] not in ("static", "map", "all-maps", "-h", "--help"):
        if argv[0].endswith(".json") or os.path.isfile(argv[0]):
            argv = ["map"] + argv
        else:
            argv = ["static"] + argv
    elif not argv:
        argv = ["static"]

    ap = _build_arg_parser(repo_root)
    args = ap.parse_args(argv)

    if not args.command:
        ap.print_help()
        return 1

    sounds_dir = os.path.join(args.ref, "packages", "sounds")
    sounds_cfg = os.path.join(args.ref, "data", "sounds.cfg")

    if not os.path.isdir(sounds_dir):
        print(f"ERROR: reference sounds directory not found: {sounds_dir}", file=sys.stderr)
        return 1

    sound_names = parse_sounds_cfg(sounds_cfg)

    catalogue_paths: list[str] = []
    if args.command == "map":
        catalogue_paths = args.catalogues
    elif args.command == "all-maps":
        if os.path.isdir(args.catalogues_dir):
            catalogue_paths = sorted(glob.glob(os.path.join(args.catalogues_dir, "*.json")))
        else:
            print(f"ERROR: catalogues directory not found: {args.catalogues_dir}", file=sys.stderr)
            return 1

    if catalogue_paths:
        sound_names.extend(parse_catalogues(catalogue_paths))

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
