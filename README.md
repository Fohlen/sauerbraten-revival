# Sauerbraten Revival

Sauerbraten Revival is an unofficial, community-driven project inspired by **Cube2: Sauerbraten**. It is built in **Godot 4 (.NET / C#)**. The project's goal is to take the original engine much further and make a Garry's Mod style sandbox, fully customizable, with the following features:

- Game: The nostalgic Sauerbraten arena FPS feel
- Modes: Suitable for public lobbies and lan-parties with friends - **CTF**, **Rugby**, **Prop Hunt**, **Racing**
- World: a native, **runtime-mutable octree** and editor
- Modding: **everything**, including physics, weapons and game modes can be changed - using [Godot Resources](https://docs.godotengine.org/en/stable/tutorials/scripting/resources.html) and [GDScript](https://docs.godotengine.org/en/stable/tutorials/scripting/gdscript/gdscript_basics.html)
- Re-use of libre assets where possible, classic maps ship as original `.ogz` files and are read at load time
- Deep support for Steam (workshop and lobbies)

The title _"Sauerbraten Revival"_ is a preliminary working title. [Collaboration is encouraged](https://pad.riseup.net/p/sauerbraten-revival-keep).

## Playing the game

In order to try out this new engine you will need to:

1. [Download dotnet](https://dotnet.microsoft.com/en-us/download)
2. [Download Godot .NET](https://godotengine.org)
3. [Clone this repository](https://docs.github.com/en/repositories/creating-and-managing-repositories/cloning-a-repository#cloning-a-repository)
4. [Download](https://drive.google.com/file/d/17UIVVnpPHoTrtI9q5V6h07SRTl0lAnGx/view?usp=share_link) and unpack the asset pack into your new repository — this holds the converted models/textures and the classic `.ogz` maps
5. [Import the project](https://docs.godotengine.org/en/stable/engine_details/development/configuring_an_ide/visual_studio.html#importing-the-project)

Once the project is set up you can run the `player_scene.tscn` to play the game or the `editor_scene.tscn` to start the in-game editor using the [_"Run Current Scene"_](https://docs.godotengine.org/en/stable/getting_started/step_by_step/nodes_and_scenes.html#running-the-scene) button.

See **[AGENTS.md](AGENTS.md)** for detailed instructions you need to work on the project:
build/run instructions, tests, repository layout, the documentation index, the
staging & commit guidelines, and the reference source.
