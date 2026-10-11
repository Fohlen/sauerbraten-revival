# Character animation (MD5 skies) — findings & required approach

**Status: implemented.** The player body now animates correctly. This records what
why the obvious Godot approach failed and what we do instead. See the tracking
items in [features.md](features.md) §5.

> **Chosen approach (option 1):** bake every `.md5anim` clip into the model glb at
> import time and let Godot's own importer own the skeleton + `AnimationPlayer`.
> `tools/model-import/import_models.py` (`bake_animated_glb`) exports once per clip
> (assimp loads a sibling `<meshbase>.md5anim`) and `gltf_anim_merge.py` merges them
> into one glb. `PlayerModel` then just uses the baked `AnimationPlayer`. The rest of
> this document explains why the "map engine poses onto Godot's skeleton" route was
> abandoned.

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

### 1.5 Animation parts (`md5animpart`) and pitch (`md5pitch`)

The skeleton is split into **animation parts** by `md5animpart <bone>...`
(`skelmodel.h` `setanimpart`). Part 0 is the implicit full-body part; each
`md5animpart` opens a new part whose mask is the named bone **plus its whole
subtree** (`expandbonemask`). A bone belongs to the last part that claims it
(`applybonemask`), and each part is animated independently: `interpbones` reads the
frame of `as[partmask[bone]]` (`skelmodel.h:871`).

For the player bodies this is what produces "walk while aiming": part 0 animates
the legs, part 1 masks `Spine1` (the upper body). The clips are the **same files**
in both parts, but their selection **priority** differs (`anims.cfg`
`md5anim "name" "file" [fps] [priority]`): on part 0 movement wins
(`forward` priority `1` beats `hold N` priority `-2`), on part 1 `hold N` wins
(priority `0` beats movement priority `-1`). `animmodel.h` `calcanim` picks the
higher-priority spec per part. Some models (inky, captaincannon) add a third part
for wings/cape.

`md5pitch <bone> <scale> <offset> <min> <max>` adds a view-pitch rotation to a
named bone (`Spine1`/`Spine` and `Head` for the player bodies; an empty bone name
sets the part-wide pitch used by the hudguns). It is applied after skinning the
bone's pose (`interpbones`, `b := pitchscale*pitch + pitchoffset`, clamped).

**Import status.** `tools/model-import/import_models.py` parses `md5animpart` and
`md5pitch` and records them in the manifest as `animParts` (the bone masks),
`partAnims` (the per-part clip table with fps + priority) and `pitch`
(`World/Entities/ModelCatalog.cs` reads them). The glb itself stays one full clip
per name (assimp has no mask concept), so the runtime still composes the parts at
playback; applying them is the remaining step.

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

## 3. Approach (implemented: option 1)

Animate the **bone nodes** the way Godot's glTF importer intends, by letting the
glb carry the clips. Options, in preference order:

1. **[CHOSEN] Bake animated `.glb`s offline and import them as Godot Resources.**
   Convert each `.md5anim` clip into the `.glb` with assimp (its MD5 importer loads
   exactly one sibling `<meshbase>.md5anim` per export), merge the per-clip exports
   (`tools/model-import/gltf_anim_merge.py`), then let Godot's **importer** own the
   model. It preserves the full node hierarchy and emits node-path animations that
   play correctly. `PlayerModel` uses the baked `AnimationPlayer` directly.
   - Body placement: `ModelRotationDegrees = (0,-90,0)` (upright), body yaw offset
     `+180` (`MapPlayer.SyncBodyModel`, not the engine's `+90`), `+10` Z nudge.
   - Cost: the merge currently duplicates the mesh per clip (~10-13 MB per player
     glb). Optimise by appending only the animation bufferViews if it matters.
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

---

## 5. First-person view model (hudgun) coordinate mapping

In the original engine (`fpsgame/render.cpp` `drawhudmodel`), hudguns are rendered with:
```c
rendermodel(NULL, gunname, anim, sway, d->yaw + 90, d->pitch, MDL_LIGHT|MDL_HUD, ...);
```
Sauerbraten's camera view matrix applies `rotate_around_z(-d->yaw)` and `rotate_around_x(-d->pitch)`. In camera space, the player's yaw and pitch cancel out completely.

In OpenGL / Sauerbraten camera coordinates:
- The model forward axis (+X in Sauerbraten model coordinates) maps to Camera Forward (`-Z` in OpenGL / Godot camera space).
- The model lateral axis (+Y in Sauerbraten model coordinates) maps to Camera Right (`+X` in OpenGL / Godot camera space).
- The model vertical axis (+Z in Sauerbraten model coordinates) maps to Camera Up (`+Y` in OpenGL / Godot camera space).

### The glTF orientation & translation
When MD5 meshes are imported to glTF via assimp (`tools/model-import/import_models.py`), assimp introduces `<MD5_Root>` with a $-90^\circ$ rotation around X:
- MD5 forward (+Y) maps to glTF `-Z` (Godot Camera Forward).
- MD5 right (+X) maps to glTF `+X` (Godot Camera Right).
- MD5 up (+Z) maps to glTF `+Y` (Godot Camera Up).

Because the glTF is already aligned with Godot's camera frame, the hudgun's camera-space basis is simply `Basis.Identity`.

For `mdltrans tx ty tz` (e.g. `1.0 -2.0 -0.3` for `mrfixit/hudguns/rifle` with scale 0.75):
- Model forward `tx` moves along Camera Forward ($-Z$): $Z_{cam} = -tx \cdot scale$.
- Model lateral `ty` moves along Camera Right ($+X$): $X_{cam} = ty \cdot scale$.
- Model vertical `tz` moves along Camera Up ($+Y$): $Y_{cam} = tz \cdot scale$.

Previously, `HudgunModel.cs` erroneously applied an extra 90° yaw rotation in camera space (`EngineHudYaw`), which negated and swapped axes (turning $X_{cam}$ into $+1.5$ off the right edge of the screen, and rotating the gun perpendicular to view). With the direct camera mapping `(ty, tz, -tx) * scale`, the hudgun sits naturally at `(-1.5, -0.225, -0.75)` without custom offsets.

### Linked multi-part models (hands + weapon)
In models configured with `md5link` (e.g. `mrfixit/hudguns/rifle`), Part 0 (the hands) attaches Part 1 (the weapon mesh) beneath a tag bone on Part 0 (`tag_bicep` -> `Bicep.R`).
Because assimp converts every MD5 mesh with `<MD5_Root>` applying a $-90^\circ$ rotation around X, Part 0's bone attachment is already in camera/glTF space. Attaching Part 1 directly under `HudgunLink_Bicep_R` without adjustment compounded Part 1's own `<MD5_Root>`, rotating the weapon mesh an additional $-90^\circ$ around X (pointing the weapon downward instead of forward). Counteracting the redundant root transform on linked child parts (`part.Basis = new Basis(Vector3.Right, Mathf.Pi / 2f)`) restores forward alignment between the hands and the weapon.
