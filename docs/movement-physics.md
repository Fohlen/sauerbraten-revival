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

---

## 4. Timing (`physicsframe`, `physics.cpp:1749`)

Physics are scheduled independently of rendering at `PHYSFRAMETIME = 5 ms`,
running `physsteps` catch-up frames inside each rendered frame. Player movement
uses `moveres = 10` sub-steps of `move()` per physics frame, i.e. ~0.5 ms
effective resolution. `interppos` smooths rendering between physics frames.

To reproduce feel in Godot, run physics at **200 Hz** and avoid variable-delta
movement.

---

## 5. Port notes / deviations in the current MVP

The first prototype (`scripts/player.gd`) reproduces the **velocity model**
exactly (constants, direction, friction lerp, forward/airborne multipliers,
gravity, jump) but uses Godot's `CharacterBody3D.move_and_slide()` for collision.
Known deviations to close later:

- Collision volume is a capsule, not the original capsule/cylinder+plane model.
- No faithful `trystepup`/`trystepdown`: Godot has no automatic up-stepping, so
  stairs are not yet climbable.
- No octree/cube world; the prototype collides with ordinary Godot colliders.
- `moveres` sub-stepping is approximated by running the whole controller at 200 Hz.
- Water, crouch, spectator-float, and the exact slope-slide gravity are not ported yet.
