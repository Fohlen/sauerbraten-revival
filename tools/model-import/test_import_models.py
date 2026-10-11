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


class LinkAndAdjustParseTest(unittest.TestCase):
    def test_parse_cfg_records_link_and_adjust(self):
        with tempfile.TemporaryDirectory() as d:
            cfg = os.path.join(d, "md5.cfg")
            _write(cfg, "\n".join([
                'md5load "gun.md5mesh" gun',
                'md5adjust Root 12.9 -5',
                'md5adjust glove.R -5 -30 -5 1.0 -2.0 -2.0',
                'md5link 0 1 tag_bicep',
                'md5link 0 1 tag_weapon -0.1 0.2 -0.3',
            ]) + "\n")
            p = import_models.parse_cfg(cfg)

        self.assertEqual(
            [{"bone": "Root", "yaw": 12.9, "pitch": -5.0},
             {"bone": "glove.R", "yaw": -5.0, "pitch": -30.0, "roll": -5.0,
              "tx": 1.0, "ty": -2.0, "tz": -2.0}],
            p["adjusts"])
        self.assertEqual(
            [{"parent": 0, "child": 1, "tag": "tag_bicep"},
             {"parent": 0, "child": 1, "tag": "tag_weapon",
              "translate": [-0.1, 0.2, -0.3]}],
            p["links"])


class Md5AdjustBakeTest(unittest.TestCase):
    _ANIM = """MD5Version 10
commandline ""

numFrames 1
numJoints 1
frameRate 24
numAnimatedComponents 6

hierarchy {
	"Root"	-1 63 0	//
}

bounds {
	( 0 0 0 ) ( 0 0 0 )
}

baseframe {
	( 1 0 0 ) ( 0 0 0 )
}

frame 0 {
	1 0 0 0 0 0
}
"""

    def test_yaw_rotates_the_joint(self):
        out = import_models.apply_md5adjust(
            self._ANIM, [{"bone": "Root", "yaw": 90.0}])
        data = import_models._parse_md5anim(out)
        pos, quat = import_models._compose_frame(data, data["frames"][0])[0]
        # In the engine (skelmodel.h skeladjustment::adjust), yaw/pitch/roll perform
        # dualquat mulorient: rotating the joint's orientation without altering pos.
        self.assertAlmostEqual(1.0, pos[0], places=5)
        self.assertAlmostEqual(0.0, pos[1], places=5)
        self.assertAlmostEqual(0.0, pos[2], places=5)
        # A 90 degree yaw about Z is a pure Z rotation (x=y=0, z~=sin45).
        self.assertAlmostEqual(0.0, quat[0], places=5)
        self.assertAlmostEqual(0.0, quat[1], places=5)
        self.assertAlmostEqual(0.70710678, abs(quat[2]), places=5)

    def test_translation_is_applied_in_the_parent_frame(self):
        out = import_models.apply_md5adjust(
            self._ANIM, [{"bone": "Root", "tx": 8.0, "ty": -4.0, "tz": 12.0}])
        data = import_models._parse_md5anim(out)
        # The engine divides md5adjust translations by 4 (skelmodel.h setadjust).
        pos, _ = import_models._compose_frame(data, data["frames"][0])[0]
        self.assertAlmostEqual(1.0 + 2.0, pos[0], places=5)
        self.assertAlmostEqual(-1.0, pos[1], places=5)
        self.assertAlmostEqual(3.0, pos[2], places=5)

    def test_no_adjust_is_a_no_op(self):
        self.assertEqual(self._ANIM, import_models.apply_md5adjust(self._ANIM, []))


