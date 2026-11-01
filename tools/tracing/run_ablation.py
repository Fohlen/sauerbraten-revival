#!/usr/bin/env python3
"""
Automated runner for frame-stutter and CPU-crunch ablation runs (Stage 3).
Invokes Godot with --demo=<demo> and --ablation-* flags across a suite of
component combinations and aggregates the JSON results into a Markdown table.
"""

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

DEFAULT_GODOT = "/Applications/Godot_mono.app/Contents/MacOS/Godot"
DEFAULT_DEMO = "forge_2026-11-01_21.35.57.fbs"

ALL_COMPONENTS = [
    "menu",
    "hud",
    "playermodel",
    "hudgun",
    "weaponeffects",
    "particles",
    "lights",
    "envmaps",
    "mapmodels",
    "items",
    "flags",
    "triggers",
    "sounds",
    "worldmesh",
    "worldenv",
    "playerview",
    "worldprocess",
]


def build_combinations():
    """Returns ordered list of (label, extra_cli_args) for the ablation matrix."""
    combos = [
        ("baseline", ["--ablation-enable", "--ablation-idle-after=5"]),
        ("all_off", ["--ablation-disable=all", "--ablation-idle-after=5"]),
        ("all_off_no_physics", ["--ablation-disable=all,worldphysics", "--ablation-idle-after=5"]),
        ("menu_idle_on", ["--ablation-idle-menu", "--ablation-duration=5"]),
        ("menu_idle_off", ["--ablation-idle-menu", "--ablation-disable=menu", "--ablation-duration=5"]),
    ]

    # Leave-one-out for each component
    for comp in ALL_COMPONENTS:
        combos.append((f"no_{comp}", [f"--ablation-disable={comp}"]))

    # Grouped subsystem ablations
    combos.extend([
        ("no_ui_and_menu", ["--ablation-disable=menu,hud"]),
        ("no_skeletal_models", ["--ablation-disable=playermodel,hudgun,menu"]),
        ("no_lighting_particles", ["--ablation-disable=lights,envmaps,particles,worldenv"]),
        ("no_entities_models", ["--ablation-disable=mapmodels,items,flags,triggers,weaponeffects"]),
        ("no_process_hooks", ["--ablation-disable=hud,playerview,worldprocess,weaponeffects,menu"]),
        ("only_worldmesh", ["--ablation-only=worldmesh,worldenv"]),
    ])
    return combos


def run_single(godot: str, project_dir: Path, demo: str, label: str, extra_args: list[str], out_dir: Path, duration: float | None):
    out_json = out_dir / f"{label}.json"
    out_log = out_dir / f"{label}.log"

    cmd = [godot, "--path", str(project_dir)]
    if "--ablation-idle-menu" not in extra_args:
        cmd.append(f"--demo={demo}")
    cmd.extend([
        f"--ablation-label={label}",
        f"--ablation-out={out_json}",
        "--ablation-quit",
    ])
    if duration is not None and not any(a.startswith("--ablation-duration=") for a in extra_args):
        cmd.append(f"--ablation-duration={duration}")
    cmd.extend(extra_args)

    print(f"[run] {label}: {' '.join(cmd)}", flush=True)
    t0 = time.monotonic()
    with open(out_log, "w", encoding="utf-8") as logf:
        proc = subprocess.run(cmd, cwd=project_dir, stdout=logf, stderr=subprocess.STDOUT)
    elapsed = time.monotonic() - t0

    if proc.returncode != 0 or not out_json.exists():
        print(f"  -> FAILED (exit={proc.returncode}, {elapsed:.1f}s) see {out_log}", file=sys.stderr)
        return None

    with open(out_json, "r", encoding="utf-8") as f:
        data = json.load(f)
    active = data.get("ActiveDemo") or {}
    print(
        f"  -> OK ({elapsed:.1f}s): fps={active.get('EffectiveFps')} "
        f"p50={active.get('P50Ms')}ms p99={active.get('P99Ms')}ms max={active.get('MaxMs')}ms "
        f">16.7ms={active.get('FramesOver16_7Ms')} cpu={active.get('CpuPercent')}% "
        f"alloc={active.get('AllocKbPerFrame')}KB/f",
        flush=True,
    )
    return data


def format_markdown_table(results: list[dict]) -> str:
    lines = [
        "| Label | Phase | Frames | FPS | Mean (ms) | P50 (ms) | P95 (ms) | P99 (ms) | Max (ms) | Jitter P99 | >8.3ms | >16.7ms | Excess (ms) | CPU % | CPU ms/f | Proc (ms) | Phys (ms) | Draws | Alloc KB/f | GC (0/1/2) |",
        "| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :--- |",
    ]
    for r in results:
        if not r:
            continue
        label = r.get("Label", "")
        for key in ("ActiveDemo", "PostDemoIdle"):
            s = r.get(key)
            if not s:
                continue
            gc_str = f"{s.get('GcGen0', 0)}/{s.get('GcGen1', 0)}/{s.get('GcGen2', 0)}"
            lines.append(
                f"| `{label}` | `{s.get('Phase')}` | {s.get('Frames')} | {s.get('EffectiveFps'):.1f} | "
                f"{s.get('MeanMs'):.2f} | {s.get('P50Ms'):.2f} | {s.get('P95Ms'):.2f} | "
                f"{s.get('P99Ms'):.2f} | {s.get('MaxMs'):.2f} | {s.get('JitterP99Ms'):.2f} | "
                f"{s.get('FramesOver8_3Ms')} | {s.get('FramesOver16_7Ms')} | {s.get('ExcessStutterMs'):.1f} | "
                f"{s.get('CpuPercent'):.1f}% | {s.get('CpuMsPerFrame'):.2f} | "
                f"{s.get('ProcessMsMean'):.2f} | {s.get('PhysicsMsMean'):.2f} | "
                f"{s.get('DrawCallsMean'):.0f} | {s.get('AllocKbPerFrame'):.2f} | {gc_str} |"
            )
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description="Run Sauerbraten Revival ablation combinations.")
    parser.add_argument("--godot", default=os.environ.get("GODOT", DEFAULT_GODOT), help="Path to Godot binary")
    parser.add_argument("--demo", default=DEFAULT_DEMO, help="Demo file name or path")
    parser.add_argument("--out-dir", default="/tmp/sauer_ablation", help="Directory for JSON and log outputs")
    parser.add_argument("--duration", type=float, default=None, help="Optional fixed duration override (seconds)")
    parser.add_argument("--filter", default=None, help="Comma-separated substring filter on combination labels")
    args = parser.parse_args()

    project_dir = Path(__file__).resolve().parents[2]
    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    combos = build_combinations()
    if args.filter:
        tokens = [t.strip().lower() for t in args.filter.split(",") if t.strip()]
        combos = [(lbl, c) for (lbl, c) in combos if any(t in lbl.lower() for t in tokens)]

    results = []
    for label, extra in combos:
        res = run_single(args.godot, project_dir, args.demo, label, extra, out_dir, args.duration)
        if res:
            results.append(res)

    summary_md = format_markdown_table(results)
    summary_path = out_dir / "summary.md"
    summary_path.write_text(summary_md + "\n", encoding="utf-8")
    print("\n" + summary_md)
    print(f"\nSaved summary table to {summary_path}")


if __name__ == "__main__":
    main()
