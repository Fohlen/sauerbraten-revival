# Frame Stutter & CPU Crunch Ablation Report

## Executive Summary

Using [`AblationProbe`](../../scripts/tracing/AblationProbe.cs) and [`run_ablation.py`](run_ablation.py), we executed a 28-combination ablation study over the CTF demo `forge_2026-11-01_21.35.57.fbs` (both an unthrottled 10s active window + 5s post-demo idle phase, and the full 27.3s / 5,400-frame demo) plus main-menu idle baselines.

The ablation data cleanly separates **frame stutter spikes** from **steady-state CPU & GC crunch**, isolating four prime suspects:

| # | Suspect | Symptom Measured by Ablation | Root Cause | Impact of Disabling / Fixing |
| :- | :--- | :--- | :--- | :--- |
| **1** | **Synchronous `.glb` Re-instantiation on Weapon Switch & Projectile Spawn** | `30–51 ms` frame stutters (`PhysicsMs` spikes from `0.47 ms` mean to **`44.3–57.7 ms`** in a single 5 ms tick; `231–328 KB` alloc spike) | `HudgunModel.SetGun` and `PlayerModel.SetViewWeapon` call `Free()` and synchronously re-load / re-instantiate `.glb` scenes + `Skeleton3D` rigs inside `_PhysicsProcess`. `MapPlayer.TryAttack` synchronously loads `projectiles_grenade.glb` (`17.9 ms` spike). | Disabling `playermodel,hudgun` (`no_skeletal_models`) eliminates **100% of `>33.3 ms` stutters** (`7 -> 0` in full demo) and cuts excess stutter time by **89–96%**. |
| **2** | **Uncached `CollisionVisibleMask` & Heap Arrays in `OctaPhysics` / `OctaVisible`** | **`87.5 KB/frame`** managed allocations during active play (**`55.4 KB/frame`** standing still) $\rightarrow$ **57 Gen0 GCs** in 27.3s | `FuzzyCollideSolid` & `FuzzyCollidePlanes` recompute `OctaVisible.CollisionVisibleMask` on every cube collision check (20–100×/frame), which allocates `new Vector2I[]` / `new Vector3I[]` arrays on the heap inside recursive octree loops (`OccludesFace`, `ClipFaceVecs`). | Disabling `worldphysics` (`all_off_no_physics`) drops allocations from `84.42 KB/frame` to **`0.02 KB/frame`** (**99.97% reduction**, `0` GCs). |
| **3** | **Main Menu 3D `StageSubViewport` & Shadow Pass** | **`41.5% CPU`** and **`53` draw calls/frame** while sitting idle at the main menu (`menu_idle_on`) | `StageSubViewport` renders at 200 FPS with a shadow-casting `DirectionalLight3D` (`KeyLight.shadow_enabled = true`, 4-cascade PSSM shadow map in an empty stage) + continuous `AnimationPlayer` skinning. `MainMenu.Close()` also leaves `ProcessMode = Always` active during gameplay. | Disabling `menu` (`menu_idle_off`) drops idle draw calls from `53` to **`0`** and cuts idle CPU from `41.5%` to **`9.4–21.9%`**. |
| **4** | **Per-Frame `_Process` Churn (`HudController`, `MapPlayer`/`PlayerView`, `MapWorld`)** | **`48.3% CPU`** standing still (`post_demo_idle`); `_Process` takes `0.36 ms/frame` (spikes to `4.7 ms`) + `~3 KB/frame` allocs | `HudController.ShouldRefreshStatusBar()` returns `true` unconditionally on CTF maps (`Flags.Count > 0`), allocating up to 14 `new AtlasTexture()` objects and calling `AddThemeFontSizeOverride` every frame. `MapPlayer._Process` and `PlayerView._Process` duplicate all work (`2×/frame`) and run 8 failed `GetNodeOrNull("Body")` lookups/frame. | Disabling `_Process` hooks (`no_process_hooks`) cuts `_Process` mean time by **75%** (`0.36 -> 0.09 ms`), max spikes from `4.7 -> 0.6 ms`, and saves **~6.8% total CPU**. |

