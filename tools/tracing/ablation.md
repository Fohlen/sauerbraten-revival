# Frame Stutter & CPU Crunch Ablation Guide

This document defines the ablation methodology, metrics, component switches, and execution stages used to isolate **frame time stutter** and **CPU crunch (active and idle)** by code path in **Sauerbraten Revival**.

---

## Stage 1: What Do We Measure?

Frame stutter (tail latency spikes) and CPU crunch (sustained core utilization) stem from different mechanisms, so each run records both **per-frame internal telemetry** (after map build finishes) and **OS process CPU accounting**.

### 1. Frame Stutter Metrics
Measured every rendered frame in `_Process` via `Time.GetTicksUsec()` (ignoring the initial warmup frames while the scene enters the tree):

* **Throughput & Central Tendency:**
  * `mean_ms`, `p50_ms` (median), and `effective_fps` (compared against the default `Engine.MaxFps = 200` $\rightarrow 5.0\text{ ms}$ frame budget).
* **Tail Latency (Stutter Distribution):**
  * `p90_ms`, `p95_ms`, `p99_ms`, `p99_9_ms`, and `max_ms`.
* **Frame-to-Frame Jitter ($\Delta|\Delta t_i - \Delta t_{i-1}|$):**
  * `jitter_p95_ms`, `jitter_p99_ms`, and `jitter_max_ms` — isolates sudden single-frame hitches from steady heavy load.
* **Hitch Threshold Counts & Excess Stutter Time:**
  * `frames_over_8_3ms` ($>1.67\times$ the 5 ms budget / $<120\text{ FPS}$)
  * `frames_over_16_7ms` ($<60\text{ FPS}$ miss)
  * `frames_over_33_3ms` ($<30\text{ FPS}$ severe freeze)
  * `excess_stutter_ms`: $\sum \max(0, \Delta t_i - 16.67\text{ ms})$
* **Engine & Runtime Sub-Frame Monitors (`Godot.Performance` + .NET GC):**
  * `process_ms_mean` / `process_ms_max` (`Performance.Monitor.TimeProcess`)
  * `physics_ms_mean` / `physics_ms_max` (`Performance.Monitor.TimePhysicsProcess`)
  * `draw_calls_mean`, `primitives_mean`, `objects_mean`
  * `alloc_kb_per_frame`, `gc_gen0`, `gc_gen1`, `gc_gen2` (`GC.GetTotalAllocatedBytes` & `GC.CollectionCount`)

### 2. CPU Crunch Metrics (Active & Idle)
Measured via `System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime` (user + kernel CPU time) over the measurement window:

* `cpu_percent`: $\frac{\Delta \text{CPU}_{\text{user+sys}}}{\Delta \text{Wall}} \times 100\%$ (where $100\% = 1$ full CPU core).
* `cpu_ms_per_frame`: $\frac{\Delta \text{CPU}_{\text{user+sys}}}{\text{Frames}}$.
* **Operating regimes:**
  1. **Active Demo Playback:** Replaying `forge_2026-11-01_21.35.57.fbs` from start to finish.
  2. **Post-Demo In-Game Idle:** Remaining in the match after demo playback finishes with zero player input (`--ablation-idle-after=<seconds>`).
  3. **Main Menu Idle:** Sitting at the startup `MainMenu` without starting a match (`--ablation-idle-menu --ablation-duration=<seconds>`).

---

## Stage 2: Component Switch (`scripts/tracing/AblationProbe.cs`)

[`AblationProbe`](../../scripts/tracing/AblationProbe.cs) is a lightweight diagnostic node attached by [`Main`](../../scripts/Main.cs) only when `--ablation*` CLI arguments or `ABLATION_*` environment variables are present. During normal play and unit test runs it is a no-op.

When a component is switched off, `AblationProbe` sets both **visibility** (`Visible = false`, `SubViewport.UpdateMode.Disabled`) and **processing** (`ProcessMode = ProcessModeEnum.Disabled`, `SetProcess(false)`, `SetPhysicsProcess(false)`, input processing off, plus stopping any `AnimationPlayer`, `GpuParticles3D`, or `AudioStreamPlayer*`) to zero on the component root and all descendants.

### Toggleable Components

