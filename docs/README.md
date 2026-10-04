# Sauerbraten Revival — Documentation

A long-term project to revive **Cube 2: Sauerbraten** as a modern, Steam-native
game built in **Godot 4**, while preserving the original game's movement and feel.

## Index

| Document | Contents |
| --- | --- |
| [vision.md](vision.md) | High-level goal, scope, non-goals, tech stack. |
| [architecture.md](architecture.md) | Core technical decisions: dynamic octree, command system, format, rendering, modding. |
| [features.md](features.md) | Feature inventory and roadmap (MVP: insta CTF, effic CTF, FFA, coop edit). |
| [movement-physics.md](movement-physics.md) | Reverse-engineered reference for the original player movement/physics (SVN r6889), with source citations. |
| [movement-verification.md](movement-verification.md) | Plan A: how we prove movement fidelity against the original (A1–A4). |

## Reference source

The original engine source used for this project is a snapshot of
**Sauerbraten SVN r6889** (`/Users/fohlen/Downloads/sauerbraten-code-r6889`).
All file/line citations in these docs point into that tree, primarily
`src/engine/physics.cpp` and `src/shared/ents.h`.
