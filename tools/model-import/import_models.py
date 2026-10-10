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

MD5 models that split the skeleton into animation parts (``md5animpart``) — the
player bodies use this to keep the arms on the weapon ``hold``/``attack`` clips
while the legs play the movement clip — also carry ``animParts`` (the per-part
bone masks) and ``partAnims`` (the per-part clip table, with each clip's engine
fps and selection priority). ``md5pitch`` view-pitch corrections are recorded as
``pitch``. The engine applies both at playback (skelmodel.h ``applybonemask`` /
``calcpitchcorrects``), so the importer only has to preserve them.

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
                    "md5dir": None,
                    "execs": [], "cfgs": [], "skins": [], "anims": [], "tags": [],
                    "animparts": [], "pitches": [], "adjusts": [], "links": []}
    if not os.path.isfile(path):
        return params
    params["cfgs"].append(path)
    cfg_dir = os.path.dirname(path)
    # Animation parts (md5animpart): part 0 is the implicit full-body part; each
    # md5animpart line opens a new part, and every md5anim after it belongs to
    # that part until the next md5animpart (skelmodel.h setanimpart).
    part = 0
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
            elif cmd == "md5dir" and args:
                # md5dir sets the skin/bumpmap base dir (modelcommands::setdir); it may
                # differ from the mesh dir (e.g. vwep/shotg loads its mesh from vwep/
                # but its textures from hudguns/shotg).
                params["md5dir"] = args[0]
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
                # md5anim "name" "file.md5anim" [fps] [priority]. The third value is
                # the engine's skelmodel setanim speed (animmodel.h: 1000/fps ms per
                # frame); the fourth is the per-part selection priority (nothing /
                # 0 means the engine default). The animation belongs to the current
                # md5animpart.
                anim = {"name": args[0], "file": args[1], "part": part}
                if len(args) >= 3:
                    try:
                        anim["fps"] = float(args[2])
                    except ValueError:
                        pass
                if len(args) >= 4:
                    try:
                        anim["priority"] = int(float(args[3]))
                    except ValueError:
                        pass
                params["anims"].append(anim)
            elif cmd == "md5animpart":
                # md5animpart <bone> [<bone> ...]: the new part's bone mask. The
                # engine expands each named bone to its whole subtree (skelmodel.h
                # expandbonemask); later parts override earlier ones for shared bones.
                # The list may arrive as one quoted string (setanimpart explodes it on
                # whitespace) or as separate arguments; normalise both.
                params["animparts"].append([b for arg in args for b in arg.split()])
                part += 1
            elif cmd == "md5pitch" and args:
                # md5pitch <bone> <scale> <offset> <min> <max> (skelmodel.h setpitch).
                # An empty bone name sets the part-wide pitch scale instead of a
                # single bone's. Missing trailing values default to 0 (the engine's
                # min/max then widen to +/-360*|scale|).
                pitch = {"bone": args[0]}
                for i, key in enumerate(("scale", "offset", "min", "max"), start=1):
                    if len(args) > i:
                        try:
                            pitch[key] = float(args[i])
                        except ValueError:
                            pass
                params["pitches"].append(pitch)
            elif cmd == "md5tag" and len(args) >= 2:
                # md5tag <bone> <tag>  (e.g. md5tag Weapon tag_weapon)
                params["tags"].append({"bone": args[0], "tag": args[1]})
            elif cmd == "md5adjust" and args:
                # md5adjust <bone> <yaw> <pitch> <roll> [tx ty tz] (skelmodel.h setadjust).
                # Rotates/translates a bone's bind+anim pose; the engine applies it per
                # animation frame, so it is recorded here and composed offline.
                adj = {"bone": args[0]}
                for i, key in enumerate(("yaw", "pitch", "roll", "tx", "ty", "tz"), start=1):
                    if len(args) > i:
                        try:
                            adj[key] = float(args[i])
                        except ValueError:
                            pass
                params["adjusts"].append(adj)
            elif cmd == "md5link" and len(args) >= 3:
                # md5link <parent> <child> <tag> [x y z] (skelmodel.h setlink): attach
                # the child mesh part to a tag on the parent part (hudguns link the
                # weapon mesh to the hands' tag_bicep/tag_weapon).
                link = {"parent": int(float(args[0])), "child": int(float(args[1])), "tag": args[2]}
                if len(args) >= 6:
                    try:
                        link["translate"] = [float(args[3]), float(args[4]), float(args[5])]
                    except ValueError:
                        pass
                params["links"].append(link)
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
        for key in ("scale", "trans", "glow", "spec", "skin", "bump", "md5dir"):
            if params.get(key) in (None, 1.0, [0.0, 0.0, 0.0]) and sub.get(key) not in (None, 1.0, [0.0, 0.0, 0.0]):
                params[key] = sub[key]
        # List-valued directives accumulate across the exec chain (md5.cfg execs
        # anims.cfg, which holds the md5anim table). An exec'd file numbers its own
        # animation parts from 0, so shift its md5anim part indices past the parts
        # the parent already declared before merging.
        part_offset = len(params.setdefault("animparts", []))
        for anim in sub.get("anims", []):
            merged = dict(anim)
            merged["part"] = merged.get("part", 0) + part_offset
            params["anims"].append(merged)
        params["animparts"].extend(sub.get("animparts", []))
        params.setdefault("pitches", []).extend(sub.get("pitches", []))
        for key in ("skins", "tags", "adjusts", "links"):
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
    # md5dir may precede md5load in the cfg (modelcommands::setdir); fall back to it.
    if params.get("md5dir"):
        search_dirs.append(os.path.join(models_root, params["md5dir"]))
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
            for k in ("scale", "trans", "glow", "spec", "skin", "bump", "md5dir"):
                if params.get(k) in (None, 1.0, [0.0, 0.0, 0.0]) and other.get(k) not in (None, 1.0, [0.0, 0.0, 0.0]):
                    params[k] = other[k]
            part_offset = len(params.setdefault("animparts", []))
            for anim in other.get("anims", []):
                merged = dict(anim)
                merged["part"] = merged.get("part", 0) + part_offset
                params.setdefault("anims", []).append(merged)
            params["animparts"].extend(other.get("animparts", []))
            params.setdefault("pitches", []).extend(other.get("pitches", []))
            for k in ("skins", "tags"):
                params.setdefault(k, []).extend(other.get(k, []))


