using System;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Hud;

/// <summary>
/// Status bar indicator icons (port of engine <c>HICON_*</c> in <c>src/fpsgame/game.h:298-326</c>).
/// Subdivided on a 4x4 grid across <c>packages/hud/items.png</c>.
/// </summary>
public enum HudIcon
{
    BlueArmour = 0,
    GreenArmour = 1,
    YellowArmour = 2,
    Health = 3,
    Fist = 4,
    Sg = 5,
    Cg = 6,
    Rl = 7,
    Rifle = 8,
    Gl = 9,
    Pistol = 10,
    Quad = 11,
    RedFlag = 12,
    BlueFlag = 13,
    NeutralFlag = 14,
    Token = 15
}

/// <summary>
/// Geometry and color logic for the bottom status bar icons and counters ported from
/// <c>src/fpsgame/fps.cpp:drawhudicons</c> and <c>src/fpsgame/game.h</c>.
/// </summary>
public static class StatusBarRenderer
{
    // Sauerbraten 1800p reference coordinates (fps.cpp:1060 / game.h:320-325)
    public const float ReferenceHeight = 1800.0f;
    public const float IconX = 20.0f;
    public const float IconY = 1650.0f;
    public const float IconSize = 120.0f;
    public const float IconStep = 490.0f;
    public const float IconSpace = 40.0f;
    public const float TextY = 1644.0f;

    /// <summary>
    /// Calculates the color for health text matching <c>fps.cpp:910</c>:
    /// Grey if dead (0x808080), red if &lt;= 25 (0xFF0000), orange if &lt;= 50 (0xFF8000),
    /// white if &lt;= 100 (0xFFFFFF), blue/cyan if &gt; 100 (0x40C0FF, megahealth boost).
    /// </summary>
    public static Color GetHealthColor(int health, bool isDead, bool healthColors = true, bool isInsta = false)
    {
        if (!healthColors || isInsta)
            return Colors.White;

        if (isDead || health <= 0)
            return new Color(0.5f, 0.5f, 0.5f); // 0x808080

        if (health <= 25)
            return new Color(1f, 0f, 0f); // 0xFF0000

        if (health <= 50)
            return new Color(1f, 0.5f, 0f); // 0xFF8000

        if (health <= 100)
            return Colors.White; // 0xFFFFFF

        return new Color(0.25f, 0.75f, 1f); // 0x40C0FF
    }

    /// <summary>
    /// Computes the UV rect on the 4x4 atlas <c>items.png</c> for a given <see cref="HudIcon"/>.
    /// Matches <c>fps.cpp:816</c>: <c>tsz = 0.25f, tx = tsz*(icon%4), ty = tsz*(icon/4)</c>.
    /// </summary>
    public static Rect2 GetIconUv(HudIcon icon)
    {
        int index = (int)icon;
        float tsz = 0.25f;
        float tx = tsz * (index % 4);
        float ty = tsz * (index / 4);
        return new Rect2(tx, ty, tsz, tsz);
    }

    /// <summary>
    /// Determines which armor icon to show based on <see cref="ArmourType"/>.
    /// </summary>
    public static HudIcon GetArmorIcon(ArmourType armourType) => armourType switch
    {
        ArmourType.Green => HudIcon.GreenArmour,
        ArmourType.Yellow => HudIcon.YellowArmour,
        _ => HudIcon.BlueArmour
    };

    /// <summary>
    /// Determines which weapon icon to show based on <see cref="Gun"/>.
    /// </summary>
    public static HudIcon GetGunIcon(Gun gun) => gun switch
    {
        Gun.Fist => HudIcon.Fist,
        Gun.Shotgun => HudIcon.Sg,
        Gun.Chaingun => HudIcon.Cg,
        Gun.RocketLauncher => HudIcon.Rl,
        Gun.Rifle => HudIcon.Rifle,
        Gun.GrenadeLauncher => HudIcon.Gl,
        Gun.Pistol => HudIcon.Pistol,
        _ => HudIcon.Fist
    };

    /// <summary>
    /// Determines which flag icon to show based on game mode and team.
    /// Port of <c>ctf.h:555</c>:
    /// <c>drawicon(m_hold ? HICON_NEUTRAL_FLAG : (f.team==ctfteamflag(player1->team) ? HICON_BLUE_FLAG : HICON_RED_FLAG), x, HICON_Y);</c>
    /// </summary>
    public static HudIcon GetFlagIcon(bool isHold, int flagTeam, int playerTeam)
    {
        if (isHold)
            return HudIcon.NeutralFlag;

        // In Sauerbraten CTF: team == ctfteamflag(player1->team) ? HICON_BLUE_FLAG : HICON_RED_FLAG
        return flagTeam == playerTeam ? HudIcon.BlueFlag : HudIcon.RedFlag;
    }

