# MD5 animation ground-truth probe

Dumps results from the **original** Cube 2 / Doom 3 `.md5anim` skeletal-animation
loader (r6889) so the C# port
([`World/Entities/Md5Anim.cs`](../../World/Entities/Md5Anim.cs)) can be asserted
against the engine's exact output — not against hand-derived assumptions about the
dual-quaternion composition (`dq * invbase`, then `parent.base * dq`) or the sign
conventions in the frame data (`pos.y` negated; quat `x`,`z` negated, `w` restored
as `-sqrt(1 - x² - y² - z²)`), which are easy to get subtly wrong.

This is the skeletal-animation analogue of the octree probe
([`tools/octaprobe`](../octaprobe)) and the A1 movement harness
([`tools/refharness`](../refharness)): a small C++ driver that copies the engine's
math **verbatim**, used offline to generate golden values.

## What it covers

The frame-compose logic is copied verbatim from `engine/md5.h`
(`md5meshgroup::loadmesh`'s `joints {}` bind-pose block, and `loadanim`'s
`hierarchy`/`baseframe`/`frame` parse + per-joint compose). It uses the engine's
real `quat`/`dualquat` from `shared/geom.h`, so the arithmetic is the engine's own.

For each requested frame it prints, per bone:

```
BONE <frame> <index> <name> <tx> <ty> <tz> <rx> <ry> <rz> <rw>
```

where `(t)` is the translation recovered from the dual part (`2 * dual *
conj(real)`) and `(r)` the real (rotation) quaternion. This is the bone's local
transform relative to its parent — exactly what Godot's `Skeleton3D` bone pose and
`<idx>/position` + `<idx>/rotation` animation tracks expect.

## Build & run

```sh
./build.sh
./animprobe [MODEL] [ANIM] [FRAME]     # defaults: mrfixit idle 0
```

Golden fixtures for `mrfixit/idle` (frames 0/50/100) are committed under
[`test/golden/anim/`](../../test/golden/anim) and diffed against the C# port by
[`test/Md5AnimTest.cs`](../../test/Md5AnimTest.cs) at float precision.
