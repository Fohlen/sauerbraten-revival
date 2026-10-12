# Movement Physics Reference (Cube 2: Sauerbraten, SVN r6889)

Reverse-engineered from the original C++ source. Use this as the source of truth
when porting. Citations are `file:line` into the r6889 tree.

Primary files:
- `src/shared/ents.h` — `physent` / `dynent` data model and defaults.
- `src/engine/physics.cpp` — all collision and movement.
- `src/fpsgame/fps.cpp` — `canjump()`, `allowmove()`.
- `src/fpsgame/movable.cpp` — physics for inanimate models (boxes, platforms).

---

## 1. Constants

| Name | Value | Meaning | Source |
| --- | --- | --- | --- |
| `maxspeed` | `100` | Player cubes/second (base) | `ents.h:91` |
| `radius` | `4.1` | Horizontal collision radius | `ents.h:91` |
| `eyeheight` | `14` | Distance from origin down to feet | `ents.h:91` |
| `aboveeye` | `1` | Distance from origin up to head | `ents.h:91` |
| `GRAVITY` | `200` | units/s² | `physics.cpp:478` |
| `JUMPVEL` | `125` | Upward impulse (units/s) | `physics.cpp:477` |
| `STAIRHEIGHT` | `4.1` | Max step-up height | `physics.cpp:473` |
| `FLOORZ` | `0.867` | `cos(max walkable slope)≈30°`; floor threshold | `physics.cpp:474` |
| `SLOPEZ` | `0.5` | `cos(60°)`; floor/slope vs wall threshold | `physics.cpp:475` |
| `WALLZ` | `0.2` | wall/slide threshold | `physics.cpp:476` |
| `PHYSFRAMETIME` | `5` | Physics frame length in ms (→ 200 Hz) | `physics.cpp:1582` |
| `moveres` (player) | `10` | Substeps per physics frame → 0.5 ms effective | `fps.cpp:264` |

Player volume is a vertical capsule/cylinder:
- horizontal half-extent = `radius` (4.1),
- bottom = `o.z - eyeheight`,
- top = `o.z + aboveeye`.
So the player is **15 units tall**, with the origin at eye height.

`physstate` enum (`ents.h:62`):
```
PHYS_FLOAT=0, PHYS_FALL=1, PHYS_SLIDE=2, PHYS_SLOPE=3,
PHYS_FLOOR=4, PHYS_STEP_UP=5, PHYS_STEP_DOWN=6, PHYS_BOUNCE=7
```

---

## 2. Per-frame movement (`moveplayer`, `physics.cpp:1681`)

```
secs = curtime / 1000
if !floating: modifygravity(pl, water, curtime)
modifyvelocity(pl, local, water, floating, curtime)

d = vel + falling
if water: d *= 0.5
d *= secs

if floating:                 # spectator / editing
    o += d
else:                        # apply velocity with collision
    f = 1 / moveres
    d *= f
    repeat moveres:
        if !move(pl, d) and ++collisions < 5:                               # discrete steps + sliding
            retry same step
```

So the real simulation runs velocity in `moveres` sub-steps, sliding on collision.

### Direction from yaw/pitch (`vecfromyawpitch`, `physics.cpp:1548`)

Z-up, `yaw = 0` faces `+Y`:
```
m.x = move * -sin(yaw)
m.y = move *  cos(yaw)
m.z = 0 on ground (pitch only used when floating/water/camera)

if strafe:
    m.x += strafe * cos(yaw)
    m.y += strafe * sin(yaw)
```
Input mapping (`physics.cpp:2014`): `forward move=+1`, `backward move=-1`,
`left strafe=+1`, `right strafe=-1`.

### Velocity update (`modifyvelocity`, `physics.cpp:1589`)

```
m = normalize( vecfromyawpitch(yaw, pitch, move, strafe) )

if on ground (physstate >= PHYS_SLOPE):
    # move up/down slopes in air; project onto floor plane
    dz = -(m.x*floor.x + m.y*floor.y) / floor.z
    m.z = water ? max(m.z, dz) : dz
    m = normalize(m)

d = m * maxspeed
if ENT_PLAYER:
    if !water and allowmove:
        d *= (move && !strafe ? 1.3 : 1.0)   # forward-only bonus
           * (physstate < PHYS_SLOPE ? 1.3 : 1.0)   # airborne bonus

fric = water ? 20 : (on_ground ? 6 : 30)
vel = d + (vel - d) * pow(1 - 1/fric, curtime/20.0)
```

Key consequences for "feel":
- **Forward-only** is 1.3× faster than diagonal/strafe-only.
- **Airborne** adds another 1.3× (forward-only in air = **1.69×**), while air
  control is *strongly* limited by `fric = 30`.
