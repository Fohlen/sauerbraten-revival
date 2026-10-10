#!/usr/bin/env python3
"""Unit tests for the model-import cfg parser (``python -m unittest``).

Run from anywhere:

    python -m unittest tools/model-import/test_import_models.py

The reference-dump tests skip when the dump is not present.
"""
from __future__ import annotations

import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import import_models  # noqa: E402


def _write(path: str, text: str) -> None:
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


class AnimPartParseTest(unittest.TestCase):
    def test_parts_priorities_and_pitch(self):
        with tempfile.TemporaryDirectory() as d:
            cfg = os.path.join(d, "md5.cfg")
            _write(cfg, "\n".join([
                'md5load "m.md5mesh"',
                'md5anim "forward" "f.md5anim" 125 1',
                'md5anim "idle" "i.md5anim" 0 -1',
                'md5anim "hold 5" "h5.md5anim" 0 -2',
                'md5animpart Spine1',
                'md5pitch Spine1 0.75 0 -45 40',
                'md5pitch Head 0.5 0 -10 10',
                'md5anim "forward" "f.md5anim" 125 -1',
                'md5anim "hold 5" "h5.md5anim"',
                'md5animpart "Wing.R Wing.L"',
                'md5anim "hold 5" "wing.md5anim" 5 7',
            ]) + "\n")
            p = import_models.parse_cfg(cfg)

        self.assertEqual([["Spine1"], ["Wing.R", "Wing.L"]], p["animparts"])

        # Part 0 (before the first md5animpart): forward has fps 125 / priority 1.
        fwd0 = next(a for a in p["anims"] if a["name"] == "forward" and a["part"] == 0)
        self.assertEqual(125.0, fwd0["fps"])
        self.assertEqual(1, fwd0["priority"])

        # Part 0 hold 5 priority -2 (movement wins on the full-body part).
        hold0 = next(a for a in p["anims"] if a["name"] == "hold 5" and a["part"] == 0)
        self.assertEqual(-2, hold0["priority"])

        # Part 1 (Spine1) forward priority -1 (hold beats it on the upper body).
        fwd1 = next(a for a in p["anims"] if a["name"] == "forward" and a["part"] == 1)
        self.assertEqual(-1, fwd1["priority"])

        # Part 1 hold 5 uses the engine defaults (no fps / priority given).
        hold1 = next(a for a in p["anims"] if a["name"] == "hold 5" and a["part"] == 1)
        self.assertNotIn("fps", hold1)
        self.assertNotIn("priority", hold1)

        # Part 2 (wings) explicit fps/priority.
        hold2 = next(a for a in p["anims"] if a["name"] == "hold 5" and a["part"] == 2)
        self.assertEqual(5.0, hold2["fps"])
        self.assertEqual(7, hold2["priority"])

        self.assertEqual(
            [{"bone": "Spine1", "scale": 0.75, "offset": 0.0, "min": -45.0, "max": 40.0},
             {"bone": "Head", "scale": 0.5, "offset": 0.0, "min": -10.0, "max": 10.0}],
            p["pitches"])

    def test_part_wide_pitch_with_empty_bone(self):
        with tempfile.TemporaryDirectory() as d:
            cfg = os.path.join(d, "md5.cfg")
            _write(cfg, 'md5pitch "" 1\n')
            p = import_models.parse_cfg(cfg)
        self.assertEqual([{"bone": "", "scale": 1.0}], p["pitches"])

    def test_exec_offsets_part_indices(self):
        with tempfile.TemporaryDirectory() as d:
            _write(os.path.join(d, "anims.cfg"), "\n".join([
                'md5anim "idle" "i.md5anim"',
                'md5animpart Spine1',
                'md5anim "idle" "i.md5anim"',
            ]) + "\n")
            main = os.path.join(d, "md5.cfg")
            _write(main, 'md5load "m.md5mesh"\nexec "anims.cfg"\n')

            p = import_models.parse_cfg(main)
            import_models._execute_cfg(p, models_root=d, packages_root=d)

        self.assertEqual([["Spine1"]], p["animparts"])
        self.assertEqual(2, len(p["anims"]))
        self.assertEqual(0, p["anims"][0]["part"])
        self.assertEqual(1, p["anims"][1]["part"])

    def test_merge_other_cfg_offsets_part_indices(self):
        with tempfile.TemporaryDirectory() as d:
            _write(os.path.join(d, "md5.cfg"), 'md5load "m.md5mesh"\nmd5animpart Spine1\n')
            _write(os.path.join(d, "anims.cfg"), 'md5anim "idle" "i.md5anim"\n')
            p = import_models.parse_cfg(os.path.join(d, "md5.cfg"))
            import_models._merge_cfg_params(p, d)

        self.assertEqual([["Spine1"]], p["animparts"])
        self.assertEqual(1, len(p["anims"]))
        # anims.cfg's own part 0 maps onto the parent's part 1 (after its animpart).
        self.assertEqual(1, p["anims"][0]["part"])