| Component ID | Scene Node(s) | Code Path / Subsystem Isolated |
| :--- | :--- | :--- |
| `menu` | `/root/Main/MainMenu` | Main menu `CanvasLayer` and its `StageSubViewport` 3D character preview. |
| `hud` | `PlayerScene/HudCanvas` (`HudController`) | Crosshair (and per-frame hitscan target check), status bar, ammo HUD, game clock, score labels. |
| `playermodel` | `MapWorld/PlayerModel` | 3rd-person MD5 skeletal mesh, `Skeleton3D`, `BoneAttachment3D`, and `AnimationPlayer`. |
| `hudgun` | `Player/Camera3D/Hudgun` | 1st-person MD5 weapon/hands mesh, `Skeleton3D`, `BoneAttachment3D`, and `AnimationPlayer`. |
| `weaponeffects` | `MapWorld/WeaponEffects` | `ImmediateMesh` tracers, 256-node bullet/scorch decal pool, explosion spheres, rocket visuals. |
| `particles` | `MapWorld/Particles` | Map `ET_PARTICLES` emitters (`GpuParticles3D`). |
| `lights` | `MapWorld/Lights` | Map `OmniLight3D` and `SpotLight3D` entities. |
| `envmaps` | `MapWorld/EnvMaps` | Map `ReflectionProbe` (`ET_ENVMAP`) nodes. |
| `mapmodels` | `MapWorld/MapModels` | Static glTF map models placed in the map. |
| `items` | `MapWorld/Items` (`ItemManager`) | Item pickup models and per-frame bobbing/spinning (`Item.UpdateVisual`). |
| `flags` | `MapWorld/Flags` | CTF flag models and per-frame `FlagNode.UpdateVisual` updates. |
| `triggers` | `MapWorld/Triggers`, `ModeEntities`, `Monsters`, `PlayerStarts` | Teleporter/jumppad models and per-frame `Teleport.UpdateVisual` updates. |
| `sounds` | `MapWorld/Sounds` + `Player/AudioPlayer*` | Ambient map `AudioStreamPlayer3D` nodes and player sound players. |
| `worldmesh` | `MapWorld/WorldMesh` + `Material_*` | `WorldChunkRenderer` direct `RenderingServer` octree chunk instances, water/lava/glass meshes, and `RadianceVolume.Process`. |
| `worldenv` | `PlayerScene/WorldEnvironment` + `Sun` | Cubemap skybox, environment tonemap/ambient settings, and directional sun. |
| `playerview` | `Player/PlayerView` + `Player._Process` | Per-frame camera, body, and hudgun presentation sync in `_Process`. |
| `worldprocess` | `MapWorld._Process` | `MapWorld._Process` loop driving teleporter, flag, and item visual updates. |
| `worldphysics` | `MapWorld._PhysicsProcess` | Fixed 5 ms `WorldSimulation.Step` octree physics loop (for idle rendering isolation). |
| `all` | All of the above (except `worldphysics`) | Minimal empty-viewport baseline while still advancing the demo clock. |

### CLI & Environment Variable Usage

```sh
GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"

# 1. Baseline demo measurement (all components ON, auto-quit when demo finishes):
"$GODOT" --path . --demo=forge_2026-11-01_21.35.57.fbs \
  --ablation-enable --ablation-quit --ablation-out=/tmp/ablation_baseline.json

# 2. Disable specific components (comma-separated):
"$GODOT" --path . --demo=forge_2026-11-01_21.35.57.fbs \
  --ablation-disable=hud,playermodel,hudgun --ablation-quit

# 3. Keep ONLY specific components enabled (disable everything else):
"$GODOT" --path . --demo=forge_2026-11-01_21.35.57.fbs \
  --ablation-only=worldmesh,worldenv --ablation-quit

# 4. Measure post-demo idle CPU crunch for 5 seconds after playback finishes:
"$GODOT" --path . --demo=forge_2026-11-01_21.35.57.fbs \
  --ablation-enable --ablation-idle-after=5 --ablation-quit

# 5. Measure MainMenu idle CPU crunch for 5 seconds:
"$GODOT" --path . --ablation-idle-menu --ablation-duration=5 --ablation-quit
```

Environment variables (`ABLATION_ENABLE=1`, `ABLATION_DISABLE=...`, `ABLATION_ONLY=...`, `ABLATION_QUIT=1`, `ABLATION_OUT=...`, `ABLATION_DURATION=...`, `ABLATION_IDLE_AFTER=...`, `ABLATION_IDLE_MENU=1`, `ABLATION_LABEL=...`) are also accepted.

---

## Stage 3: Combinations Run Plan

1. **Full Baseline (`none` disabled):** Establish reference frame time percentiles, stutter count, and active CPU %.
2. **Leave-One-Out (Single-Component Ablation):** Disable each component individually (`-hud`, `-playermodel`, `-hudgun`, `-weaponeffects`, `-particles`, `-lights`, `-envmaps`, `-mapmodels`, `-items`, `-flags`, `-triggers`, `-sounds`, `-worldmesh`, `-worldenv`, `-menu`) to measure each subsystem's marginal contribution to frame spikes and CPU load.
3. **Grouped / Cumulative Combinations:**
   * `all` disabled (bare engine loop + physics playback)
   * Rendering groups (`-lights,envmaps,particles`, `-playermodel,hudgun,mapmodels,items,flags,triggers`, `-worldmesh`)
   * Per-frame `_Process` churn (`-hud,worldprocess,playerview,weaponeffects`)
4. **Idle CPU Crunch Sweep:**
   * Menu idle (`MainMenu` open vs `menu` disabled)
   * Post-demo match idle (baseline vs `all` disabled vs `worldphysics` disabled)

---

## Stage 4: Analysis

Results from Stage 3 are aggregated to rank:
1. Which code paths cause **frame time spikes / stutters** (P99 / Max / >16.7 ms hitches).
2. Which code paths cause **sustained CPU crunch** during active play and when idle.
