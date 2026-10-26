# Map lists

The engine's curated classic-map pools, imported verbatim from **Sauerbraten SVN
r6889** `data/lists.cfg`. One Godot `MapList` resource per engine `MapList_*`
alias, so the front-end offers the same per-mode map pools as the original.

| File | Engine alias | GUI tab | Maps |
| --- | --- | --- | ---: |
| `dm.tres` | `MapList_DM` | ffa | 187 |
| `capture.tres` | `MapList_CAP` | capture | 117 |
| `ctf.tres` | `MapList_CTF` | ctf | 110 |
| `sp.tres` | `MapList_SP` | single player | 11 |
| `concept.tres` | `MapList_Concept` | concept | 10 |
| `rpg.tres` | `MapList_RPG` | rpg | 2 |

`MapList_ALL` is the engine's `(concat …)` of the six lists, so it is not stored
separately.

Names are package-relative without extension; `base/<name>.ogz` is resolved
through `ContentRoots`, and `<name>.jpg` supplies the menu thumbnail.

## Mode → pool

`MapListCatalog.ForMode` mirrors the engine's maps GUI (`data/menus/_utility.cfg`,
`ML__SetupMode` → `ML_ModeMaps`):

- CTF, protect, collect → **ctf**
- capture, hold (all variants) → **capture** (hold is an `M_CTF` mode in the
  engine, but its maps GUI still uses the capture list)
- free-for-all, teamplay, coop edit → **dm**

The engine offers all four pools as tabs for the DM/TDM/edit family; our
single left/right picker starts from the deathmatch pool.

These are normal `Resource`s, editable in the inspector and replaceable by a mod
pack that mounts its own list into `res://resources/maplists`.
