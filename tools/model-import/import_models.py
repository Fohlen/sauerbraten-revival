#!/usr/bin/env python3
"""Convert a metadata catalogue's used mapmodels to glTF via assimp.

Sauerbraten mapmodels are OBJ/MD2/MD3/MD5/IQM, none of which Godot imports. This
tool resolves each model's source geometry and its Cube2 ``*.cfg`` parameters
(``mdlscale``/``mdltrans``/``mdlglow``/``mdlspec`` + skin), runs ``assimp export``
to produce a ``.glb``, and writes a small manifest the runtime/viewer consumes.

The reference dump is never modified; output goes to a build dir (git-ignored).

Usage:
    import_models.py CATALOGUE.json --ref /path/to/sauerbraten-code-r6889 \
        [--out tools/model-import/out] [--assimp assimp]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shlex
import shutil
import struct
import subprocess
import sys


def parse_cfg(path: str) -> dict:
    """Extract the model parameters we care about from a model .cfg."""
    params: dict = {"scale": 1.0, "trans": [0.0, 0.0, 0.0], "glow": None, "spec": None,
                    "skin": None, "bump": None, "source": None, "objdir": None, "cfgs": []}
    if not os.path.isfile(path):
        return params
    params["cfgs"].append(path)
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for raw in f:
            line = raw.split("//", 1)[0].strip()
            if not line:
                continue
            try:
                toks = shlex.split(line)
            except ValueError:
                continue
            if not toks:
                continue
            cmd, args = toks[0], toks[1:]
            if cmd == "mdlscale" and args:
                params["scale"] = int(float(args[0])) / 100.0
            elif cmd == "mdltrans" and len(args) >= 3:
                params["trans"] = [float(args[0]), float(args[1]), float(args[2])]
            elif cmd == "mdlglow" and len(args) >= 3:
                params["glow"] = [int(args[0]), float(args[1]), float(args[2])]
            elif cmd == "mdlspec" and args:
                params["spec"] = int(args[0])
            elif cmd in ("objdir",) and args:
                params["objdir"] = args[0]
            elif cmd in ("objload", "md2load", "md3load", "md5load", "iqmload") and args:
                params["source"] = args[0]
            elif cmd in ("objskin", "md3skin", "md5skin", "md2skin") and args:
                # e.g. objskin * diffuse mask  |  md3skin layer1 "diffuse" "mask"
                if params["skin"] is None and len(args) >= 2:
                    params["skin"] = args[1]
            elif cmd == "objbumpmap" and len(args) >= 2:
                params["bump"] = args[1]
    return params


def find_model(models_root: str, name: str) -> tuple[str | None, dict]:
    """Resolve a registered model name to a source geometry file + its params.

    Mirrors the engine: try the model dir, then fall back to the parent dir for
    "vert sharing" (e.g. `armor/green` uses `armor/tris.md3` + `armor/md3.cfg`).
    """
    base = os.path.join(models_root, name)
    dirs = [base]
    if os.path.isdir(base) and os.path.dirname(base) != models_root:
        dirs.append(os.path.dirname(base))

    for d in dirs:
        if not os.path.isdir(d):
            continue
        for cfg_name in ("obj.cfg", "md3.cfg", "md5.cfg", "md2.cfg", "iqm.cfg"):
            cfg = os.path.join(d, cfg_name)
            if not os.path.isfile(cfg):
                continue
            params = parse_cfg(cfg)
            if params["source"]:
                load_dir = os.path.join(models_root, params["objdir"]) if params["objdir"] else d
                source = os.path.normpath(os.path.join(load_dir, params["source"]))
                if os.path.isfile(source):
                    _merge_cfg_params(params, d)
                    params["cfg_dir"] = d
                    return source, params
        for conv, cfg_name in (("tris.obj", "obj.cfg"), ("tris.md2", "md2.cfg"), ("tris.md3", "md3.cfg"),
                               ("tris.md5mesh", "md5.cfg"), ("tris.iqm", "iqm.cfg")):
            source = os.path.join(d, conv)
            if os.path.isfile(source):
                params = parse_cfg(os.path.join(d, cfg_name))
                _merge_cfg_params(params, d)
                params["cfg_dir"] = d
                return source, params

    for ext in (".obj", ".md3", ".md5mesh", ".iqm", ".md2"):
        source = base + ext
        if os.path.isfile(source):
            params = parse_cfg(base + ".cfg")
            params["cfg_dir"] = os.path.dirname(source)
            return source, params

    return None, {}


def _merge_cfg_params(params: dict, base: str) -> None:
    # scan any other cfgs in the dir (e.g. obj.cfg alongside md3.cfg)
    for f in sorted(os.listdir(base)):
        if f.endswith(".cfg") and f not in ("obj.cfg", "md3.cfg", "md5.cfg", "md2.cfg", "iqm.cfg"):
            other = parse_cfg(os.path.join(base, f))
            for k in ("scale", "trans", "glow", "spec", "skin", "bump"):
                if params.get(k) in (None, 1.0, [0.0, 0.0, 0.0]) and other.get(k) not in (None, 1.0, [0.0, 0.0, 0.0]):
                    params[k] = other[k]


def default_skin(model_dir: str) -> str | None:
    """MD2/MD3 models often ship a skin.jpg/.png with no cfg directive."""
    for candidate in ("skin.png", "skin.jpg", "skin.jpeg", "skin.tga"):
        if os.path.isfile(os.path.join(model_dir, candidate)):
            return candidate
    return None


def resolve_skin(cfg_skin: str | None, cfg_dir: str, name_dir: str) -> str | None:
    """Prefer a cfg skin, but fall back to skin.* in the model-name dir (armour)."""
    candidates = []
    if cfg_skin:
        candidates.append(cfg_skin if os.path.isabs(cfg_skin) else os.path.join(cfg_dir, cfg_skin))
    for d in (name_dir, cfg_dir):
        local = default_skin(d)
        if local:
            candidates.append(os.path.join(d, local))
    for candidate in candidates:
        if os.path.isfile(candidate):
            return candidate
    return None


def rel_to_packages(path: str | None, packages_root: str, cfg_dir: str) -> str | None:
    if not path:
        return None
    path = re.sub(r"<[^>]*>", "", path)  # strip Cube2 <mad:...>/<color> tags
    if not path:
        return None
    full = path if os.path.isabs(path) else os.path.join(cfg_dir, path)
    full = os.path.normpath(full)
    try:
        return os.path.relpath(full, packages_root).replace(os.sep, "/")
    except ValueError:
        return None


def sanitize(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", name).strip("_")


def strip_glb_materials(path: str) -> None:
    """Drop materials/textures from a .glb (we apply the Cube2 skin ourselves).

    Assimp references the source model's internal texture names (e.g. a dangling
    skin.tga), which Godot then tries to open and logs errors for.
    """
    with open(path, "rb") as f:
        data = f.read()
    magic, version, length = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67:
        return

    offset, chunks, json_bytes = 12, [], None
    while offset + 8 <= length:
        clen, ctype = struct.unpack_from("<II", data, offset)
        offset += 8
        chunk = data[offset:offset + clen]
        offset += clen
        chunks.append((ctype, chunk))
        if ctype == 0x4E4F534A:  # 'JSON'
            json_bytes = chunk
    if json_bytes is None:
        return

    doc = json.loads(json_bytes.decode("utf-8"))
    for key in ("materials", "textures", "images", "samplers"):
        doc.pop(key, None)
    for mesh in doc.get("meshes", []):
        for prim in mesh.get("primitives", []):
            prim.pop("material", None)

    new_json = json.dumps(doc, separators=(",", ":")).encode("utf-8")
    new_json += b" " * ((4 - len(new_json) % 4) % 4)

    body = struct.pack("<II", len(new_json), 0x4E4F534A) + new_json
    for ctype, chunk in chunks:
        if ctype == 0x4E4F534A:
            continue
        pad = (4 - len(chunk) % 4) % 4
        body += struct.pack("<II", len(chunk) + pad, ctype) + chunk + b"\0" * pad

    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("catalogue")
    ap.add_argument("--ref", default=os.environ.get("SAUER_REF", "/Users/fohlen/Downloads/sauerbraten-code-r6889"))
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    ap.add_argument("--out", default=os.path.join(repo_root, "assets", "models"))
    ap.add_argument("--assimp", default="assimp")
    args = ap.parse_args()

    packages = os.path.join(args.ref, "packages")
    models_root = os.path.join(packages, "models")
    if not os.path.isdir(models_root):
        print(f"ERROR: models root not found: {models_root}", file=sys.stderr)
        return 1

    with open(args.catalogue) as f:
        catalogue = json.load(f)

    os.makedirs(args.out, exist_ok=True)
    manifest: dict = {"models": {}}

    # Built-in entity models (items, teleporter) referenced by entmdlname, plus
    # the map's own registered mapmodels. Dedupe by name.
    builtins = [
        "teleporter", "health", "boost", "quad",
        "armor/green", "armor/yellow",
        "ammo/shells", "ammo/bullets", "ammo/rockets", "ammo/rrounds", "ammo/grenades", "ammo/cartridges",
        "carrot", "checkpoint",
    ]
    entries = [{"index": -1, "path": name} for name in builtins]
    seen = {e["path"] for e in entries}
    for entry in catalogue.get("mapmodels", []):
        if entry["path"] not in seen:
            entries.append(entry)
            seen.add(entry["path"])

    for entry in entries:
        name = entry["path"]
        source, params = find_model(models_root, name)
        # animmodel::loaded() does `scale /= 4`: Cube2 model units are 4x world units.
        record = {
            "index": entry["index"],
            "source": None,
            "glb": None,
            "scale": params.get("scale", 1.0) / 4.0,
            "trans": params.get("trans", [0.0, 0.0, 0.0]),
            "glow": params.get("glow"),
            "spec": params.get("spec"),
            "skin": None,
            "bump": None,
        }
        if source and os.path.isfile(source):
            cfg_dir = params.get("cfg_dir") or os.path.dirname(source)
            name_dir = os.path.join(models_root, name)
            record["source"] = os.path.relpath(source, packages).replace(os.sep, "/")
            skin = resolve_skin(params.get("skin"), cfg_dir, name_dir)
            record["skin"] = rel_to_packages(skin, packages, cfg_dir)
            record["bump"] = rel_to_packages(params.get("bump"), packages, cfg_dir)
            glb = os.path.join(args.out, sanitize(name) + ".glb")
            proc = subprocess.run([args.assimp, "export", source, glb], capture_output=True, text=True)
            if proc.returncode == 0 and os.path.isfile(glb):
                strip_glb_materials(glb)
                record["glb"] = os.path.basename(glb)
                print(f"OK    {name} -> {os.path.basename(glb)} (scale={record['scale']})")
            else:
                print(f"FAIL  {name}: assimp failed\n{proc.stdout}\n{proc.stderr}", file=sys.stderr)
        else:
            print(f"MISS  {name}: no source geometry")
        manifest["models"][name] = record

    # Merge into any existing manifest so converting map B doesn't drop map A's models.
    manifest_path = os.path.join(args.out, "models.json")
    if os.path.isfile(manifest_path):
        try:
            with open(manifest_path) as f:
                existing = json.load(f).get("models", {})
            existing.update(manifest["models"])
            manifest["models"] = existing
        except (OSError, ValueError):
            pass
    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=2)
    print(f"wrote {manifest_path} ({len(manifest['models'])} models)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
