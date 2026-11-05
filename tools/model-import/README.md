# Model Import & Weapon Orientations

Toolchain for converting original Cube 2: Sauerbraten assets (MD2, MD3, MD5, and IQM) into GLB (glTF 2.0 binary) scenes and catalog manifests for Sauerbraten Revival.

---

## 1. Third-Person View Weapons (`vwep/*`)

Third-person view weapons are rigid models attached to the player character skeleton's `tag_weapon` bone (`Weapon`) in [`PlayerModel.cs`](../../World/Models/PlayerModel.cs).

### Transform Table (Rotation & Position Offset)

| Weapon | ID | Engine Path | Model Scale | Base Rotation `(X, Y, Z)` | Base Offset `(X, Y, Z)` | Orientation & Joint Root Analysis |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Fist (Chainsaw)** | 0 | `vwep/fist` | 700 / 400 | `(90, 0, 0)` | `(0, 0, 0)` | Universal $+90^\circ$ X cancels duplicate `<MD5_Root>` conversion. |
| **Shotgun** | 1 | `vwep/shotg` | 500 / 400 | `(90, 0, 0)` | `(0, 0, 0)` | Universal $+90^\circ$ X cancels duplicate `<MD5_Root>` conversion. |
| **Chaingun** | 2 | `vwep/chaing` | 700 / 400 | `(90, 0, 0)` | `(0, 0, 0)` | Universal $+90^\circ$ X cancels duplicate `<MD5_Root>` conversion. |
| **Rocket Launcher** | 3 | `vwep/rocket` | 600 / 400 | `(90, 0, 0)` | `(0, 0, 0)` | Universal $+90^\circ$ X cancels duplicate `<MD5_Root>` conversion. Tube runs along $-Y$, which `hold 4` (`rl_idle`) points forward. |
| **Sniper Rifle** | 4 | `vwep/rifle` | 600 / 400 | `(90, 0, 0)` | `(0, 0, 0)` | Universal $+90^\circ$ X cancels duplicate `<MD5_Root>` conversion. |
| **Grenade Launcher** | 5 | `vwep/gl` | 700 / 400 | `(90, 0, 0)` | `(0, 0, 0)` | Universal $+90^\circ$ X cancels duplicate `<MD5_Root>` conversion. |
| **Pistol (Revolver)** | 6 | `vwep/pistol` | 600 / 400 | `(90, 0, 0)` | `(0, 0, 0)` | Universal $+90^\circ$ X cancels duplicate `<MD5_Root>` conversion. |

### Why Sauerbraten r6889 Does Not Store These Transforms

1. **Sauerbraten Render-Time Bypass**:
   In [`src/engine/skelmodel.h:1441-1456`](/Users/fohlen/Downloads/sauerbraten-code-r6889/src/engine/skelmodel.h#L1441-L1456), Sauerbraten checks `if(!skel->numframes)`. Because `vwep` models have no animation clips, the engine **never evaluates their skeletal hierarchy or joint matrices at render time**.
2. **Pre-evaluated VBO Coordinates**:
   At map load, [`src/engine/md5.h:55-73`](/Users/fohlen/Downloads/sauerbraten-code-r6889/src/engine/md5.h#L55-L73) (`buildverts`) bakes the vertices into a static VBO using the base joints. When drawn in [`src/engine/animmodel.h:924`](/Users/fohlen/Downloads/sauerbraten-code-r6889/src/engine/animmodel.h#L924), Sauerbraten only pushes `link.matrix` (the player's hand bone).
3. **Assimp glTF Export & Root Matrices**:
   When Assimp converts `.md5mesh` to `.glb` ([`import_models.py`](import_models.py)), it generates an `<MD5_Root>` node applying a Z-up to Y-up conversion matrix ($-90^\circ$ around X). Because a view weapon is parented inside the player body's own hierarchy (which already lives inside its own root conversion), the vwep's root matrix is applied a second time. A universal $+90^\circ$ X rotation on all view weapons in [`PlayerViewWeapon.GetBaseRotationDegrees()`](../../World/Models/PlayerViewWeapon.cs) precisely cancels this duplicate transform.
4. **Runtime Resolution**:
   [`PlayerViewWeapon.GetBaseRotationDegrees()`](../../World/Models/PlayerViewWeapon.cs) applies `(90, 0, 0)` across all view weapons. Fine adjustments can also be made live via [`PlayerModel.ViewWeaponRotationDegrees`](../../World/Models/PlayerModel.cs) and [`PlayerModel.ViewWeaponOffset`](../../World/Models/PlayerModel.cs).

---

## 2. First-Person Hudguns (`hudguns/*`)

First-person hudguns are dual-part models (`part0` hands + `part1` gun) configured via `packages/models/hudguns/*/md5.cfg`. They are pre-built and managed by [`HudgunModel.cs`](../../World/Models/HudgunModel.cs).

| Weapon | Dir | Scale | Translation `(X, Y, Z)` | Attachment Tag | Hand Animation | Gun Animation |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Fist** | `hudguns/fist` | 300 | `(0.3, -0.3, -1.7)` | `tag_weapon` | `hands_saw_idle.md5anim` | `chainsaw_idle.md5anim` |
| **Shotgun** | `hudguns/shotg` | 300 | `(1.5, -1.85, -0.3)` | `tag_weapon` | `hands_shotgun_idle.md5anim` | `shotgun_idle.md5anim` |
| **Chaingun** | `hudguns/chaing` | 300 | `(-0.5, -1.8, -2.25)` | `tag_bicep` | `hands_mg_idle.md5anim` | `chaing_idle.md5anim` |
| **Rocket Launcher** | `hudguns/rocket` | 300 | `(1.8, -2.725, 0.15)` | `tag_weapon` | `hands_rl_idle.md5anim` | `rl_idle.md5anim` |
| **Rifle** | `hudguns/rifle` | 300 | `(1.0, -2.0, -0.3)` | `tag_bicep` | `hands_rifle_idle.md5anim` | `rifle_idle.md5anim` |
| **Grenade Launcher** | `hudguns/gl` | 300 | `(-0.6, -2.65, -1.1)` | `tag_weapon` | `hands_gl_idle.md5anim` | `gl_idle.md5anim` |
| **Pistol** | `hudguns/pistol` | 300 | `(1.2, -1.2, -1.0)` | `tag_weapon` | `hands_revolver_idle.md5anim` | `revolver_idle.md5anim` |

---

## 3. Monster View Weapons

Monster weapons are defined in `src/fpsgame/monster.cpp` (`monstertypes[].vwepname`). Unlike player weapons, monster view weapons use static meshes (MD2/MD3 format):

| Monster | View Weapon Model | Type | Notes |
| :--- | :--- | :--- | :--- |
| **Ogro** | `ogro/vwep` | MD2/MD3 static | Rigid mesh attached to `tag_weapon` |
| **Slith** | `monster/slith/vwep` | MD2/MD3 static | Rigid mesh attached to `tag_weapon` |
| **Bauul** | `monster/bauul/vwep` | MD2/MD3 static | Rigid mesh attached to `tag_weapon` |
| **Goblin** | `monster/goblin/vwep` | MD2/MD3 static | Rigid mesh attached to `tag_weapon` |
| **Knight** | `monster/knight/vwep` | MD2/MD3 static | Rigid mesh attached to `tag_weapon` |
| **Rat** | `monster/rat/vwep` | MD2/MD3 static | Rigid mesh attached to `tag_weapon` |
