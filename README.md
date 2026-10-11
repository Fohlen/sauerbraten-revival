# Sauerbraten Revival

A revival/port of **Cube 2: Sauerbraten** built in **Godot 4 (.NET / C#)**. The
goal is to recreate the original's iconic movement and feel first, then the in-game
editor (a defining feature) and the game on top of it.

- MVP modes: **insta CTF**, **effic CTF**, **FFA**, **coop edit**.
- World: a native, **runtime-mutable octree** (live editing + dynamic geometry).
- Legacy content is converted **once, offline**; the game never loads `.ogz` or
  interprets CubeScript.

See **[AGENTS.md](AGENTS.md)** for everything you need to work on the project:
build/run instructions, tests, repository layout, the documentation index, the
staging & commit guidelines, and the reference source.
