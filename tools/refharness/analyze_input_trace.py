#!/usr/bin/env python3
"""Summarise an A2 `SAUER_INPUTTRACE` CSV (the layer-3/4 timing + mouse capture).

The trace is one CSV tagged by `kind`:
    I <seq> <dx> <dy> <curtime> <cursens> <yaw> <pitch>   # one per mousemove()
    T <seq> <elapsedtime> <scaledtime> <curtime> <timeerr> # one per rendered frame
    P <seq> <diff> <physframetime> <physsteps>             # one per physicsframe()

This prints the distributions the port must reproduce, so a divergence in the
port's scheduler / mouse handling is obvious. See
tools/refharness/instrumented/README.md ("Input / timing trace").
"""
import argparse
import collections
import sys


def load(path):
    t, p, imouse = [], [], []
    with open(path) as f:
        header = f.readline()
        for line in f:
            parts = line.strip().split(",")
            if len(parts) != 8:
                continue
            kind, seq = parts[0], int(parts[1])
            a, b, c, d, e, fv = (float(x) for x in parts[2:])
            if kind == "T":
                t.append((seq, a, b, c, d))       # elapsed, scaled, curtime, timeerr
            elif kind == "P":
                p.append((seq, a, b, c))          # diff, physframetime, physsteps
            elif kind == "I":
                imouse.append((seq, a, b, c, d, e, fv))  # dx, dy, curtime, sens, yaw, pitch
    return t, p, imouse


def dist(values):
    c = collections.Counter(int(v) for v in values)
    total = sum(c.values())
    return ", ".join(f"{v}×{n} ({100*n/total:.0f}%)" for v, n in sorted(c.items()))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("trace")
    args = ap.parse_args()

    t, p, imouse = load(args.trace)
    if not t and not p and not imouse:
        print("no trace lines found", file=sys.stderr)
        return 1

    print(f"frames (T): {len(t)}   physics frames (P): {len(p)}   mousemove (I): {len(imouse)}")
    if t:
        print(f"  elapsedtime (ms):   {dist(a for _, a, _, _, _ in t)}")
        print(f"  curtime (ms):       {dist(c for _, _, _, c, _ in t)}")
        print(f"  timeerr:            {dist(d for _, _, _, _, d in t)}")
    if p:
        print(f"  physframetime (ms): {dist(b for _, _, b, _ in p)}")
        print(f"  physsteps:          {dist(c for _, _, _, c in p)}")
    if imouse:
        tot = sum(1 for _ in imouse)
        moved = [m for m in imouse if m[1] or m[2]]
        print(f"  mouse events w/ motion: {len(moved)}/{tot}")
        cs = collections.Counter(round(m[4], 6) for m in imouse)
        print(f"  cursens values:     {', '.join(f'{v}×{n}' for v, n in cs.most_common(5))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
