#!/usr/bin/env python3
"""Convert a metadata catalogue's used mapmodels to glTF via assimp.

Sauerbraten mapmodels are OBJ/MD2/MD3/MD5/IQM, none of which Godot imports. This
tool resolves each model's source geometry and its Cube2 ``*.cfg`` parameters
(``mdlscale``/``mdltrans``/``mdlglow``/``mdlspec`` + skin), runs ``assimp export``
to produce a ``.glb``, and writes a small manifest the runtime/viewer consumes.

Skeletal MD5 models (``.md5mesh``; player bodies and their view weapons) get extra
manifest fields the flat OBJ/MD2/MD3 models do not need: ``skeleton``, the
per-part ``skins`` (``md5skin <part> "diffuse" "mask"``), the ``md5tag`` bone/tag
map, and the de-duplicated ``anims`` (``md5anim`` name -> ``.md5anim`` path). The
skeleton itself is carried through by assimp; the separate ``.md5anim`` clips are
recorded for a later commit to attach.

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
                    "skin": None, "bump": None, "source": None, "objdir": None,
                    "execs": [], "cfgs": [], "skins": [], "anims": [], "tags": []}
    if not os.path.isfile(path):
        return params
    params["cfgs"].append(path)
    cfg_dir = os.path.dirname(path)
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
            if cmd == "exec" and args:
                params["execs"].append((args[0], cfg_dir))
            elif cmd == "mdlscale" and args:
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
                # objskin * diffuse mask  |  md3skin layer1 "diffuse" "mask"
                # md5skin <part> "diffuse" "mask" [gloss] [glow]  (skelmodel: per-part)
                if cmd == "md5skin" and len(args) >= 2:
                    params["skins"].append({"part": args[0], "diffuse": args[1],
                                            "mask": args[2] if len(args) >= 3 else None})
                if params["skin"] is None and len(args) >= 2:
                    params["skin"] = args[1]
            elif cmd == "objbumpmap" and len(args) >= 2:
                params["bump"] = args[1]
            elif cmd == "md5bumpmap" and len(args) >= 2:
                # md5bumpmap <part> "normal"
                if params["bump"] is None:
                    params["bump"] = args[1]
            elif cmd == "md5anim" and len(args) >= 2:
                # md5anim "name" "file.md5anim" [fps] [flags]
                params["anims"].append({"name": args[0], "file": args[1]})
            elif cmd == "md5tag" and len(args) >= 2:
                # md5tag <bone> <tag>  (e.g. md5tag Weapon tag_weapon)
                params["tags"].append({"bone": args[0], "tag": args[1]})
    return params


def _execute_cfg(params: dict, models_root: str, packages_root: str) -> None:
    """Follow ``exec`` directives (e.g. ``base/neutral/md3.cfg`` -> ``base/base.cfg``)
    and merge the parameters the exec'd file declares.

    Cube2 model dirs often delegate to a shared parent cfg that holds the real
    ``md3load``/``mdlscale`` directives (the ``base`` family, ``armor`` sharing).
    The exec'd cfg's directory is recorded in ``exec_dirs`` so relative paths such
    as ``../base.md3`` resolve against it later.
    """
    pending = [(t, d) for t, d in params.get("execs", [])]
    seen = set(params["cfgs"])
    exec_dirs = params.setdefault("exec_dirs", [])
    while pending:
        target, cfg_dir = pending.pop(0)
        if target.startswith("packages/"):
            path = os.path.join(packages_root, target[len("packages/"):])
        else:
            path = os.path.join(cfg_dir, target)
        path = os.path.normpath(path)
        if path in seen or not os.path.isfile(path):
            continue
        seen.add(path)
        params["cfgs"].append(path)  # remember, so _merge_cfg_params does not re-merge it
        sub = parse_cfg(path)
        sub_dir = os.path.dirname(path)
        exec_dirs.append(sub_dir)
        pending.extend(sub["execs"])
        for key in ("source", "objdir"):
            if not params.get(key) and sub.get(key):
                params[key] = sub[key]
        for key in ("scale", "trans", "glow", "spec", "skin", "bump"):
            if params.get(key) in (None, 1.0, [0.0, 0.0, 0.0]) and sub.get(key) not in (None, 1.0, [0.0, 0.0, 0.0]):
                params[key] = sub[key]
        # List-valued directives accumulate across the exec chain (md5.cfg execs
        # anims.cfg, which holds the md5anim table).
        for key in ("skins", "anims", "tags"):
            params.setdefault(key, []).extend(sub.get(key, []))


def find_model(models_root: str, name: str) -> tuple[str | None, dict]:
    """Resolve a registered model name to a source geometry file + its params.

    Mirrors the engine: try the model dir, then fall back to the parent dir for
    "vert sharing" (e.g. `armor/green` uses `armor/tris.md3` + `armor/md3.cfg`).
    ``exec``-delegating cfgs (the ``base`` family) are followed too.
    """
    packages_root = os.path.dirname(models_root)
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
            _execute_cfg(params, models_root, packages_root)
            if params["source"]:
                resolved = _resolve_source(params, models_root, d)
                if resolved:
                    _merge_cfg_params(params, d)
                    params["cfg_dir"] = d
                    return resolved, params
        for conv, cfg_name in (("tris.obj", "obj.cfg"), ("tris.md2", "md2.cfg"), ("tris.md3", "md3.cfg"),
                               ("tris.md5mesh", "md5.cfg"), ("tris.iqm", "iqm.cfg")):
            source = os.path.join(d, conv)
            if os.path.isfile(source):
                params = parse_cfg(os.path.join(d, cfg_name))
                _execute_cfg(params, models_root, packages_root)
                _merge_cfg_params(params, d)
                params["cfg_dir"] = d
                return source, params

    for ext in (".obj", ".md3", ".md5mesh", ".iqm", ".md2"):
        source = base + ext
        if os.path.isfile(source):
            params = parse_cfg(base + ".cfg")
            _execute_cfg(params, models_root, packages_root)
            params["cfg_dir"] = os.path.dirname(source)
            return source, params

    return None, {}


def _resolve_source(params: dict, models_root: str, name_dir: str) -> str | None:
    """Resolve a ``*load`` source path against objdir then each candidate dir.

    Relative paths like ``../base.md3`` are relative to the cfg that declared
    them, which may be an ``exec``'d parent cfg.
    """
    src = params["source"]
    search_dirs = []
    if params.get("objdir"):
        search_dirs.append(os.path.join(models_root, params["objdir"]))
    search_dirs.append(name_dir)
    # Vert sharing: a variant dir (e.g. snoutx10k/blue) names the mesh that lives
    # in its parent model root (snoutx10k/), so try the parent too.
    parent = os.path.dirname(name_dir)
    if parent and parent != models_root:
        search_dirs.append(parent)
    search_dirs.extend(params.get("exec_dirs", []))
    for d in search_dirs:
        candidate = os.path.normpath(os.path.join(d, src))
        if os.path.isfile(candidate):
            return candidate
    return None


def _merge_cfg_params(params: dict, base: str) -> None:
    # scan any other cfgs in the dir (e.g. obj.cfg alongside md3.cfg)
    seen = set(params.get("cfgs", []))
    for f in sorted(os.listdir(base)):
        if f.endswith(".cfg") and f not in ("obj.cfg", "md3.cfg", "md5.cfg", "md2.cfg", "iqm.cfg"):
            path = os.path.normpath(os.path.join(base, f))
            if path in seen:
                continue
            other = parse_cfg(path)
            for k in ("scale", "trans", "glow", "spec", "skin", "bump"):
                if params.get(k) in (None, 1.0, [0.0, 0.0, 0.0]) and other.get(k) not in (None, 1.0, [0.0, 0.0, 0.0]):
                    params[k] = other[k]
            for k in ("skins", "anims", "tags"):
                params.setdefault(k, []).extend(other.get(k, []))


def default_skin(model_dir: str) -> str | None:
    """MD2/MD3 models often ship a skin.jpg/.png with no cfg directive."""
    for candidate in ("skin.png", "skin.jpg", "skin.jpeg", "skin.tga"):
        if os.path.isfile(os.path.join(model_dir, candidate)):
            return candidate
    return None


def resolve_skin(cfg_skin: str | None, cfg_dir: str, name_dir: str, mesh_dir: str | None = None) -> str | None:
    """Prefer a cfg skin, but fall back to skin.* in the model-name dir (armour).

    A variant cfg (e.g. ``mrfixit/blue``) declares skins that live in the model
    root (``mrfixit/``), so the source-mesh dir is tried before the cfg dir — this
    mirrors the engine resolving texture paths against the model's ``dir``.
    """
    dirs = [d for d in (mesh_dir, cfg_dir, name_dir) if d]
    candidates = []
    if cfg_skin:
        # Cube2 texture paths carry inline tags like <dds> or <mad:...> that must
        # be stripped before touching the filesystem (MD5 models use <dds>).
        clean = re.sub(r"<[^>]*>", "", cfg_skin)
        if clean:
            if os.path.isabs(clean):
                candidates.append(clean)
            else:
                candidates.extend(os.path.join(d, clean) for d in dirs)
    for d in (name_dir, cfg_dir):
        local = default_skin(d)
        if local:
            candidates.append(os.path.join(d, local))
    for candidate in candidates:
        if os.path.isfile(candidate):
            return candidate
    return None


def rel_to_packages(path: str | None, packages_root: str, cfg_dir: str,
                    extra_dirs: list[str] | None = None) -> str | None:
    """Resolve a cfg-declared content path to a ``packages/``-relative slash path.

    A variant cfg can name a texture that physically lives in the model root, so
    candidate dirs are tried in order (the one that actually contains the file
    wins); the resolved location is what gets recorded in the manifest.
    """
    if not path:
        return None
    path = re.sub(r"<[^>]*>", "", path)  # strip Cube2 <mad:...>/<color> tags
    if not path:
        return None
    if os.path.isabs(path):
        full = path
    else:
        dirs = [cfg_dir] + [d for d in (extra_dirs or []) if d]
        full = os.path.join(cfg_dir, path)
        for d in dirs:
            candidate = os.path.normpath(os.path.join(d, path))
            if os.path.isfile(candidate):
                full = candidate
                break
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


def bake_animated_glb(assimp: str, mesh_source: str, anims: dict, packages_root: str, out_glb: str) -> bool:
    """Bake all of a model's ``.md5anim`` clips into one glTF via assimp + merge.

    assimp's MD5 importer loads exactly one sibling ``<meshbase>.md5anim`` per export,
    so we export once per clip (with the clip copied to that name) and merge the
    results with :mod:`gltf_anim_merge`. Godot then imports the single glb with an
    ``AnimationPlayer`` holding every clip, and animates the skeleton itself — which
    is why this is used instead of mapping the engine poses onto Godot's skeleton
    (see docs/character-animation.md).
    """
    import tempfile
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import gltf_anim_merge

    if not anims:
        return False
    base = os.path.splitext(os.path.basename(mesh_source))[0]
    with tempfile.TemporaryDirectory() as tmp:
        mesh_copy = os.path.join(tmp, base + ".md5mesh")
        shutil.copyfile(mesh_source, mesh_copy)
        clip_glbs = []
        for clip, rel in anims.items():
            anim_os = os.path.join(packages_root, rel)
            if not os.path.isfile(anim_os):
                print(f"WARN  {base}: missing clip {rel}", file=sys.stderr)
                continue
            shutil.copyfile(anim_os, os.path.join(tmp, base + ".md5anim"))
            clip_glb = os.path.join(tmp, f"clip_{sanitize(clip)}.glb")
            proc = subprocess.run([assimp, "export", mesh_copy, clip_glb], capture_output=True, text=True)
            if proc.returncode != 0 or not os.path.isfile(clip_glb):
                print(f"WARN  {base}: assimp failed for clip '{clip}'", file=sys.stderr)
                continue
            clip_glbs.append((clip, clip_glb))
        if not clip_glbs:
            return False
        gltf_anim_merge.merge_animations(clip_glbs, out_glb)
    strip_glb_materials(out_glb)
    return True


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
        # game-mode entities: CTF flags (ctf.h), capture bases (capture.h)
        "flags/neutral", "flags/red", "flags/blue",
        "base/neutral", "base/red", "base/blue",
        # monsters (monster.cpp monstertypes)
        "ogro", "monster/rhino", "monster/rat", "monster/slith", "monster/bauul",
        "monster/hellpig", "monster/knight", "monster/goblin", "monster/spider",
        # monster view-weapons (monstertypes vwepname; attached to the tag_weapon bone)
        "ogro/vwep", "monster/rat/vwep", "monster/slith/vwep", "monster/bauul/vwep",
        "monster/knight/vwep", "monster/goblin/vwep",
        # player bodies (render.cpp playermodels; skeletal MD5) + their FFA/team skins
        "mrfixit", "mrfixit/blue", "mrfixit/red",
        "snoutx10k", "snoutx10k/blue", "snoutx10k/red",
        "ogro2", "ogro2/blue", "ogro2/red",
        "inky", "inky/blue", "inky/red",
        "captaincannon", "captaincannon/blue", "captaincannon/red",
        # first-person hands (render.cpp hudguns)
        "mrfixit/hudguns", "snoutx10k/hudguns", "inky/hudguns", "captaincannon/hudguns",
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
            # The engine resolves skin/anim paths against the model's own dir
            # (variant cfgs like mrfixit/blue declare textures that live in the
            # model root), which is the directory of the resolved mesh.
            mesh_dir = os.path.dirname(source)
            record["source"] = os.path.relpath(source, packages).replace(os.sep, "/")
            skin = resolve_skin(params.get("skin"), cfg_dir, name_dir, mesh_dir)
            record["skin"] = rel_to_packages(skin, packages, cfg_dir, extra_dirs=[mesh_dir])
            record["bump"] = rel_to_packages(params.get("bump"), packages, cfg_dir, extra_dirs=[mesh_dir])

            # MD5 (skelmodel) extras: per-part skins, the .md5anim table and the
            # named bone tags the engine attaches sub-models to. The single `skin`
            # above stays the runtime fallback; `skins` preserves the per-part split.
            ext = os.path.splitext(source)[1].lower()
            if ext == ".md5mesh":
                record["skeleton"] = True
                skins = []
                for s in params.get("skins", []):
                    diffuse = rel_to_packages(s.get("diffuse"), packages, cfg_dir, extra_dirs=[mesh_dir])
                    mask = rel_to_packages(s.get("mask"), packages, cfg_dir, extra_dirs=[mesh_dir])
                    if diffuse:
                        skins.append({"part": s["part"], "diffuse": diffuse, "mask": mask})
                if skins:
                    record["skins"] = skins
                if params.get("tags"):
                    record["tags"] = params["tags"]
                if params.get("anims"):
                    # De-duplicated name -> resolved .md5anim path. The cfgs list some
                    # animations more than once (alternate fps/flags); the engine keeps
                    # the last definition, so do the same (dict insertion order preserves
                    # the first-seen order, the value is overwritten to the last).
                    # Anim files may live in an exec'd dir (md5dir / shared anims.cfg,
                    # e.g. snoutx10k uses mrfixit's), so try those too.
                    anim_dirs = [mesh_dir] + list(params.get("exec_dirs", []))
                    anims = {}
                    for a in params["anims"]:
                        anims[a["name"]] = rel_to_packages(a["file"], packages, cfg_dir,
                                                           extra_dirs=anim_dirs)
                    record["anims"] = anims

            glb = os.path.join(args.out, sanitize(name) + ".glb")
            # Skeletal MD5: bake every clip into the glb so Godot's own importer can
            # play them (assimp drops separately-named .md5anim files otherwise; see
            # docs/character-animation.md).
            baked = False
            if ext == ".md5mesh" and record.get("anims"):
                baked = bake_animated_glb(args.assimp, source, record["anims"], packages, glb)
            if not baked:
                proc = subprocess.run([args.assimp, "export", source, glb], capture_output=True, text=True)
                if proc.returncode == 0 and os.path.isfile(glb):
                    strip_glb_materials(glb)
                else:
                    print(f"FAIL  {name}: assimp failed\n{proc.stdout}\n{proc.stderr}", file=sys.stderr)
                    glb = None
            if glb and os.path.isfile(glb):
                record["glb"] = os.path.basename(glb)
                record["animated"] = baked
                print(f"OK    {name} -> {os.path.basename(glb)} (scale={record['scale']}"
                      + (", animated" if baked else "") + ")")
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
