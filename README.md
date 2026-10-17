# Sauerbraten Revival

A revival/port of **Cube 2: Sauerbraten** built in **Godot 4 (.NET / C#)**. The
goal is to recreate the original's iconic movement and feel first, then the in-game
editor (a defining feature) and the game on top of it.

- MVP modes: **insta CTF**, **effic CTF**, **FFA**, **coop edit**.
- World: a native, **runtime-mutable octree** (live editing + dynamic geometry).
- Modding: allow shareable, changeable data and code mods of game modes, weapons etc
- Classic maps ship as original `.ogz` files and are read at load time; there is
  **no CubeScript** and no offline converter. Maps made in the editor use a new, versioned, native format.

The project's ultimate goal is to make a Garry's Mod style, fully customizable game on top of Godot. Its default will be the Sauerbraten we know and love, including the current assets, with deep support for Steam (lobbies, matchmaking, workshop out of the box).

## Playing with the project

In order to work with this new engine you will need to:

1. [Download Godot .NET](https://godotengine.org)
2. Clone this repository
3. [Download](https://drive.google.com/file/d/17UIVVnpPHoTrtI9q5V6h07SRTl0lAnGx/view?usp=share_link) and unpack the asset pack into your new repository — this holds the converted models/textures and the classic `.ogz` maps
4. [Import the project](https://docs.godotengine.org/en/stable/engine_details/development/configuring_an_ide/visual_studio.html#importing-the-project)

Once the project is set up you can run the `player_scene.tscn` to play the game or the `editor_scene.tscn` to start the in-game editor using the [_"Run Current Scene"_](https://docs.godotengine.org/en/stable/getting_started/step_by_step/nodes_and_scenes.html#running-the-scene) button.

See **[AGENTS.md](AGENTS.md)** for detailed instructions you need to work on the project:
build/run instructions, tests, repository layout, the documentation index, the
staging & commit guidelines, and the reference source.