def default_skin(model_dir: str) -> str | None:
    """MD2/MD3 models often ship a skin.jpg/.png with no cfg directive."""
    for candidate in ("skin.png", "skin.jpg", "skin.jpeg", "skin.tga"):
        if os.path.isfile(os.path.join(model_dir, candidate)):
            return candidate
    return None


def resolve_skin(cfg_skin: str | None, cfg_dir: str, name_dir: str, mesh_dir: str | None = None,
                 tex_dir: str | None = None) -> str | None:
    """Prefer a cfg skin, but fall back to skin.* in the model-name dir (armour).

    A variant cfg (e.g. ``mrfixit/blue``) declares skins that live in the model
    root (``mrfixit/``), so the source-mesh dir is tried before the cfg dir — this
    mirrors the engine resolving texture paths against the model's ``dir``.
    ``tex_dir`` is the ``md5dir`` base (set by ``modelcommands::setdir``), which can
    differ from the mesh dir (``vwep/<gun>`` loads its textures from ``hudguns/<gun>``).
    """
    dirs = [d for d in (tex_dir, mesh_dir, cfg_dir, name_dir) if d]
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


def build_anim_metadata(params: dict, packages: str, cfg_dir: str, mesh_dir: str) -> dict:
    """Build an MD5 model's manifest animation fields from parsed cfg params.

    Returns the flat ``anims``/``animSpeeds`` table plus, when the model declares
    ``md5animpart``, the ``animParts``/``partAnims`` per-part tables, and any
    ``md5pitch`` correction as ``pitch``.
    """
    out: dict = {}
    if params.get("anims"):
        # Anim files may live in an exec'd dir (md5dir / shared anims.cfg, e.g.
        # snoutx10k uses mrfixit's), so try those too.
        anim_dirs = [mesh_dir] + list(params.get("exec_dirs", []))
        anims = {}
        anim_speeds = {}
        n_parts = len(params.get("animparts", [])) + 1
        part_anims = [dict() for _ in range(n_parts)]
        for a in params["anims"]:
            resolved = rel_to_packages(a["file"], packages, cfg_dir, extra_dirs=anim_dirs)
            anims[a["name"]] = resolved
            # Keep the last definition's speed (the engine's md5anim loader overwrites
            # by name); 0/absent means the engine default.
            if a.get("fps", 0.0) > 0.0:
                anim_speeds[a["name"]] = a["fps"]
            else:
                anim_speeds.pop(a["name"], None)
            part = a.get("part", 0)
            if 0 <= part < n_parts:
                clip = {"file": resolved}
                if a.get("fps", 0.0) > 0.0:
                    clip["fps"] = a["fps"]
                if a.get("priority"):
                    clip["priority"] = a["priority"]
                part_anims[part][a["name"]] = clip
        out["anims"] = anims
        if anim_speeds:
            out["animSpeeds"] = anim_speeds
        # md5animpart: the explicit bone masks (part 0 is the implicit full-body
        # complement) and the per-part clip table the runtime needs to reproduce
        # calcanim()'s per-part selection.
        if params.get("animparts"):
            out["animParts"] = [{"mask": bones} for bones in params["animparts"]]
            out["partAnims"] = part_anims
    if params.get("pitches"):
        # md5pitch: per-bone (or part-wide, empty bone) view-pitch correction that
        # tracks the running pitch (skelmodel.h interpbones).
        out["pitch"] = [
            {"bone": p["bone"],
             "scale": p.get("scale", 0.0),
             "offset": p.get("offset", 0.0),
             "min": p.get("min", 0.0),
             "max": p.get("max", 0.0)}
            for p in params["pitches"]
        ]
    return out