    /// <summary>
    /// Determines whether a weapon should be visible in the ammo bar strip.
    /// Port of <c>fps.cpp:1019-1026: ammobargunvisible</c>.
    /// </summary>
    public static bool IsAmmoBarGunVisible(int ammo, int selectedGun, int gun, bool hideEmpty, bool isInsta, bool isEfficiency)
    {
        if (ammo > 0 || selectedGun == gun) return true;
        if (hideEmpty) return false;
        if (isEfficiency) return gun != (int)Gun.Pistol;
        if (isInsta) return gun == (int)Gun.Rifle;
        return true;
    }

    /// <summary>
    /// Computes the color for an ammo bar counter based on ammo amount and item stat add value.
    /// Port of <c>fps.cpp:1012-1013</c>:
    /// <c>float ammoratio = (float)p->ammo[gun] / itemstats[gun-GUN_SG].add;</c>
    /// <c>color = p->ammo[gun] == 0 || ammoratio >= 1.0f ? 0xFFFFFF : (ammoratio >= 0.5f ? 0xFFC040 : 0xFF0000);</c>
    /// </summary>
    public static Color GetAmmoBarColor(int ammo, int addStat)
    {
        if (ammo <= 0) return Colors.White;
        float ammoratio = (float)ammo / addStat;
        if (ammoratio >= 1.0f) return Colors.White;
        if (ammoratio >= 0.5f) return new Color(1f, 0.75294f, 0.25098f); // 0xFFC040
        return new Color(1f, 0f, 0f); // 0xFF0000
    }

    /// <summary>
    /// Standard font width/height metrics for the default Sauerbraten font.
    /// Used for bounds calculation in ammobar layout (text_bounds("000", fw, fh)).
    /// </summary>
    public const float DefaultFontCharWidth = 30.0f; // character '0' advance is 30 in default.cfg
    public const float DefaultFontHeight = 64.0f;    // FONTH == 64 in default font

    public record struct AmmoBarItem(Gun Gun, int Ammo, Vector2 Center, Color Color, float Alpha);

    /// <summary>
    /// Calculates layout positions for ammo bar counters matching engine <c>fps.cpp:1028-1055</c>.
    /// </summary>
    public static System.Collections.Generic.List<AmmoBarItem> LayoutAmmoBar(
        int[] playerAmmo,
        int selectedGun,
        float screenW,
        float screenH,
        bool isInsta,
        bool isEfficiency,
        int align = 0,
        bool horizontal = false,
        bool flip = false,
        bool hideEmpty = true,
        float sep = 20f,
        float countSep = 20f,
        float countScale = 1.5f,
        float barX = 0.025f,
        float barY = 0.5f,
        float barScale = 0.5f)
    {
        var items = new System.Collections.Generic.List<AmmoBarItem>();
        if (isInsta) return items;

        int numPlayerGuns = (int)Gun.Pistol - (int)Gun.Shotgun + 1;
        int numVisibleGuns = 0;
        for (int i = 0; i < numPlayerGuns; i++)
        {
            int gun = (int)Gun.Shotgun + i;
            int ammo = playerAmmo != null && gun < playerAmmo.Length ? playerAmmo[gun] : 0;
            if (IsAmmoBarGunVisible(ammo, selectedGun, gun, hideEmpty, isInsta, isEfficiency))
                numVisibleGuns++;
        }

        if (numVisibleGuns == 0) return items;

        Vector2 origin = new Vector2(barX, barY) * (new Vector2(screenW, screenH) / barScale);
        Vector2 offsetDir = horizontal ? new Vector2(1, 0) : new Vector2(0, 1);
        float stepSize = IconSize + sep;
        float initialOffset = (align - 1) * (numVisibleGuns - 1) * stepSize / 2f;

        int numSkippedGuns = 0;
        for (int i = 0; i < numPlayerGuns; i++)
        {
            int gunId = (int)Gun.Shotgun + i;
            Gun gun = (Gun)gunId;
            int ammo = playerAmmo != null && gunId < playerAmmo.Length ? playerAmmo[gunId] : 0;
            if (IsAmmoBarGunVisible(ammo, selectedGun, gunId, hideEmpty, isInsta, isEfficiency))
            {
                float offset = initialOffset + (i - numSkippedGuns) * stepSize;
                Vector2 center = offsetDir * offset + origin;
                ItemStat stat = ItemStats.ForGun(gun);
                Color color = GetAmmoBarColor(ammo, stat.Add);
                float alpha = ammo > 0 ? 1.0f : 0.5f; // alpha = p->ammo[gun] ? 0xFF : 0x7F
                items.Add(new AmmoBarItem(gun, ammo, center, color, alpha));
            }
            else
            {
                numSkippedGuns++;
            }
        }

        return items;
    }