class MetadataBuildTest(unittest.TestCase):
    ref = os.environ.get("SAUER_REF", "/Users/fohlen/Downloads/sauerbraten-code-r6889")

    def test_mrfixit_end_to_end(self):
        models_root = os.path.join(self.ref, "packages", "models")
        if not os.path.isdir(models_root):
            self.skipTest("reference dump not present")
        source, params = import_models.find_model(models_root, "mrfixit")
        packages = os.path.dirname(models_root)
        cfg_dir = params["cfg_dir"]
        md = import_models.build_anim_metadata(params, packages, cfg_dir, os.path.dirname(source))

        self.assertEqual([{"mask": ["Spine1"]}], md["animParts"])
        self.assertEqual(2, len(md["partAnims"]))
        self.assertEqual(-2, md["partAnims"][0]["hold 5"]["priority"])
        self.assertNotIn("priority", md["partAnims"][1]["hold 5"])
        self.assertTrue(md["partAnims"][0]["hold 5"]["file"].endswith("sniper_idle.md5anim"))
        self.assertEqual(["Spine1", "Head"], [p["bone"] for p in md["pitch"]])
        self.assertEqual("models/mrfixit/sniper_idle.md5anim", md["anims"]["hold 5"])


class ReferenceCfgTest(unittest.TestCase):
    """Ground-truth checks against the read-only reference dump (skipped if absent)."""

    ref = os.environ.get("SAUER_REF", "/Users/fohlen/Downloads/sauerbraten-code-r6889")

    def _cfg(self, model: str) -> str | None:
        path = os.path.join(self.ref, "packages", "models", model, "anims.cfg")
        return path if os.path.isfile(path) else None

    def test_mrfixit_two_parts_and_pitch(self):
        cfg = self._cfg("mrfixit")
        if not cfg:
            self.skipTest("reference dump not present")
        p = import_models.parse_cfg(cfg)
        self.assertEqual([["Spine1"]], p["animparts"])
        self.assertEqual(["Spine1", "Head"], [x["bone"] for x in p["pitches"]])
        # hold 5 is full-body (part 0, priority -2) and upper-body (part 1, default).
        parts = {a["part"] for a in p["anims"] if a["name"] == "hold 5"}
        self.assertEqual({0, 1}, parts)

    def test_inky_three_parts(self):
        cfg = self._cfg("inky")
        if not cfg:
            self.skipTest("reference dump not present")
        p = import_models.parse_cfg(cfg)
        self.assertEqual(2, len(p["animparts"]))
        self.assertEqual(["Spine"], p["animparts"][0])
        self.assertIn("Wing.R", p["animparts"][1])
        self.assertEqual(["Spine", "Head"], [x["bone"] for x in p["pitches"]])

    def test_captaincannon_three_parts(self):
        cfg = self._cfg("captaincannon")
        if not cfg:
            self.skipTest("reference dump not present")
        p = import_models.parse_cfg(cfg)
        self.assertEqual([["Spine"], ["Cape"]], p["animparts"])


if __name__ == "__main__":
    unittest.main()
