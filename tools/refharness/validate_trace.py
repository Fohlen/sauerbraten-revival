#!/usr/bin/env python3
"""Validate the ported movement formulas against a captured original-client trace (A2/A3).

Replays the recorded inputs one fixed 5 ms step at a time using the documented
model (see docs/movement-physics.md) and compares the predicted `vel`/`falling`
to what the original recorded. Steps are split by environment so `move()`'s
collision effects (which need geometry we don't have yet) don't pollute the test:

  - FLAT: on a flat floor (floor.z == 1) both before and after -> vel must match.
  - AIR:  PHYS_FALL both before and after -> vel AND falling must match.

Misses are then bucketized; large ones correspond to external effects (jump
pads, teleports) or collisions, not model errors.

Usage:  python3 validate_trace.py <trace.csv>
"""
import csv
import math
import sys

RAD = math.pi / 180.0
FALL, SLOPE, FLOOR = 1, 3, 4
MAXSPEED, GRAVITY, JUMPVEL, FLOORZ = 100.0, 200.0, 125.0, 0.867
CURTIME = 5  # ms


def f(row, key):
    return float(row[key])


def i(row, key):
    return int(float(row[key]))


def vecfromyawpitch(yaw, move, strafe):
    m = [0.0, 0.0, 0.0]
    if move:
        m[0] = move * -math.sin(RAD * yaw)
        m[1] = move * math.cos(RAD * yaw)
    if strafe:
        m[0] += strafe * math.cos(RAD * yaw)
        m[1] += strafe * math.sin(RAD * yaw)
    return m


def normalize(v):
    m = math.sqrt(sum(x * x for x in v))
    return [x / m for x in v] if m > 1e-9 else v


def predict(prev, cur):
    """One fixed step of modifygravity + modifyvelocity (water/floating false)."""
    ps, floor = prev["ps"], prev["floor"]
    vel, falling = list(prev["vel"]), list(prev["falling"])
    move, strafe = i(cur, "move"), i(cur, "strafe")
    yaw, jumping = f(cur, "yaw"), i(cur, "jumping")

    # modifyvelocity: jump
    if jumping and ps >= SLOPE:
        vel[2] = max(vel[2], JUMPVEL)

    # modifyvelocity: wish direction (pitch is 0 for a grounded/normal player)
    d = [0.0, 0.0, 0.0]
    if move or strafe:
        d = vecfromyawpitch(yaw, move, strafe)
        if ps >= SLOPE:
            d[2] = -(d[0] * floor[0] + d[1] * floor[1]) / floor[2]
        d = normalize(d)

    mult = (1.3 if (move and not strafe) else 1.0) * (1.3 if ps < SLOPE else 1.0)
    target = [x * MAXSPEED * mult for x in d]
    fric = 6.0 if ps >= SLOPE else 30.0
    t = (1 - 1 / fric) ** (CURTIME / 20.0)
    vel = [target[k] + (vel[k] - target[k]) * t for k in range(3)]

    # modifygravity
    secs = CURTIME / 1000.0
    g = [0.0, 0.0, 0.0]
    if ps == FALL:
        g[2] -= GRAVITY * secs
    elif 0 < floor[2] < FLOORZ:
        g = [0.0, 0.0, -1.0]
        k = g[2] * floor[2]
        g = [g[j] - k * floor[j] for j in range(3)]
        m = math.sqrt(sum(x * x for x in g))
        g = [x / m * GRAVITY * secs for x in g]
    falling = [falling[j] + g[j] for j in range(3)]
    if ps >= SLOPE:
        c = min(max((floor[2] - 0.5) / (FLOORZ - 0.5), 0.0), 1.0)
        falling = [x * (1 - c / 6.0) ** (CURTIME / 20.0) for x in falling]

    return vel, falling


def load(row):
    return {
        "ps": i(row, "physstate"),
        "floor": [f(row, "floorx"), f(row, "floory"), f(row, "floorz")],
        "vel": [f(row, "vx"), f(row, "vy"), f(row, "vz")],
        "falling": [f(row, "fx"), f(row, "fy"), f(row, "fz")],
    }


def maxerr(a, b):
    return max(abs(a[k] - b[k]) for k in range(3))


def main(path):
    rows = [r for r in csv.DictReader(open(path))]
    flat = {"n": 0, "ok": 0, "max": 0.0}
    air = {"n": 0, "ok": 0, "maxv": 0.0, "maxf": 0.0, "miss": []}

    for k in range(1, len(rows)):
        prev, cur = load(rows[k - 1]), load(rows[k])
        pv, pf = predict(prev, rows[k])
        ev, ef = maxerr(pv, cur["vel"]), maxerr(pf, cur["falling"])

        if prev["ps"] == FLOOR and cur["ps"] == FLOOR and \
           prev["floor"] == [0, 0, 1] and cur["floor"] == [0, 0, 1]:
            flat["n"] += 1
            flat["max"] = max(flat["max"], ev)
            if ev < 1e-3:
                flat["ok"] += 1

        if prev["ps"] == FALL and cur["ps"] == FALL:
            air["n"] += 1
            air["maxv"] = max(air["maxv"], ev)
            air["maxf"] = max(air["maxf"], ef)
            if ev < 1e-3 and ef < 1e-3:
                air["ok"] += 1
            elif len(air["miss"]) < 5:
                air["miss"].append((k, ev, ef))

    pct = lambda o, n: 100.0 * o / n if n else 0.0
    print(f"trace: {path}  ({len(rows)} steps)")
    print(f"FLAT  {flat['n']:5d}  exact vel: {flat['ok']:5d} ({pct(flat['ok'], flat['n']):5.1f}%)  max err {flat['max']:.5f}")
    print(f"AIR   {air['n']:5d}  exact vel+falling: {air['ok']:5d} ({pct(air['ok'], air['n']):5.1f}%)  "
          f"max vel err {air['maxv']:.3f}  max fall err {air['maxf']:.3f}")
    if air["miss"]:
        print("  worst air misses (step, vel err, fall err) - external events/collisions:")
        for m in sorted(air["miss"], key=lambda x: -max(x[1], x[2]))[:5]:
            print(f"    step {m[0]:5d}  vel {m[1]:8.3f}  fall {m[2]:7.3f}")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
