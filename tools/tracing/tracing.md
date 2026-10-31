# Tracing and Profiling with `dotnet-trace`

This document explains how to profile the running game using `dotnet-trace` and export flame graphs / profiles for analysis in [Speedscope](https://www.speedscope.app/).

---

## 1. Prerequisites

Install the official .NET diagnostic CLI tool globally:

```sh
dotnet tool install --global dotnet-trace
```

*(To update an existing installation: `dotnet tool update --global dotnet-trace`)*

Ensure `~/.dotnet/tools` is in your shell's `PATH`.

---

## 2. Launching the Game with Diagnostics

Godot .NET embeds the .NET runtime. To ensure the runtime exposes the diagnostic IPC socket (`$TMPDIR/dotnet-diagnostic-<PID>-*-socket`), export `DOTNET_EnableDiagnostics=1` before launching Godot:

```sh
DOTNET_EnableDiagnostics=1 /Applications/Godot_mono.app/Contents/MacOS/Godot --path .
```

*(Adjust the path to your Godot binary if it differs from the default `Godot_mono` macOS location).*

---

## 3. Attaching to a Running Session

### Step A: Find the Process ID (PID)

In a separate terminal, list active .NET processes:

```sh
dotnet-trace ps
```

Example output:
```text
  54321  Godot_mono   /Applications/Godot_mono.app/Contents/MacOS/Godot
```

Note the PID (e.g. `54321`).

### Step B: Collect the Trace in Speedscope Format

Run `dotnet-trace collect` targeting the process PID with `--format speedscope`:

```sh
dotnet-trace collect \
  --process-id 54321 \
  --format speedscope \
  --output ./game_trace.speedscope.json
```

Press **`<Enter>`** or **`Ctrl+C`** when you are done capturing the scenario.

#### Useful Flags

* `--duration <hh:mm:ss>`: Automatically stop recording after a fixed duration (e.g., `--duration 00:00:15` for 15 seconds).
* `--profile cpu-sampling`: Uses standard sample profiler (default).
* `--providers`: If you want to sample CPU without CLR events:
  ```sh
  --providers Microsoft-DotNETCore-SampleProfiler:0x0000F00000000000:5
  ```

---

## 4. Tracing Directly From Startup

To profile startup routines (e.g., world parsing, octree generation, initial meshing) directly without attaching manually:

```sh
DOTNET_EnableDiagnostics=1 dotnet-trace collect \
  --format speedscope \
  --output ./startup_trace.speedscope.json \
  -- /Applications/Godot_mono.app/Contents/MacOS/Godot --path .
```

---

## 5. Analyzing in Speedscope

1. Open [https://www.speedscope.app](https://www.speedscope.app) in any browser (it processes traces entirely locally in JavaScript).
2. Drag and drop the generated `*.speedscope.json` file.
3. Use the view modes:
   * **Time Order**: Visualizes call stacks across the timeline (helpful to diagnose periodic hitches, frame time spikes, and GC pauses).
   * **Left Heavy**: Aggregates all time spent across call stacks (helpful to identify primary CPU bottlenecks).
   * **Sandwich**: Explores callers and callees for a specific method.