class LinkedPartParseTest(unittest.TestCase):
    def _build_model(self, d: str) -> str:
        """Lay out a two-part (hands + weapon) model with a link, in a temp root."""
        packages = os.path.join(d, "packages")
        models = os.path.join(packages, "models")
        hands, gun, combo = (os.path.join(models, n) for n in ("hands", "gun", "combo"))
        for path in (hands, gun, combo):
            os.makedirs(path)
        # Touch the geometry/texture/animation files the parser resolves.
        for f in ("hands.md5mesh", "hands.png", "hands_idle.md5anim"):
            _write(os.path.join(hands, f), "")
        for f in ("gun.md5mesh", "gun.png", "gun_idle.md5anim"):
            _write(os.path.join(gun, f), "")
        _write(os.path.join(hands, "md5.cfg"), "\n".join([
            'md5dir "hands"',
            'md5load "hands.md5mesh" hands',
            'md5skin hands "hands.png"',
        ]) + "\n")
        # The weapon's cfg is exec'd after the hands cfg, so its pre-load
        # tag/adjust/anim commands belong to the hands part.
        _write(os.path.join(gun, "md5.cfg"), "\n".join([
            'md5tag Bicep.R tag_bicep',
            'md5adjust Root 10 -5',
            'md5anim "gun idle" "hands_idle.md5anim"',
            'md5dir "gun"',
            'md5load "gun.md5mesh" gun',
            'md5skin gun "gun.png"',
            'md5tag muzzle tag_muzzle',
            'md5anim "gun idle" "gun_idle.md5anim"',
            'md5link 0 1 tag_bicep',
        ]) + "\n")
        _write(os.path.join(combo, "md5.cfg"), "\n".join([
            'exec "packages/models/hands/md5.cfg"',
            'exec "packages/models/gun/md5.cfg"',
            'mdlscale 300',
            'mdltrans 1 -2 -0.3',
        ]) + "\n")
        return models

    def test_two_parts_and_link_in_exec_order(self):
        with tempfile.TemporaryDirectory() as d:
            models = self._build_model(d)
            packages = os.path.dirname(models)
            parts, model = import_models.parse_linked_parts("combo", models, packages)

        self.assertEqual(2, len(parts))
        self.assertEqual(3.0, model["scale"])
        self.assertEqual([1.0, -2.0, -0.3], model["trans"])
        self.assertEqual([{"parent": 0, "child": 1, "tag": "tag_bicep"}], model["links"])

        hands = parts[0]
        self.assertEqual("hands", hands["mesh"])
        self.assertEqual("models/hands/hands.md5mesh", hands["source_rel"])
        # Pre-load weapon commands attach to the hands part (the engine's parts.last()).
        self.assertEqual([{"bone": "Bicep.R", "tag": "tag_bicep"}], hands["tags"])
        self.assertEqual([{"bone": "Root", "yaw": 10.0, "pitch": -5.0}], hands["adjusts"])
        self.assertEqual("models/hands/hands_idle.md5anim", hands["anims"][0]["file"])
        self.assertEqual("models/hands/hands.png", hands["skins"][0]["diffuse"])

        weapon = parts[1]
        self.assertEqual("gun", weapon["mesh"])
        self.assertEqual("models/gun/gun.md5mesh", weapon["source_rel"])
        self.assertEqual([{"bone": "muzzle", "tag": "tag_muzzle"}], weapon["tags"])
        self.assertEqual("models/gun/gun_idle.md5anim", weapon["anims"][0]["file"])
        self.assertEqual("models/gun/gun.png", weapon["skins"][0]["diffuse"])

    def test_reference_hudgun_parts(self):
        ref = os.environ.get("SAUER_REF", "/Users/fohlen/Downloads/sauerbraten-code-r6889")
        models_root = os.path.join(ref, "packages", "models")
        if not os.path.isdir(models_root):
            self.skipTest("reference dump not present")
        packages = os.path.dirname(models_root)
        parts, model = import_models.parse_linked_parts(
            "mrfixit/hudguns/rifle", models_root, packages)
        self.assertEqual(2, len(parts))
        self.assertEqual("hands", parts[0]["mesh"])
        self.assertEqual("models/mrfixit/hudguns/fixit_hands.md5mesh", parts[0]["source_rel"])
        self.assertEqual("models/hudguns/rifle/rifle.md5mesh", parts[1]["source_rel"])
        self.assertIn("gun idle", [a["name"] for a in parts[0]["anims"]])
        self.assertIn("gun shoot", [a["name"] for a in parts[1]["anims"]])
        self.assertEqual({"parent": 0, "child": 1, "tag": "tag_bicep"}, model["links"][0])
        self.assertEqual({"bone": "Bicep.R", "tag": "tag_bicep"}, parts[0]["tags"][0])
        self.assertEqual([{"bone": "", "scale": 1.0}], parts[0]["pitches"])

    def test_all_reference_hudguns_resolve(self):
        ref = os.environ.get("SAUER_REF", "/Users/fohlen/Downloads/sauerbraten-code-r6889")
        models_root = os.path.join(ref, "packages", "models")
        if not os.path.isdir(models_root):
            self.skipTest("reference dump not present")
        packages = os.path.dirname(models_root)

        for player in ("mrfixit", "snoutx10k", "inky", "captaincannon"):
            for gun in ("fist", "shotg", "chaing", "rocket", "rifle", "gl", "pistol"):
                with self.subTest(player=player, gun=gun):
                    parts, model = import_models.parse_linked_parts(
                        f"{player}/hudguns/{gun}", models_root, packages)
                    self.assertEqual(2, len(parts))
                    self.assertEqual(1, len(model["links"]))
                    link = model["links"][0]
                    self.assertEqual((0, 1), (link["parent"], link["child"]))
                    self.assertIn(link["tag"], {tag["tag"] for tag in parts[0]["tags"]})
                    self.assertIn(
                        {"bone": "", "scale": 1.0},
                        parts[0]["pitches"])
                    self.assertEqual(
                        {"gun idle", "gun shoot"},
                        {anim["name"] for anim in parts[0]["anims"]})
                    self.assertEqual(
                        {"gun idle", "gun shoot"},
                        {anim["name"] for anim in parts[1]["anims"]})

                    for part in parts:
                        self.assertTrue(os.path.isfile(part["source"]))
                        for skin in part["skins"]:
                            self.assertTrue(os.path.isfile(os.path.join(packages, skin["diffuse"])))
                            if skin["mask"]:
                                self.assertTrue(os.path.isfile(os.path.join(packages, skin["mask"])))
                        for anim in part["anims"]:
                            self.assertTrue(os.path.isfile(os.path.join(packages, anim["file"])))


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
