# Sauerbraten Revival

A revival/port of **Cube 2: Sauerbraten** built in **Godot 4 (.NET / C#)**. The
goal is to recreate the original's iconic movement and feel first, then the in-game
editor (a defining feature) and the game on top of it.

- MVP modes: **insta CTF**, **effic CTF**, **FFA**, **coop edit**.
- World: a native, **runtime-mutable octree** (live editing + dynamic geometry).
- Legacy content is converted **once, offline**; the game never loads `.ogz` or
  interprets CubeScript.


## Playing with the project

In order to work with this new engine you will need to:

1. [Download Godot .NET](https://godotengine.org)
2. Clone this repository
3. [Download](https://drive.google.com/file/d/17UIVVnpPHoTrtI9q5V6h07SRTl0lAnGx/view?usp=share_link) and unpack a copy of the already converted assets (e.g models) into your new repository
4. [Importing this new project](https://docs.godotengine.org/en/stable/engine_details/development/configuring_an_ide/visual_studio.html#importing-the-project)
5. Project → Project Settings → Filter Settings → Sauerbraten
6. Set a reference path to a copy of Sauerbraten (if you wish to load OGZ maps)


See **[AGENTS.md](AGENTS.md)** for detailed instructions you need to work on the project:
build/run instructions, tests, repository layout, the documentation index, the
staging & commit guidelines, and the reference source.