# ---- md5adjust: bake the engine's per-bone bind/animation corrections ----


def _qmul(a: tuple, b: tuple) -> tuple:
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def _qaxis(axis: tuple, degrees: float) -> tuple:
    import math
    rad = math.radians(degrees)
    s = math.sin(rad / 2.0)
    return (s * axis[0], s * axis[1], s * axis[2], math.cos(rad / 2.0))


def _qrotate(q: tuple, v: tuple) -> tuple:
    x, y, z, w = q
    vx, vy, vz = v
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


def _restorew(x: float, y: float, z: float) -> float:
    import math
    w = 1.0 - x * x - y * y - z * z
    return 0.0 if w < 0.0 else -math.sqrt(w)


def _floats(text: str) -> list[float]:
    out = []
    for tok in text.replace("(", " ").replace(")", " ").split():
        try:
            out.append(float(tok))
        except ValueError:
            pass
    return out


def _parse_md5anim(text: str) -> dict:
    """Parse the pieces of an ``.md5anim`` we need to rewrite; values are kept in the
    engine's *logical* (post sign-convention) space."""
    lines = text.splitlines()
    data = {"numFrames": 0, "numJoints": 0, "frameRate": 24.0,
            "hierarchy": [], "bounds": [], "baseframe": [], "frames": []}
    i = 0
    while i < len(lines):
        s = lines[i].strip()
        if s.startswith("numFrames"):
            data["numFrames"] = int(float(s.split()[1]))
        elif s.startswith("numJoints"):
            data["numJoints"] = int(float(s.split()[1]))
        elif s.startswith("frameRate"):
            data["frameRate"] = float(s.split()[1])
        elif s.startswith("hierarchy"):
            i += 1
            while i < len(lines) and lines[i].strip() != "}":
                m = re.match(r'"([^"]+)"\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)', lines[i].strip())
                if m:
                    data["hierarchy"].append((m.group(1), int(m.group(2)),
                                              int(m.group(3)), int(m.group(4))))
                i += 1
        elif s.startswith("bounds"):
            i += 1
            while i < len(lines) and lines[i].strip() != "}":
                data["bounds"].append(lines[i].strip())
                i += 1
        elif s.startswith("baseframe"):
            i += 1
            while i < len(lines) and lines[i].strip() != "}":
                v = _floats(lines[i])
                if len(v) >= 6:  # stored -> logical (negate y, qx, qz)
                    data["baseframe"].append(((v[0], -v[1], v[2]),
                                              (-v[3], v[4], -v[5], _restorew(-v[3], v[4], -v[5]))))
                i += 1
        elif s.startswith("frame "):
            i += 1
            frame_floats = []
            while i < len(lines) and lines[i].strip() != "}":
                frame_floats.extend(_floats(lines[i]))
                i += 1
            data["frames"].append(frame_floats)
        i += 1
    return data