---

## Key Ablation Comparison Table

### 10s Unthrottled Matrix + Idle Baselines

| Label | Phase | Frames | FPS | Mean | P50 | P95 | P99 | Max (ms) | Jitter P99 | >8.3ms | >16.7ms | >33.3ms | Excess (ms) | CPU % | CPU ms/f | Proc (mean/max) | Phys (mean/max) | Rend (mean/max) | Draws | Alloc KB/f | GC (0/1/2) |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :--- |
| `baseline` | `active_demo` | 1891 | 189.0 | 5.29 | 5.01 | 6.69 | 12.96 | 154.45 | 14.46 | 45 | 11 | **5** | **312.3** | 50.3% | 2.66 | 0.36/4.7 | **0.47/44.3** | 4.46/148.1 | 172 | **87.51** | 27/3/0 |
| `baseline` | `post_demo_idle` | 999 | 199.8 | 5.01 | 5.04 | 6.07 | 7.44 | 14.99 | 5.44 | 7 | 0 | 0 | 0.0 | **48.3%** | 2.42 | 0.40/1.3 | 0.29/5.5 | 4.32/12.1 | 136 | **55.41** | 9/1/0 |
| `all_off` | `active_demo` | 1980 | 197.9 | 5.05 | 5.01 | 6.49 | 8.77 | 42.69 | 12.59 | 22 | 5 | 2 | 59.3 | 31.6% | 1.60 | 0.03/0.7 | 0.50/4.5 | 4.52/39.2 | 0 | **84.42** | 27/1/0 |
| `all_off_no_physics` | `active_demo` | 982 | 196.3 | 5.09 | 5.00 | 5.50 | 10.70 | 28.60 | 15.12 | 18 | 3 | 0 | 27.9 | 29.1% | 1.48 | 0.05/0.4 | 0.01/0.1 | 5.03/28.5 | 0 | **0.02** | **0/0/0** |
| `menu_idle_on` | `menu_idle` | 958 | 191.5 | 5.22 | 5.00 | 6.14 | 14.38 | 20.91 | 12.59 | 43 | 3 | 0 | 10.4 | **41.5%** | 2.16 | 0.09/0.5 | 0.01/0.5 | 5.11/20.9 | **53** | 0.04 | 0/0/0 |
| `menu_idle_off` | `menu_idle` | 975 | 194.8 | 5.13 | 5.00 | 5.52 | 15.34 | 26.14 | 16.63 | 26 | 7 | 0 | 22.4 | **21.9%** | 1.13 | 0.01/0.3 | 0.01/0.1 | 5.11/26.1 | **0** | 0.01 | 0/0/0 |
| `no_hud` | `active_demo` | 1978 | 197.7 | 5.06 | 5.03 | 6.33 | 7.22 | 49.26 | 6.75 | 14 | 5 | 3 | 86.0 | 46.5% | 2.35 | **0.23/0.9** | 0.47/44.6 | 4.35/18.0 | 163 | **84.54** | 27/1/0 |
| `no_playermodel` | `active_demo` | 1977 | 197.7 | 5.06 | 5.04 | 6.63 | 7.92 | 52.37 | 6.61 | 16 | 5 | 2 | 77.6 | 49.1% | 2.48 | 0.33/2.4 | 0.47/48.5 | 4.26/18.7 | 172 | 85.58 | 28/2/0 |
| `no_hudgun` | `active_demo` | 1972 | 197.1 | 5.07 | 5.05 | 6.65 | 7.37 | 49.41 | 6.11 | 15 | 5 | 4 | 103.5 | 45.8% | 2.32 | 0.33/3.6 | 0.47/44.3 | 4.27/31.9 | 170 | 86.23 | 28/2/0 |
| `no_skeletal_models` | `active_demo` | 1993 | **199.3** | **5.02** | 5.04 | 6.57 | **7.23** | **23.49** | **5.24** | **8** | **2** | **0** | **12.5** | 46.9% | 2.35 | 0.31/2.2 | **0.42/17.9** | 4.29/20.7 | 170 | 84.95 | 28/2/0 |
| `no_process_hooks` | `active_demo` | 1973 | 197.2 | 5.07 | 5.05 | 6.50 | 7.55 | 48.91 | 6.96 | 13 | 5 | 3 | 96.2 | 43.5% | 2.21 | **0.09/0.6** | 0.51/43.9 | 4.46/19.4 | 162 | 84.61 | 27/1/0 |
| `only_worldmesh` | `active_demo` | 1999 | **199.8** | **5.00** | 5.02 | 6.41 | **7.11** | **16.16** | **4.68** | **4** | **0** | **0** | **0.0** | 41.0% | 2.05 | 0.04/0.4 | **0.47/4.4** | 4.49/15.0 | 159 | 83.15 | 27/1/0 |