    public record struct AmmoHudItem(Gun Gun, Vector2 Position, float Size);

    /// <summary>
    /// Computes the quick ammo selector / cycling HUD icon positions matching engine <c>fps.cpp:860-899: drawammohud</c>.
    /// The coordinates returned are in the unscaled 1800p reference space.
    /// </summary>
    public static System.Collections.Generic.List<AmmoHudItem> LayoutAmmoHud(
        int[] playerAmmo,
        int selectedGun,
        int[]? upConfig = null,
        int[]? downConfig = null,
        int[]? cycleConfig = null)
    {
        var items = new System.Collections.Generic.List<AmmoHudItem>();
        float x = IconX + 2 * IconStep;
        float y = IconY;
        float sz = IconSize;

        int[] up = upConfig ?? new int[] { (int)Gun.Chaingun, (int)Gun.RocketLauncher, (int)Gun.GrenadeLauncher };
        int[] down = downConfig ?? new int[] { (int)Gun.Rifle, (int)Gun.Shotgun, (int)Gun.Pistol };
        int[] cycle = cycleConfig ?? new int[] { -1, -1, -1, -1, -1, -1, -1 };

        // Up stack: float xup = (x+sz)*3.2f, yup = y*3.2f + 0.1f*sz;
        // In unscaled coordinates (dividing by 3.2f):
        float xup = x + sz;
        float yup = y + 0.1f * sz / 3.2f;
        for (int i = 0; i < 3 && i < up.Length; i++)
        {
            int gun = up[i];
            if (gun < (int)Gun.Fist || gun > (int)Gun.Pistol || gun == selectedGun) continue;
            int ammo = playerAmmo != null && gun < playerAmmo.Length ? playerAmmo[gun] : 0;
            if (ammo <= 0) continue;
            items.Add(new AmmoHudItem((Gun)gun, new Vector2(xup, yup), sz / 3.2f));
            yup += sz / 3.2f;
        }

        // Down stack: float xdown = x*3.2f - sz, ydown = (y+sz)*3.2f - 0.1f*sz;
        // In unscaled coordinates:
        float xdown = x - sz / 3.2f;
        float ydown = (y + sz) - 0.1f * sz / 3.2f;
        for (int i = 0; i < 3 && i < down.Length; i++)
        {
            int gun = down[3 - i - 1];
            if (gun < (int)Gun.Fist || gun > (int)Gun.Pistol || gun == selectedGun) continue;
            int ammo = playerAmmo != null && gun < playerAmmo.Length ? playerAmmo[gun] : 0;
            if (ammo <= 0) continue;
            ydown -= sz / 3.2f;
            items.Add(new AmmoHudItem((Gun)gun, new Vector2(xdown, ydown), sz / 3.2f));
        }

        // Cycle horizontal row above weapon icon
        int offset = 0;
        int num = 0;
        for (int i = 0; i < 7 && i < cycle.Length; i++)
        {
            int gun = cycle[i];
            if (gun < (int)Gun.Fist || gun > (int)Gun.Pistol) continue;
            if (gun == selectedGun)
            {
                offset = i + 1;
            }
            else
            {
                int ammo = playerAmmo != null && gun < playerAmmo.Length ? playerAmmo[gun] : 0;
                if (ammo > 0) num++;
            }
        }

        float xcycle = (x + sz / 2f) + 0.5f * num * (sz / 3.2f);
        float ycycle = y - sz / 3.2f;
        for (int i = 0; i < 7 && i < cycle.Length; i++)
        {
            int gun = cycle[(i + offset) % 7];
            if (gun < (int)Gun.Fist || gun > (int)Gun.Pistol || gun == selectedGun) continue;
            int ammo = playerAmmo != null && gun < playerAmmo.Length ? playerAmmo[gun] : 0;
            if (ammo <= 0) continue;
            xcycle -= sz / 3.2f;
            items.Add(new AmmoHudItem((Gun)gun, new Vector2(xcycle, ycycle), sz / 3.2f));
        }

        return items;
    }
}