def _compose_frame(data: dict, frame_floats: list[float]) -> list[tuple]:
    """Reconstruct each joint's parent-relative ``(pos, quat)`` for one frame."""
    joints = []
    for k, (_name, _parent, flags, start) in enumerate(data["hierarchy"]):
        px, py, pz = data["baseframe"][k][0]
        qx, qy, qz = data["baseframe"][k][1][:3]
        # The engine's start index advances in flag order (bit0=x .. bit5=qz).
        d = start
        if flags & 1:
            px = frame_floats[d]; d += 1
        if flags & 2:
            py = -frame_floats[d]; d += 1
        if flags & 4:
            pz = frame_floats[d]; d += 1
        if flags & 8:
            qx = -frame_floats[d]; d += 1
        if flags & 16:
            qy = frame_floats[d]; d += 1
        if flags & 32:
            qz = -frame_floats[d]; d += 1
        qw = _restorew(qx, qy, qz)
        joints.append(((px, py, pz), (qx, qy, qz, qw)))
    return joints


def apply_md5adjust(anim_text: str, adjusts: list[dict]) -> str:
    """Bake the engine's ``md5adjust`` corrections into an ``.md5anim``.

    The engine applies each adjustment to the *composed* joint (baseframe overridden
    per frame) before subtracting the mesh bind (md5.h ``loadanim``): a yaw/pitch/roll
    rotation about Z/-Y/-X plus a translation (divided by 4), in the joint's parent
    frame. assimp has no such concept, so we apply it to every frame here. assimp
    already reproduces the unadjusted clips, so transforming the clip input matches
    the engine's rendered pose.
    """
    if not adjusts:
        return anim_text
    data = _parse_md5anim(anim_text)
    name_to_index = {name: k for k, (name, *_rest) in enumerate(data["hierarchy"])}
    adjust_by_index = {}
    for adj in adjusts:
        k = name_to_index.get(adj.get("bone"))
        if k is not None:
            adjust_by_index[k] = adj
    if not adjust_by_index:
        return anim_text

    out_frames = []
    for frame_floats in data["frames"]:
        joints = _compose_frame(data, frame_floats)
        for k, adj in adjust_by_index.items():
            yaw, pitch, roll = adj.get("yaw", 0.0), adj.get("pitch", 0.0), adj.get("roll", 0.0)
            a = _qmul(_qmul(_qaxis((-1.0, 0.0, 0.0), roll), _qaxis((0.0, -1.0, 0.0), pitch)),
                      _qaxis((0.0, 0.0, 1.0), yaw))
            pos, quat = joints[k]
            pos = _qrotate(a, pos)
            pos = (pos[0] + adj.get("tx", 0.0) / 4.0,
                   pos[1] + adj.get("ty", 0.0) / 4.0,
                   pos[2] + adj.get("tz", 0.0) / 4.0)
            joints[k] = (pos, _qmul(a, quat))
        out_frames.append(joints)

    # Re-emit with every joint fully specified (flags 63), which keeps the byte layout
    # simple and is valid MD5. Values are converted back to the stored sign convention.
    out = ["MD5Version 10", 'commandline ""', "",
           f"numFrames {len(out_frames)}", f"numJoints {len(data['hierarchy'])}",
           f"frameRate {data['frameRate']:g}",
           f"numAnimatedComponents {6 * len(data['hierarchy'])}", "", "hierarchy {"]
    for k, (name, parent, _flags, _start) in enumerate(data["hierarchy"]):
        out.append(f'\t"{name}"\t{parent} 63 {6 * k}\t//')
    out += ["}", "", "bounds {"]
    out += ["\t" + b for b in data["bounds"][:len(out_frames)]]
    out += ["}", "", "baseframe {"]

    def stored(joint):
        (px, py, pz), (qx, qy, qz, qw) = joint
        if qw > 0.0:
            qx, qy, qz = -qx, -qy, -qz
        return (px, -py, pz), (-qx, qy, -qz)

    for k in range(len(data["hierarchy"])):
        (sp, sq) = stored(data["baseframe"][k])
        out.append(f"\t( {sp[0]:.6f} {sp[1]:.6f} {sp[2]:.6f} ) ( {sq[0]:.6f} {sq[1]:.6f} {sq[2]:.6f} )")
    out.append("}")
    out.append("")
    for fi, joints in enumerate(out_frames):
        out.append(f"frame {fi} {{")
        for joint in joints:
            sp, sq = stored(joint)
            out.append(f"\t{sp[0]:.6f} {sp[1]:.6f} {sp[2]:.6f} {sq[0]:.6f} {sq[1]:.6f} {sq[2]:.6f}")
        out.append("}")
        out.append("")
    return "\n".join(out) + "\n"


