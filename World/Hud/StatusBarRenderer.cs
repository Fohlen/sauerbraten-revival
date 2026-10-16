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
}
