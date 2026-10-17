#!/usr/bin/env python3
"""Merge several single-animation glTF/GLB files that share one mesh into one GLB.

assimp's MD5 importer loads exactly one sibling ``<meshbase>.md5anim`` per export,
so baking a model's clip table means exporting once per clip and merging. All the
per-clip exports share the identical mesh/nodes/skin (same ``.md5mesh`` input), so
only their animation data differs: this appends each file's ``animations`` (with
accessor/bufferView/buffer indices remapped) and renames it to the clip name.

Stdlib only (matches the rest of ``tools/``).
"""
from __future__ import annotations

import json
import struct
import sys


def read_glb(path: str) -> tuple[dict, bytes]:
    with open(path, "rb") as f:
        data = f.read()
    magic, version, length = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67:
        raise ValueError(f"{path}: not a GLB")
    offset, json_chunk, bin_chunk = 12, None, b""
    while offset + 8 <= length:
        clen, ctype = struct.unpack_from("<II", data, offset)
        offset += 8
        chunk = data[offset:offset + clen]
        offset += clen
        if ctype == 0x4E4F534A:  # JSON
            json_chunk = chunk
        elif ctype == 0x004E4942:  # BIN
            bin_chunk = chunk
    if json_chunk is None:
        raise ValueError(f"{path}: missing JSON chunk")
    return json.loads(json_chunk.decode("utf-8")), bin_chunk


def write_glb(path: str, doc: dict, binary: bytes) -> None:
    js = json.dumps(doc, separators=(",", ":")).encode("utf-8")
    js += b" " * ((4 - len(js) % 4) % 4)
    body = struct.pack("<II", len(js), 0x4E4F534A) + js
    if binary:
        b = binary + b"\0" * ((4 - len(binary) % 4) % 4)
        body += struct.pack("<II", len(b), 0x004E4942) + b
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body)


def merge_animations(clip_glbs: list[tuple[str, str]], out_path: str) -> None:
    """Merge ``[(clip_name, glb_path), ...]`` into ``out_path``.

    The first entry provides the mesh/nodes/skin; every entry contributes its
    animations (renamed to its clip name).

    assimp re-exports the whole mesh for every clip, but the base file already
    provides it: only the accessors/bufferViews a later clip's animations actually
    reference are copied over. Copying the rest would leave each clip's geometry
    and inverse-bind matrix as unreferenced dead bytes in the merged binary (the
    bulk of a baked player model).
    """
    base_doc, base_bin = read_glb(clip_glbs[0][1])

    # Re-add the base file's own animation under its clip name. Its accessors are
    # already in the base document, so no remapping is needed.
    base_anims = base_doc.get("animations", [])
    base_doc["animations"] = []
    for anim in base_anims:
        base_doc["animations"].append({
            "name": clip_glbs[0][0],
            "samplers": anim.get("samplers", []),
            "channels": anim.get("channels", []),
        })

    for clip_name, glb_path in clip_glbs[1:]:
        doc, binary = read_glb(glb_path)

        # Node indices must match the base (identical mesh input), or channels break.
        if [n.get("name") for n in doc.get("nodes", [])] != [n.get("name") for n in base_doc.get("nodes", [])]:
            raise ValueError(f"{glb_path}: node hierarchy differs from base")

        # Only the accessors this clip's animations reference are carried over.
        needed: set[int] = set()
        for anim in doc.get("animations", []):
            for s in anim.get("samplers", []):
                needed.add(s["input"])
                needed.add(s["output"])

        base_doc.setdefault("accessors", [])
        base_doc.setdefault("bufferViews", [])
        acc_map: dict[int, int] = {}
        bv_map: dict[int, int] = {}
        appended = b""
        for acc_idx in sorted(needed):
            acc = dict(doc["accessors"][acc_idx])
            if "bufferView" in acc:
                old_bv = acc["bufferView"]
                if old_bv not in bv_map:
                    view = doc["bufferViews"][old_bv]
                    offset = view.get("byteOffset", 0)
                    length = view["byteLength"]
                    # Keep every appended view 4-byte aligned (largest component size).
                    appended += b"\0" * ((4 - (len(base_bin) + len(appended)) % 4) % 4)
                    new_view = dict(view)
                    new_view["byteOffset"] = len(base_bin) + len(appended)
                    new_view["buffer"] = 0
                    bv_map[old_bv] = len(base_doc["bufferViews"])
                    base_doc["bufferViews"].append(new_view)
                    appended += binary[offset:offset + length]
                acc["bufferView"] = bv_map[old_bv]
            acc_map[acc_idx] = len(base_doc["accessors"])
            base_doc["accessors"].append(acc)
        base_bin += appended

        for anim in doc.get("animations", []):
            samplers = []
            for s in anim.get("samplers", []):
                s = dict(s)
                s["input"] = acc_map[s["input"]]
                s["output"] = acc_map[s["output"]]
                samplers.append(s)
            channels = [dict(c) for c in anim.get("channels", [])]
            base_doc["animations"].append({
                "name": clip_name,
                "samplers": samplers,
                "channels": channels,
            })

    # Buffer 0 now holds everything.
    if base_doc.get("buffers"):
        base_doc["buffers"][0]["byteLength"] = len(base_bin) + ((4 - len(base_bin) % 4) % 4)
    write_glb(out_path, base_doc, base_bin)


if __name__ == "__main__":
    # merge_animations.py out.glb clip1=a.glb clip2=b.glb ...
    out = sys.argv[1]
    pairs = [(a.split("=", 1)[0], a.split("=", 1)[1]) for a in sys.argv[2:]]
    merge_animations(pairs, out)
    print(f"wrote {out} ({len(pairs)} clips)")
