# dmoreader — Sauerbraten .dmo demo reader & .fbs converter

CLI tool for reading classic Cube 2: Sauerbraten `.dmo` demo files, inspecting
player trajectories, and translating them into revival FlatBuffers `.fbs` demo
packages for deterministic playback in engine.

## Usage

```sh
# View demo information & player stats
tools/dmoreader/dmoreader info /path/to/demo.dmo

# View summary as JSON (for automated scripting & botlab ingestion)
tools/dmoreader/dmoreader info /path/to/demo.dmo --json

# Convert .dmo -> .fbs (for playback in SauerbratenRevival)
tools/dmoreader/dmoreader convert /path/to/demo.dmo -o user://demos/duel7.fbs

# Dump trajectory data (CSV format)
tools/dmoreader/dmoreader dump /path/to/demo.dmo --player 4 --limit 50
```

## Replaying in SauerbratenRevival

Converted `.fbs` demos (or raw `.dmo` demos) can be replayed directly in the game:

```sh
# Headless or interactive demo replay
"$GODOT" --path . --demo /path/to/demo.dmo
# or with converted .fbs
"$GODOT" --path . --demo user://demos/duel7.fbs
```