### Full 27.3s Demo Comparison (`baseline` vs. Skeletal/Visual Ablations)

| Label | Frames | FPS | P99 (ms) | Max (ms) | Jitter P99 | >8.3ms | >16.7ms | >33.3ms | Excess Stutter (ms) | CPU % | Draws | Alloc KB/f | GC (0/1/2) |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :--- |
| `baseline` | 5358 | 197.3 | 7.63 | **51.45** | 7.39 | 42 | 11 | **7** | **231.2 ms** | 47.4% | 168 | 65.14 | 57/4/0 |
| `no_skeletal_models` (`playermodel,hudgun`) | 5414 | **199.3** | **7.31** | **25.39** | **5.30** | **17** | **3** | **0** | **25.4 ms (-89%)** | 44.8% | 166 | 64.33 | 57/4/0 |
| `only_worldmesh` | 5421 | **199.6** | **6.93** | **22.03** | **4.25** | **10** | **3** | **0** | **10.0 ms (-96%)** | 42.2% | 155 | 62.88 | 56/3/0 |

---

## Section 1: Synchronous `.glb` Re-instantiation on Weapon Switch & Projectile Spawn

### 1.1 Evidence from Per-Frame Stutter Logs
In `baseline`, severe gameplay frame spikes occur at `t=3.35s` (`30.1 ms`), `t=4.20s` (`49.3 ms`), `t=6.94s` (`38.6 ms`), and `t=8.78s` (`44.0 ms`). Inspecting the per-frame stutter trace shows that on those exact frames:
- `PhysicsMs` jumps from a `0.47 ms` average to **`24.4–44.3 ms`** inside a **single** 5 ms physics tick (`PhysSteps = 1`).
- Managed heap allocation on that single frame jumps from `85 KB` to **`231–328 KB`**.
- Disabling `playermodel` alone (`no_playermodel`) or `hudgun` alone (`no_hudgun`) leaves the other half of the weapon-switch spike intact (`PhysicsMsMax = 48.5 ms` and `44.3 ms`).
- Disabling **both** `playermodel` and `hudgun` (`no_skeletal_models`) eliminates **100% of `>33.3 ms` frame stutters** (`0` across the entire 27.3s demo) and drops `PhysicsMsMax` to `17.9 ms`.
- The single remaining `17.9 ms` `PhysicsMs` spike at `t=3.21s` disappears in `only_worldmesh` (`PhysicsMsMax = 4.4 ms`, `0` frames `>16.7 ms`).