def bake_animated_glb(assimp: str, mesh_source: str, anims: dict, packages_root: str,
                      out_glb: str, adjusts: list[dict] | None = None) -> bool:
    """Bake all of a model's ``.md5anim`` clips into one glTF via assimp + merge.

    assimp's MD5 importer loads exactly one sibling ``<meshbase>.md5anim`` per export,
    so we export once per clip (with the clip copied to that name) and merge the
    results with :mod:`gltf_anim_merge`. Godot then imports the single glb with an
    ``AnimationPlayer`` holding every clip, and animates the skeleton itself — which
    is why this is used instead of mapping the engine poses onto Godot's skeleton
    (see docs/character-animation.md). ``adjusts`` (``md5adjust``) is baked into each
    clip before export.
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
            anim_target = os.path.join(tmp, base + ".md5anim")
            if adjusts:
                with open(anim_os, "r", encoding="utf-8", errors="replace") as f:
                    transformed = apply_md5adjust(f.read(), adjusts)
                with open(anim_target, "w", encoding="utf-8") as f:
                    f.write(transformed)
            else:
                shutil.copyfile(anim_os, anim_target)
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


def parse_linked_parts(model_name: str, models_root: str, packages_root: str) -> tuple[list[dict], dict]:
    """Parse a multi-part (``md5link``) model into its per-mesh parts, in cfg order.

    Cube2's first-person ``hudguns`` are one engine model assembled from two MD5
    meshes: the player's ``hands`` and the ``weapon``. The weapon part is attached
    to a tag on the hands part with ``md5link <parent> <child> <tag>``. Every
    ``md5skin``/``md5anim``/``md5tag``/``md5adjust`` applies to the most recently
    ``md5load``-ed part (skelmodel.h reads ``parts.last()``), so this follows
    ``exec`` inline and tracks a "current part" to preserve that ordering.

    Returns ``(parts, model)``: ``parts`` carry the resolved source, per-part
    skins/bump/anims/tags/adjusts; ``model`` carries the model-wide
    ``scale``/``trans`` and the ``links`` list. Paths are ``packages/``-relative.
    """
    parts: list[dict] = []
    model = {"scale": 1.0, "trans": [0.0, 0.0, 0.0], "links": [], "md5dir": None}
    seen: set[str] = set()
    name_dir = os.path.join(models_root, model_name)

    def candidates(cfg_dir: str) -> list[str]:
        dirs = []
        if model.get("md5dir"):
            dirs.append(os.path.join(models_root, model["md5dir"]))
        dirs.append(cfg_dir)
        dirs.append(name_dir)
        return dirs

    def resolve(raw: str | None, dirs: list[str]) -> str | None:
        if not raw:
            return None
        clean = re.sub(r"<[^>]*>", "", raw)
        if not clean:
            return None
        for d in dirs:
            candidate = os.path.normpath(os.path.join(d, clean))
            if os.path.isfile(candidate):
                return os.path.relpath(candidate, packages_root).replace(os.sep, "/")
        return None

    def resolve_abs(raw: str | None, dirs: list[str]) -> str | None:
        if not raw:
            return None
        clean = re.sub(r"<[^>]*>", "", raw)
        if not clean:
            return None
        for d in dirs:
            candidate = os.path.normpath(os.path.join(d, clean))
            if os.path.isfile(candidate):
                return candidate
        return None

    def float_at(args: list[str], i: int) -> float | None:
        if len(args) <= i:
            return None
        try:
            return float(args[i])
        except ValueError:
            return None

    def process(cfg_path: str) -> None:
        if cfg_path in seen or not os.path.isfile(cfg_path):
            return
        seen.add(cfg_path)
        cfg_dir = os.path.dirname(cfg_path)
        with open(cfg_path, "r", encoding="utf-8", errors="replace") as f:
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
                    target = args[0]
                    if target.startswith("packages/"):
                        path = os.path.join(packages_root, target[len("packages/"):])
                    else:
                        path = os.path.join(cfg_dir, target)
                    process(os.path.normpath(path))
                elif cmd == "md5dir" and args:
                    model["md5dir"] = args[0]
                elif cmd == "mdlscale" and args:
                    model["scale"] = int(float(args[0])) / 100.0
                elif cmd == "mdltrans" and len(args) >= 3:
                    model["trans"] = [float(args[0]), float(args[1]), float(args[2])]
                elif cmd == "md5load" and args:
                    dirs = candidates(cfg_dir)
                    source = resolve_abs(args[0], dirs)
                    parts.append({
                        "mesh": args[1] if len(args) > 1 else "",
                        "source": source,
                        "source_rel": resolve(args[0], dirs),
                        "cfg_dir": cfg_dir,
                        "md5dir": model.get("md5dir"),
                        "skins": [], "bump": None, "anims": [],
                        "tags": [], "adjusts": [], "pitches": [],
                    })
                elif parts and cmd == "md5skin" and len(args) >= 2:
                    part = parts[-1]
                    dirs = candidates(cfg_dir)
                    part["skins"].append({
                        "part": args[0],
                        "diffuse": resolve(args[1], dirs),
                        "mask": resolve(args[2], dirs) if len(args) >= 3 else None,
                    })
                elif parts and cmd == "md5bumpmap" and len(args) >= 2:
                    parts[-1]["bump"] = resolve(args[1], candidates(cfg_dir))
                elif parts and cmd == "md5anim" and len(args) >= 2:
                    part = parts[-1]
                    anim = {"name": args[0], "file": resolve(args[1], candidates(cfg_dir)),
                            "part": 0}
                    fps = float_at(args, 2)
                    if fps:
                        anim["fps"] = fps
                    priority = float_at(args, 3)
                    if priority:
                        anim["priority"] = int(priority)
                    part["anims"].append(anim)
                elif parts and cmd == "md5tag" and len(args) >= 2:
                    parts[-1]["tags"].append({"bone": args[0], "tag": args[1]})
                elif parts and cmd == "md5adjust" and args:
                    adj = {"bone": args[0]}
                    for i, key in enumerate(("yaw", "pitch", "roll", "tx", "ty", "tz"), start=1):
                        value = float_at(args, i)
                        if value is not None:
                            adj[key] = value
                    parts[-1]["adjusts"].append(adj)
                elif parts and cmd == "md5pitch" and args:
                    pitch = {"bone": args[0]}
                    for i, key in enumerate(("scale", "offset", "min", "max"), start=1):
                        value = float_at(args, i)
                        if value is not None:
                            pitch[key] = value
                    parts[-1]["pitches"].append(pitch)
                elif cmd == "md5link" and len(args) >= 3:
                    link = {"parent": int(float(args[0])), "child": int(float(args[1])), "tag": args[2]}
                    if len(args) >= 6:
                        link["translate"] = [float(args[3]), float(args[4]), float(args[5])]
                    model["links"].append(link)

    process(os.path.join(name_dir, "md5.cfg"))
    return parts, model


def build_linked_record(name: str, parts: list[dict], model: dict, packages: str,
                        assimp: str, out_dir: str) -> dict | None:
    """Export a linked multi-part model: one glb per part plus a ``link`` map."""
    if len(parts) < 2 or not model.get("links"):
        return None
    # animmodel::loaded() does `scale /= 4`: Cube2 model units are 4x world units.
    scale = model["scale"] / 4.0
    record: dict = {"index": -1, "linked": True, "scale": scale,
                    "trans": model["trans"], "parts": []}
    for i, part in enumerate(parts):
        source = part.get("source")
        if not source or not os.path.isfile(source):
            return None
        # Pick the FFA skin as the flat fallback: the team variants are the ones
        # whose filenames mention blue/red, so prefer the remaining entry.
        skin = None
        skins = [s for s in part["skins"] if s.get("diffuse")]
        if skins:
            plain = [s for s in skins if "blue" not in s["diffuse"].lower()
                     and "red" not in s["diffuse"].lower()]
            skin = (plain or skins)[-1]["diffuse"]
        entry: dict = {
            "source": part.get("source_rel"),
            "scale": scale, "trans": model["trans"],
            "skin": skin, "bump": part.get("bump"),
        }
        if skins:
            entry["skins"] = skins
        anims = {a["name"]: a["file"] for a in part["anims"] if a.get("file")}
        speeds = {a["name"]: a["fps"] for a in part["anims"] if a.get("fps", 0.0) > 0.0}
        if anims:
            entry["anims"] = anims
        if speeds:
            entry["animSpeeds"] = speeds
        if part.get("tags"):
            entry["tags"] = part["tags"]
        if part.get("adjusts"):
            entry["adjusts"] = part["adjusts"]
        if part.get("pitches"):
            entry["pitch"] = part["pitches"]

        glb = os.path.join(out_dir, sanitize(name) + f"_part{i}.glb")
        baked = False
        if anims:
            baked = bake_animated_glb(assimp, source, anims, packages, glb,
                                      adjusts=part.get("adjusts"))
        if not baked:
            proc = subprocess.run([assimp, "export", source, glb], capture_output=True, text=True)
            if proc.returncode == 0 and os.path.isfile(glb):
                strip_glb_materials(glb)
            else:
                print(f"FAIL  {name} part {i}: assimp failed\n{proc.stdout}\n{proc.stderr}", file=sys.stderr)
                glb = None
        if glb and os.path.isfile(glb):
            entry["glb"] = os.path.basename(glb)
            entry["animated"] = baked
        else:
            return None
        record["parts"].append(entry)

    for link in model.get("links", []):
        parent, child = link["parent"], link["child"]
        if 0 <= parent < len(record["parts"]) and 0 <= child < len(record["parts"]):
            entry = {"parent": parent, "tag": link["tag"]}
            if link.get("translate"):
                entry["translate"] = link["translate"]
            record["parts"][child]["link"] = entry
    return record


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
        # third-person view weapons (render.cpp vweps; attached to the body's tag_weapon bone)
        "vwep/fist", "vwep/shotg", "vwep/chaing", "vwep/rocket", "vwep/rifle", "vwep/gl", "vwep/pistol",
    ]
    # First-person hudguns (render.cpp drawhudmodel): one linked hands+weapon model per
    # playermodel/gun. ogro2 reuses mrfixit's hudguns, so only four bases are distinct.
    gun_files = ("fist", "shotg", "chaing", "rocket", "rifle", "gl", "pistol")
    hudgun_names = [f"{player}/hudguns/{gun}"
                    for player in ("mrfixit", "snoutx10k", "inky", "captaincannon")
                    for gun in gun_files]
    entries = [{"index": -1, "path": name} for name in builtins]
    seen = {e["path"] for e in entries}
    for entry in catalogue.get("mapmodels", []):
        if entry["path"] not in seen:
            entries.append(entry)
            seen.add(entry["path"])

    for name in hudgun_names:
        parts, model = parse_linked_parts(name, models_root, packages)
        record = build_linked_record(name, parts, model, packages, args.assimp, args.out)
        if record is None:
            print(f"MISS  {name}: no linked parts")
            manifest["models"][name] = {"index": -1, "linked": True, "parts": []}
            continue
        manifest["models"][name] = record
        print(f"OK    {name} -> {len(record['parts'])} linked parts")

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
            # Skin/bumpmap paths resolve against md5dir (modelcommands::setdir),
            # which can differ from the mesh dir (vwep/<gun> textures live in
            # hudguns/<gun>).
            tex_dir = (os.path.join(models_root, params["md5dir"])
                       if params.get("md5dir") else None)
            record["source"] = os.path.relpath(source, packages).replace(os.sep, "/")
            skin = resolve_skin(params.get("skin"), cfg_dir, name_dir, mesh_dir, tex_dir)
            record["skin"] = rel_to_packages(skin, packages, cfg_dir, extra_dirs=[mesh_dir, tex_dir])
            record["bump"] = rel_to_packages(params.get("bump"), packages, cfg_dir, extra_dirs=[mesh_dir, tex_dir])

            # MD5 (skelmodel) extras: per-part skins, the .md5anim table and the
            # named bone tags the engine attaches sub-models to. The single `skin`
            # above stays the runtime fallback; `skins` preserves the per-part split.
            ext = os.path.splitext(source)[1].lower()
            if ext == ".md5mesh":
                record["skeleton"] = True
                skins = []
                for s in params.get("skins", []):
                    diffuse = rel_to_packages(s.get("diffuse"), packages, cfg_dir, extra_dirs=[tex_dir, mesh_dir])
                    mask = rel_to_packages(s.get("mask"), packages, cfg_dir, extra_dirs=[tex_dir, mesh_dir])
                    if diffuse:
                        skins.append({"part": s["part"], "diffuse": diffuse, "mask": mask})
                if skins:
                    record["skins"] = skins
                if params.get("tags"):
                    record["tags"] = params["tags"]
                record.update(build_anim_metadata(params, packages, cfg_dir, mesh_dir))

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
