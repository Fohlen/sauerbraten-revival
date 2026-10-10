# Character animation (MD5 skies) — findings & required approach

**Status: the current Godot approach is wrong and must be replaced.** This records
what the original engine does, what we built, why it deforms, and how to do it
differently. See the tracking items in [features.md](features.md) §5.

---

## 1. What the original engine does

### 1.1 The MD5 model

Skeletal player/monster/mapmodels are **MD5** (`engine/md5.h`): a `.md5mesh`
(bind-pose joints + weighted vertices) and one `.md5anim` file per clip, listed in
`anims.cfg` (`md5anim "name" "file" [fps] [flags]`).

The loader (`md5.h`) applies sign conventions when reading both the mesh joints and
the anim baseframe/frames:

```
pos.y = -pos.y;  orient.x = -orient.x;  orient.z = -orient.z;  orient.restorew();
```

`quat::restorew()` (`shared/geom.h`) stores `w = -sqrt(1 - x²-y²-z²)` (negated), so
identity joints come out as `(0,0,0,-1)`.

### 1.2 Bind pose and per-bone transforms

For each bone the mesh loader sets an **absolute** bind dual-quaternion from the
joint hierarchy:

```
bone.base    = dualquat(joint.orient, joint.pos);   // absolute bind
bone.invbase = inverse(bone.base)
```

The animation evaluation (`md5.h` `loadanim`) produces, per bone `k`:

```
frame[k] = parent.base * (dq_k * invbase_k)      // parent = engine skeleton parent
```

Two facts matter and were confirmed numerically:

- **`frame[k]` is a parent-relative *delta from the bind pose*** — at the bind pose
  it collapses to identity (`frame ≈ (0,0,0)` / `(0,0,0,-1)`), which is exactly what
  the `tools/animprobe` golden dumps show.
- The skinning accumulation (`skelmodel.h:903-904`) is
  `bdata[k] = bdata[parent] * frame[k]` (root: `bdata = frame`), so **`bdata[k]` is
  the absolute delta from bind**, in **bind-space**.

### 1.3 Skinning

Vertices in the mesh are stored in **bind space**. Skinning transforms them by the
accumulated delta directly (`skelmodel.h:923-928`):

```
v_def = bdata[k] * v_bind
```

There is no separate "inverse bind matrix" at skin time — the bind is baked into the
vertex positions and `bdata` is the pure delta (identity at rest).

### 1.4 Render placement

`renderclient` (`rendermodel.cpp`) places the body at `d->feetpos()` with
`yaw = d->yaw + 90`, ignoring view pitch.

---

## 2. What we built in Godot, and why it is wrong

We parse `.md5anim` in C# (`World/Entities/Md5Anim.cs`, `Md5MeshBind.cs`,
`Quat.cs`, `Dq.cs`) — verified against the engine by `tools/animprobe`
(`test/Md5AnimTest.cs`, `test/golden/anim/`) — and build a Godot `Animation` that
drives the loaded glTF's **`Skeleton3D` bone poses**, via
`PlayerModel.BuildAnimations`.

### 2.1 `Skeleton3D` bone tracks use the bone name, not a property path

Godot's `Animation.TrackSetPath` for a skeleton bone is
**`"<skeleton node path>:<bone_name>"`** (e.g. `…/Skeleton3D:LowerArm.R`) — **not**
`…:bones/<idx>/rotation`. Getting this wrong makes the mixer silently do nothing
(no error, no motion). This was the first bug.

### 2.2 The pose mapping, and the residual deformation

Equating the two skinning systems (both operate on the same bind-space vertices):

```
engine: v_def = bdata[k] * v_bind
Godot:  v_def = globalPose[k] * globalRest[k]⁻¹ * v_bind
=>      globalPose[k] = bdata[k] * globalRest[k]
```

so each Godot bone pose is `pose[k] = globalPose[godotParent]⁻¹ * globalPose[k]`,
computed from the **absolute** `bdata` (to survive Godot's bone collapsing, see
below). `PlayerModel` uses `Skeleton3D.GetBoneGlobalRest`.