- `fric = 6` on the ground means velocity converges slowly (slippery accel),
  `fric = 30` in air means velocity barely changes (limited air control).
  `fric` here is inverse-friction: higher value → velocity retained more.
- Diagonal input moves at full `maxspeed` (100), pure forward at 130 (ground).

### Gravity (`modifygravity`, `physics.cpp:1650`)

```
g = 0
if physstate == PHYS_FALL:      g.z -= GRAVITY * secs
elif floor.z in (0, FLOORZ):    g = project(-Z onto floor).normalized() * GRAVITY * secs

falling += g   # (unless water and moving: gravity ignored)

if water or physstate >= PHYS_SLOPE:
    c = water ? 1 : clamp((floor.z - SLOPEZ)/(FLOORZ - SLOPEZ), 0, 1)
    falling *= pow(1 - c/fric, curtime/20)   # fric = water ? 2 : 6
```

### Jump (`modifyvelocity`, `physics.cpp:1603`)

```
if jumping and on ground (physstate >= PHYS_SLOPE) and allowmove:
    jumping = false
    vel.z = max(vel.z, JUMPVEL)
    if water: vel.x /= 8; vel.y /= 8
```

Note the impulse is stored in **`vel.z`**, not in `falling`. Gravity then
accumulates in `falling`, and both are added when moving. Because `vel` is
lerped toward the (horizontal) movement target, the upward impulse slowly decays
in the air too.

Grounded check for jumping: `canjump()` (`fps.cpp:381`) plus `allowmove()`
(`fps.cpp:388`, blocks during taunt).

---

## 3. Collision (`collide`, `move`, `physics.cpp`)

Default entity collision type is `COLLIDE_ELLIPSE` (`ents.h:95`). For **solid
cubes** it uses `fuzzycollidesolid<EntCapsule>` (`physics.cpp:777`), which is
effectively an AABB/cylinder test:

```
Player box: [o.x±radius] × [o.y±radius] × [o.z-eyeheight .. o.z+aboveeye]
For each cube, penetration per face = the (largest, i.e. least-negative) overlap:
  LEFT   dist = cube.min.x - (o.x + radius)      normal (-1,0,0)
  RIGHT  dist = (o.x - radius) - cube.max.x      normal (+1,0,0)
  BACK   dist = cube.min.y - (o.y + radius)      normal (0,-1,0)
  FRONT  dist = (o.y - radius) - cube.max.y      normal (0,+1,0)
  BOTTOM dist = cube.min.z - (o.z + aboveeye)    normal (0,0,-1)
  TOP    dist = (o.z - eyeheight) - cube.max.z   normal (0,0,+1)
Pick the face with the max dist, subject to the dir/cutoff filter.
```

`dir` and `cutoff` filter faces the entity is moving away from:
`dot(dir, normal) >= -cutoff * |dir|` → ignore. This is why landing and moving
don't fight the faces behind you.

`move()` (`physics.cpp:1340`) does, in order:
1. Tentatively apply `o += dir`; if colliding:
   - try `trystepup` if standing on a floor/slope (stair climbing),
   - otherwise slide (`slideagainst`) along the wall and mark `blocked`.
2. `findfloor` / `falling` / `landing` to update `physstate` and `floor`.

Helpers:
- `recalcdir` (`physics.cpp:1059`) re-derives the remaining move length after a
  slide so total speed is preserved in direction.
- `switchfloor` (`physics.cpp:1086`) zeroes `falling` on flat floors, projects
  velocity onto the floor plane.
- `trystepup` (`physics.cpp:1102`) / `trystepdown` (`physics.cpp:1193`,`:1244`)
  implement stair climbing and the step-down state.
- `findfloor` (`physics.cpp:1299`), `landing` (`physics.cpp:1283`),
  `falling` (`physics.cpp:1270`).

### Step-up is what makes ramps and stairs walkable

When the player collides while standing on a floor/slope, `move()` calls
`trystepup(d, dir, obstacle, STAIRHEIGHT, floor)` (`physics.cpp:1362`). It:

- raises the body by up to `STAIRHEIGHT = 4.1` and checks for free space above;
- has a `cansmooth` branch that walks shallow slopes *continuously* via a
  `smoothdir` with vertical component `1/scale` (scale 1 or 2);
- uses `-obstacle.xy` as the climb direction for slope obstacles
  (`obstacle.z` in `[0, SLOPEZ)`).

Without this, a small vertical lip (e.g. the base of a rotated box ramp) blocks
movement. Sauerbraten does **not** require authors to round corners: the engine
steps over lips up to 4.1 units. The player's rounded capsule (`COLLIDE_ELLIPSE`
→ `EntCapsule`, `mpr.h:164`) plus the `fuzzycollidesolid` margin/cutoff filters
and the `bs.add(1)` guard (`physics.cpp:1055`) are what stop it catching on small
edges.