### 1.2 Root Cause in Code
During `_PhysicsProcess` $\rightarrow$ [`WorldSimulation.Step`](../../World/Runtime/WorldSimulation.cs#L44-L48) $\rightarrow$ [`MapPlayer.PrePhysicsStep`](../../World/Game/Players/MapPlayer.cs#L604-L616) $\rightarrow$ [`MapPlayer.ApplyUpdate`](../../World/Game/Players/MapPlayer.cs#L802-L805), whenever the player switches weapons, [`MapPlayer.SelectWeapon`](../../World/Game/Players/MapPlayer.cs#L910-L919) synchronously invokes:
1. **[`HudgunModel.SetGun`](../../World/Models/HudgunModel.cs#L86-L154) $\rightarrow$ `BuildModel()`:**
   - Calls `part.Free()` on both `Part0` (first-person hands) and `Part1` (weapon), even though `Part0` (`hudguns/hands`) is identical across all weapons for a given playermodel.
   - Creates two new [`ModelInstance`](../../World/Models/ModelInstance.cs#L97-L136) nodes, which call `ResourceLoader.Load<PackedScene>(GlbPath)` and `packed.Instantiate()`, rebuilding the full MD5 `Skeleton3D`, skinning buffers, `AnimationPlayer`, and `BoneAttachment3D` on the main thread.
2. **[`PlayerModel.SetViewWeapon`](../../World/Models/PlayerModel.cs#L133-L225) $\rightarrow$ `AttachViewWeapon()`:**
   - Calls `ViewWeapon.Free()` and synchronously loads and instantiates the third-person `vwep/*` `.glb` (`PlayerViewWeapon`).
   - At [`PlayerModel.cs:220`](../../World/Models/PlayerModel.cs#L220), `_skinCache` is `null` when not passed by `MapPlayer.EnsurePlayerModel()`, so every weapon switch allocates a `new Dictionary<string, Material>()` and rebuilds the `ShaderMaterial` from scratch.
3. **[`ModelInstance.TryLoadModel`](../../World/Models/ModelInstance.cs#L97-L136) retains no strong `PackedScene` reference:**
   - For non-skeletal models (`KeepSkeleton == false`, such as [`BounceBody`](../../World/Weapons/BounceBody.cs) grenades spawned in [`MapPlayer.TryAttack`](../../World/Game/Players/MapPlayer.cs#L1117-L1141)), `scene.Free()` at [`ModelInstance.cs:130`](../../World/Models/ModelInstance.cs#L130) immediately drops the temporary scene, allowing the `PackedScene` to be evicted and re-loaded from disk on subsequent spawns (`17.9 ms` spike at `t=3.21s`).

### 1.3 Recommended Fix
- **Pre-build & retain the 7 weapon subtrees on `HudgunModel` and `PlayerModel`:**
  There are only 7 selectable weapons (`fist`, `shotg`, `chaing`, `rl`, `rifle`, `gl`, `pistol`). Total disk size per playermodel is ~3.5–4.5 MB for `hudguns` and ~200 KB for all 7 `vwep/*` models. Pre-instantiating all 7 weapon children at player setup and toggling `.Visible` + `.ProcessMode` makes `SetGun()` and `SetViewWeapon()` `<0.01 ms` and zero-allocation.
- **Maintain a persistent `PackedScene` + `Material` cache (`ModelSceneCache`):**
  Keep strong references to loaded `PackedScene` resources (and pre-warm projectiles `projectiles/grenade` and `projectiles/rocket` at map load) and pass a shared `_skinCache` into `PlayerModel`.

---

## Section 2: Per-Frame Managed Heap Allocations (`85 KB/frame` in `WorldSimulation.Step` / `OctaPhysics`)

### 2.1 Evidence from Ablation Matrix
- Comparing `baseline` (`87.51 KB/frame`, `27` Gen0 GCs in 10s) vs. `all_off` (`84.42 KB/frame`, `27` Gen0 GCs in 10s) proves that **97% of per-frame heap allocations happen even when all visuals, UI, audio, and `_Process` hooks are disabled**.
- Even standing completely still in `post_demo_idle` allocates **`55.41 KB/frame`**.
- Disabling `worldphysics` (`all_off_no_physics`) drops allocations from `84.42 KB/frame` to **`0.02 KB/frame`** (`0` GCs), isolating the allocations to [`WorldSimulation.Step`](../../World/Runtime/WorldSimulation.cs#L31-L90).

### 2.2 Root Cause in Code
1. **Uncached `OctaVisible.CollisionVisibleMask` on every cube collision check:**
   - In original Sauerbraten (`octarender.cpp:1567` `setcubevisibility`), the 6-bit face collision mask is computed once at mesh time and stored in `c.visible`, so `fuzzycollidesolid` (`physics.cpp:786`) is a 1-byte read (`c.visible`).
   - In our port, [`FuzzyCollideSolid` (`OctaPhysics.cs:391`)](../../World/Physics/OctaPhysics.cs#L391) and [`FuzzyCollidePlanes` (`OctaPhysics.cs:422`)](../../World/Physics/OctaPhysics.cs#L422) call [`OctaVisible.CollisionVisibleMask(_octa, c, co, size)`](../../World/Physics/OctaVisible.cs#L340-L346) **live on every collision check** (20–100+ times per frame across the 10 `moveres` sub-steps of `MovePlayer`).
   - In `FuzzyCollidePlanes`, `CollisionVisibleMask` is even computed **before** [`OctaClipping.GetClipPlanes`](../../World/Physics/OctaClipping.cs#L104-L106) checks `Cache.TryGetValue(c, out var cached)`.
2. **Heap array allocations inside recursive octree loops in [`OctaVisible.cs`](../../World/Physics/OctaVisible.cs):**
   - [`ClassifyFace` (`lines 324, 331`)](../../World/Physics/OctaVisible.cs#L324-L331) allocates `new Vector2I[4]` arrays (`cf`, `of`, `cf2`).
   - [`GenFaceVecs` (`line 96`)](../../World/Physics/OctaVisible.cs#L96) allocates `new Vector3I[4]` whenever `v == null`.
   - [`OccludesFace` (`lines 194, 201, 205`)](../../World/Physics/OctaVisible.cs#L188-L219) recursively walks every subdivided child of the neighbour cube and allocates `new Vector2I[8]` (`nf`), `new Vector2I[8]` (`cf`), and `new Vector2I[4]` (`of`) at **every leaf**.
   - [`ClipFaceVecs` (`lines 164–165`)](../../World/Physics/OctaVisible.cs#L164-L165) allocates `var corner = new[] { ... }` plus **four 1-element `new[] { corner[i] }` arrays inside a `for` loop** on every call:
     ```csharp
     var corner = new[] { new Vector2I(cx, cy), new Vector2I(cx + size, cy), new Vector2I(cx + size, cy + size), new Vector2I(cx, cy + size) };
     for (int i = 0; i < 4; i++) if (InsideFace(new[] { corner[i] }, 1, o, numo)) rvecs[r++] = corner[i];
     ```
3. **Three smaller per-step heap allocations:**
   - [`EntCapsule` (`OctaClipping.cs:45`)](../../World/Physics/OctaClipping.cs#L45) is a `sealed class` instead of a `readonly struct`, allocated via `new EntCapsule(d)` on every deformed cube test ([`OctaPhysics.cs:453`](../../World/Physics/OctaPhysics.cs#L453)).
   - [`LoopDynentCache` (`OctaPhysics.cs:334`)](../../World/Physics/OctaPhysics.cs#L334) takes a `Func<int, int, bool>` delegate, allocating a capturing closure every 5 ms step in `UpdateDynentCache` ([`line 310`](../../World/Physics/OctaPhysics.cs#L310)) and `PlCollide` ([`line 156`](../../World/Physics/OctaPhysics.cs#L156)).
   - [`MapPlayer.Step` (`MapPlayer.cs:1340`)](../../World/Game/Players/MapPlayer.cs#L1340) allocates `new IExplosionTarget[] { this }` every tick even when `GetParent() is IGameWorld` and the array is unused.

### 2.3 Recommended Fix
1. Convert all temporary `Vector2I[]` and `Vector3I[]` buffers in [`OctaVisible.cs`](../../World/Physics/OctaVisible.cs) to `Span<T>` / `ReadOnlySpan<T>` with `stackalloc`, and add a scalar `InsideFace(Vector2I pt, ReadOnlySpan<Vector2I> o, int numo)` overload.
2. Move `OctaVisible.CollisionVisibleMask` inside `OctaClipping.GetClipPlanes` *after* the `Cache.TryGetValue(c, out var cached)` check, and cache the 6-bit solid collision mask per `Cube` in `OctaClipping.GetCollisionVisibleMask`.
3. Make `EntCapsule` a `readonly struct`, replace the `LoopDynentCache` delegate with direct `for` loops, and move `explosionCandidates` inside the `if (GetParent() is not IGameWorld)` block in `MapPlayer.Step`.

---

## Section 3: Main Menu Idle CPU Crunch (`MainMenu` & `StageSubViewport`)

### 3.1 Evidence from Ablation Matrix
- Sitting idle at the main menu (`menu_idle_on`) consumes **`41.5% CPU`** (`2.16 CPU ms/frame`, `_Process` mean `0.09 ms`) and submits **`53` draw calls/frame** at 200 FPS (`10,600 draw calls/sec`).
- Disabling `MainMenu` (`menu_idle_off`) drops draw calls to **`0`**, reduces `_Process` mean to `0.01 ms`, and cuts idle CPU to **`21.9%`** (`9.4%` steady-state).

### 3.2 Root Cause in Code
1. **Shadow-casting `DirectionalLight3D` in an empty preview stage ([`scenes/main_menu.tscn:468-471`](../../scenes/main_menu.tscn#L468-L471)):**
   `KeyLight` inside `StageSubViewport` has `shadow_enabled = true`. Although there is no floor or backdrop mesh in `StageSubViewport` to receive shadows, Godot's Forward+ renderer executes a 4-cascade directional PSSM shadow map pass of the skinned `PlayerModel` 200 times per second.
2. **200 Hz `StageSubViewport` rendering & skeletal animation ([`scripts/MainMenu.cs:384-390`](../../scripts/MainMenu.cs#L384-L390)):**
   `StageSubViewport` re-renders and skins the preview `PlayerModel`'s `"idle"` animation at 200 FPS.
3. **`MainMenu.Close()` leaves `ProcessMode = Always` active during matches ([`scripts/MainMenu.cs:127, 226-230`](../../scripts/MainMenu.cs#L226-L230)):**
   `MainMenu` is a `CanvasLayer` with `ProcessMode = ProcessModeEnum.Always`. When `Close()` is called, it only sets `Visible = false` without disabling `ProcessMode` or `StageSubViewport.RenderTargetUpdateMode`, leaving the preview model's `AnimationPlayer` ticking in the background during gameplay.

### 3.3 Recommended Fix
1. Set `shadow_enabled = false` on `KeyLight` in [`scenes/main_menu.tscn:471`](../../scenes/main_menu.tscn#L471).
2. In [`MainMenu.Close()`](../../scripts/MainMenu.cs#L226), set `ProcessMode = ProcessModeEnum.Disabled` and `StageSubViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled` (and restore both in [`MainMenu.Open()`](../../scripts/MainMenu.cs#L215)).

---

## Section 4: In-Game `_Process` & Post-Demo Idle CPU Crunch (`HudController`, `MapPlayer`, `PlayerView`, `MapWorld`)

### 4.1 Evidence from Ablation Matrix
- Standing still after the demo ends (`baseline` `post_demo_idle`) consumes **`48.3% CPU`** at 200 FPS (`0.40 ms/frame` in `_Process`).
- Disabling `hud` (`no_hud`) drops `_Process` mean from `0.36 ms` to **`0.23 ms` (-36%)**, drops `_Process` max spike from `4.7 ms` to **`0.9 ms`**, reduces draw calls by `9`, and saves **`~3.0 KB/frame`** of heap allocations.
- Disabling all `_Process` hooks (`no_process_hooks`) drops `_Process` mean by **75%** (`0.36 -> 0.09 ms`) and lowers total CPU by **~6.8%**.

### 4.2 Root Cause in Code
1. **[`HudController._Process`](../../World/Hud/HudController.cs#L740-L763) rebuilds the entire status bar and allocates `new AtlasTexture()` objects every frame:**
   - [`ShouldRefreshStatusBar()` (`HudController.cs:761`)](../../World/Hud/HudController.cs#L761) contains:
     ```csharp
     if (_player.Flags != null && _player.Flags.Flags.Count > 0) return true;
     ```
     On any CTF map, `Flags.Count > 0` is always true, so [`UpdateStatusBar()` (`lines 809–1039`)](../../World/Hud/HudController.cs#L809-L1039) runs **every single frame (200 Hz)**.
   - Inside `UpdateStatusBar()`, [`UpdateAmmoHud` (`line 1069`)](../../World/Hud/HudController.cs#L1069) and [`UpdateAmmoBar` (`line 1140`)](../../World/Hud/HudController.cs#L1140) call [`CreateAtlasTexture` (`lines 722–738`)](../../World/Hud/HudController.cs#L722-L738) for every weapon slot, allocating **up to 14 `new AtlasTexture()` Godot objects per frame (2,800/sec)** plus `new List<AmmoHudItem>()` and `new List<AmmoBarItem>()` lists.
   - [`UpdateGameClock()` (`lines 1170–1216`)](../../World/Hud/HudController.cs#L1170-L1216) and [`UpdateHudScore()` (`lines 1221–1288`)](../../World/Hud/HudController.cs#L1221-L1288) format strings and call `AddThemeFontSizeOverride("font_size", fontSize)` on every frame (200 Hz), dirtying Godot `Control` theme caches and forcing text re-layout even when the clock second (`currentMillis / 1000`) and team scores have not changed.
2. **Duplicate per-frame work in [`MapPlayer._Process`](../../World/Game/Players/MapPlayer.cs#L1479-L1484) and [`PlayerView._Process`](../../World/Game/Players/PlayerView.cs#L233-L239):**
   - Both `MapPlayer._Process` and its child `PlayerView._Process` call `SyncTransform()`, `UpdateBodyAnim()`, and `UpdateHudgunAnim()` every frame—running all camera/model transform math and animation checks **twice per frame**.
   - `SyncCamera()`, `SyncBodyModel()`, `UpdateBodyAnim()`, and `UpdateHudgunAnim()` each call [`ResolveNodes()` (`PlayerView.cs:79-95`)](../../World/Game/Players/PlayerView.cs#L79-L95), which runs `PlaceholderBody ??= parentPlayer.GetNodeOrNull<MeshInstance3D>("Body")`. Because `"Body"` does not exist in `player_scene.tscn`, `GetNodeOrNull("Body")` executes **8 native node-path lookups per frame**.
   - [`MapPlayer.SelectBodyAnim()` (`MapPlayer.cs:861, 890`)](../../World/Game/Players/MapPlayer.cs#L861-L890) allocates interpolated strings (`$"hold {Combat.GunSelect + 1}"`, `$"attack {gun + 1}"`) twice per frame even in first-person mode where `PlayerModel` is hidden.
3. **Per-frame delegate allocation in [`MapWorld._Process` (`MapWorld.cs:475`)](../../World/Runtime/Formats/Ogz/MapWorld.cs#L475):**
   - Passing the method group `getOwnerInfo: GetPlayerOwnerInfo` inside the `_flagNodes` loop allocates a new `FlagOwnerInfoLookup` delegate instance per flag every frame.

### 4.3 Recommended Fix
1. **In [`HudController.cs`](../../World/Hud/HudController.cs):**
   - Cache `AtlasTexture` instances in a `Dictionary<HudIcon, AtlasTexture>` instead of allocating `new AtlasTexture()` per call.
   - Replace `_player.Flags.Flags.Count > 0` in `ShouldRefreshStatusBar()` with a check on whether the player's carried flag state (`GetCarriedFlagState`) or hold-timer second actually changed.
   - Cache `_lastClockSecond = currentMillis / 1000` in `UpdateGameClock()` and `(_lastScore1, _lastScore2)` in `UpdateHudScore()`, early-returning when unchanged and only calling `AddThemeFontSizeOverride` when `fontSize` changes.
2. **In [`MapPlayer.cs`](../../World/Game/Players/MapPlayer.cs) & [`PlayerView.cs`](../../World/Game/Players/PlayerView.cs):**
   - Remove the duplicate `_Process` override on `PlayerView` (letting `MapPlayer._Process` drive `View` once per frame), guard `ResolveNodes()` with a `_nodesResolved` flag, and use static string tables for `"hold 1".."hold 7"` and `"attack 1".."attack 7"`.
3. **In [`MapWorld.cs`](../../World/Runtime/Formats/Ogz/MapWorld.cs#L475):**
   - Cache `GetPlayerOwnerInfo` in a `readonly FlagOwnerInfoLookup` field.