This reproduces the bind and the bulk of the motion correctly, **but the hands and
right foot remain visibly deformed**. Measured against `bdata[k]*globalRest[k]` on a
live frame, the offenders are bones on **collapsed** chains — e.g. the finger bones
whose engine parent is a `FingerNull` joint.

### 2.3 Root cause: Godot's glTF import restructures the skeleton

This is the part that cannot be worked around by a better formula:

- The original MD5 skeleton has **75 bones**.
- Godot's glTF importer **collapses bones with no skinning influence**
  (`Root`, `SpineTwist`, `Torso`, `SpineIK*`, and the `FingerNull*`/IK helper bones)
  and builds a `Skeleton3D` with **50 bones** (the skin's joints). It also
  re-parents surviving bones where ancestors were dropped.
- Our **runtime** load (`GltfDocument.GenerateScene`) additionally reshapes the node
  tree: the bones become `Skeleton3D` bones with `BoneAttachment3D` helpers, and the
  original bone **nodes** are largely discarded.

Because the engine's `frame[k]` is parent-relative in the **engine** hierarchy, and
Godot's bone is parent-relative in a **different, collapsed** hierarchy, no
per-bone formula maps them exactly: the absolute `bdata` fixes the large errors but
bones whose Godot parent is a *different* bone than their engine parent still land
wrong. The hands/feet are exactly those cases.

> Godot's **own** glTF importer does **not** animate `Skeleton3D` bones for a
> node-authored glTF: it emits `Animation` tracks that target the **bone `Node3D`s by
> path** (`…/Root/SpineTwist`, type `Rotation3D`), keeping the full node hierarchy.
> Our runtime reconstruction throws that hierarchy away.

---

## 3. Required approach (do it differently)

Animate the **bone nodes**, not the reconstructed `Skeleton3D` bones — matching what
Godot's glTF importer actually emits. Concrete options, in preference order:

1. **Bake animated `.glb`s offline and import them as Godot Resources.** Convert each
   `.md5anim` clip into the `.glb` with assimp (its MD5 importer loads exactly one
   sibling `<meshbase>.md5anim` per export — confirmed by experiment), then let
   Godot's **importer** own the model: it preserves the full node hierarchy and
   emits node-path animations that play correctly. Multiple clips means one glb per
   clip (or a merge step). This is the least bespoke code and the most robust.
   Caveat: this changes the importer (`tools/model-import`) and the manifest.
2. **Block Godot from collapsing bones.** Investigate `GltfState.ImportAsSkeletonBones`
   and the glTF `skin` layout so the runtime load keeps the full 75-node hierarchy,
   then animate the **bone nodes** by path (the engine `frame[k]` maps directly to
   node parent-relative transforms, no `bdata`/`globalRest` gymnastics).
3. **CPU-skin ourselves.** Evaluate `bdata[k]` and deform the mesh in C#, bypassing
   Godot's skeleton entirely. Fully faithful but heavy and defeats the GPU path.

**Do not** continue tuning the `Skeleton3D` bone-pose formula: the collapsed
hierarchy is a lossy reshape, so it cannot be exact for all bones.

### Reusable pieces (keep)

- `Md5Anim` / `Md5MeshBind` / `Quat` / `Dq` — the parser and pose math are correct
  and verified against the engine (`tools/animprobe`, `test/golden/anim/`).
- `tools/animprobe` — the golden ground-truth probe; extend it if the new path needs
  different comparison points.

---

## 4. Verification notes

- **The AABB is not a valid check** for orientation/pose — a sideways or
  differently-posed model can share the same bounds. Compare **basis vectors** or
  **skinned vertex positions**.
- **Headless `AnimationPlayer.Seek`/`Advance` does not reliably apply** poses in the
  GoDotTest harness; use a real running frame (windowed) to observe animation.
- The one truly independent check, not yet done, is comparing Godot's **actual
  skinned vertex output** against the engine's `bdata`-transformed bind vertices.