---

## 4. Timing (`physicsframe`, `physics.cpp:1749`)

Physics are scheduled independently of rendering at `PHYSFRAMETIME = 5 ms`,
running `physsteps` catch-up frames inside each rendered frame. Player movement
uses `moveres = 10` sub-steps of `move()` per physics frame, i.e. ~0.5 ms
effective resolution. `interppos` smooths rendering between physics frames.

Measured on a real run (see
[movement-verification.md](movement-verification.md)): the main-loop `curtime` is
**variable** (5–28 ms at a 200 fps cap) but `physframetime` is **always 5 ms** and
the jitter is absorbed by `physsteps = ceil(diff/5)`. So the rule is not just “run
at 200 Hz” — a faithful port must advance in **fixed 5 ms steps and catch up by
step count**, never scale the step by a variable `delta`.

---

## 5. Port notes / deviations in the current MVP

The live player is `World/Ogz/MapPlayer.cs`. It uses the **ported octree physics**
(`World/Physics/OctaPhysics.cs` — `collide`/`move`/`trystepup`/`trystepdown`/…), so
it climbs ramp lips and stairs like the original, and shares one implementation
with the A3 replay differential. It is parented under the Z-up world node and
simulates in Sauerbraten coordinates.

Input/timing match the captured original behaviour
(`tools/refharness/traces/session-motion.input.csv`): fixed **5 ms** `MovePlayer`
steps caught up by **count**, and the whole frame's mouse delta applied **once per
rendered frame** (never per tick).

Still to do / known deviations:

- Collision volume/`moveres` are shared with A3; velocity + collision are verified
  across all stages with zero deviation (100.0% velocity, 100.0% falling, zero misses
  on event-free movement; see [movement-verification.md](movement-verification.md)).
- Water, spectator-float and the strafe-roll camera state are now ported
  (`PhysEnt.InWater`, `PhysEnt.State`, `Movement.ModifyVelocity`/
  `ModifyGravity`, `OctaPhysics.MovePlayer`): the one-time `vel /= 8` entry damp,
  the mid-body water-exit re-lookup, `physicstrigger` water transitions and the
  `CS_EDITING`/`CS_SPECTATOR` floating branch. Verified against the original via
  the A1 golden fixtures (`tools/refharness/golden/`, diffed by
  `test/WaterPhysicsTest.cs`).
- Water gun sway / jump / land / water sounds are wired through the
  `OctaPhysics.PhysTrigger` seam but not yet emitted by the game layer.
- **`bounce()`** (`physics.cpp:1402`/`:1796`, `ENT_BOUNCE` — grenades, debris,
  gibs; `weapon.cpp` `bouncer`) is **ported and wired**: `OctaPhysics.Bounce`
  (both forms), `InterpPos`, the `collide(..., playercol)` overload, the **dynent
  cache** (`ClearDynentCache`/`UpdateDynentCache`/`CheckDynentCache`/
  `OverlapsDynent`), `plcollide`/`ellipsecollide`, and the `game::bounced`/
  `dynentcollide` seams. The runtime node is `World/Entities/BounceBody.cs`
  (fixed 5 ms steps, registers itself as a dynent). Asserted against the editor
  octree by `test/BouncePhysicsTest.cs` (reflection × elasticity, gravity, water
  damping, settle) and `test/BounceBodyTest.cs` (node wiring, dynent cache, player
  hit). A golden differential would still need a bounce extension to the
  refharness, whose collision is a flat-floor stand-in.
- **Boxes/barrels/platforms are not `ENT_BOUNCE`** — they are `ENT_INANIMATE` and
  run through `moveplayer`/`moveplatform` (`fpsgame/movable.cpp`). That subsystem
  is **deliberately not ported**: it is hard-gated to single-player
  (`movable.cpp:90`), unused by the target modes (CTF/FFA/coop edit), and
  superseded by the command-based dynamic-world plan (a moving platform should be
  a `GameMode`/`Script` command stream). See [features.md](features.md).
- **Crouch is not a thing in Cube 2 / Sauerbraten r6889** — there is no crouch
  key and no variable player height in the original `src/`. The player volume is
  fixed (`radius 4.1`, `eyeheight 14`, `aboveeye 1`). Nothing to port.
- `MapPlayer.Setup` spawns at the first `ET_PLAYERSTART`; multi-spawn/team modes
  and `game::suicide` respawn are not wired. F1 toggles the spectator-float state.
- `scripts/Player.cs`, `scenes/player.tscn` and `scenes/test_level.tscn` (the old
  `move_and_slide()` prototype and its CSG level) have been removed.
