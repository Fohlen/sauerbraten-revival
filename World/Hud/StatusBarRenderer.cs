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

    /// <summary>
    /// Formats the remaining match time as "mins:secs" (e.g. "10:00", "0:59")
    /// matching engine <c>fps.cpp:955-957: drawgameclock</c>:
    /// <c>int secs = max(maplimit-lastmillis + 999, 0)/1000, mins = secs/60; secs %= 60;</c>
    /// </summary>
    public static (int Minutes, int Seconds, string Formatted) FormatGameClock(int mapLimitMillis, int lastMillis)
    {
        int remainingSecs = Math.Max(mapLimitMillis - lastMillis + 999, 0) / 1000;
        int mins = remainingSecs / 60;
        int secs = remainingSecs % 60;
        return (mins, secs, $"{mins}:{secs:D2}");
    }

    /// <summary>
    /// Computes the color for the game clock matching engine <c>fps.cpp:946-948, 968</c>:
    /// <c>mins &lt; 1 ? gameclocklowcolour (0xFFA050) : gameclockcolour (0xFFFFFF)</c>.
    /// </summary>
    public static Color GetGameClockColor(int minutes, uint regularRgb = 0xFFFFFF, uint lowRgb = 0xFFA050, int alpha = 255)
    {
        uint rgb = minutes < 1 ? lowRgb : regularRgb;
        float r = ((rgb >> 16) & 0xFF) / 255.0f;
        float g = ((rgb >> 8) & 0xFF) / 255.0f;
        float b = (rgb & 0xFF) / 255.0f;
        float a = Mathf.Clamp(alpha, 0, 255) / 255.0f;
        return new Color(r, g, b, a);
    }

    /// <summary>
    /// Measures the bounds of a clock string formatted as "%d:%02d" using the default Sauerbraten font metrics.
    /// In Sauerbraten's default font:
    /// digits '0'-'9' have advance 30, ':' has advance 16, height FONTH is 64.
    /// </summary>
    public static Vector2 MeasureGameClockText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new Vector2(0f, DefaultFontHeight);

        float width = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ':')
                width += 16f;
            else if (c >= '0' && c <= '9')
                width += DefaultFontCharWidth;
            else
                width += DefaultFontCharWidth;
        }

        return new Vector2(width, DefaultFontHeight);
    }

    /// <summary>
    /// Computes the draw offset and dimensions for the game clock matching engine <c>fps.cpp:959-971</c>:
    /// <c>vec2 offset = vec2(gameclockx, gameclocky).mul(vec2(w, h).div(gameclockscale));</c>
    /// <c>if(gameclockalign == 1) offset.x -= tw; else if(gameclockalign == 0) offset.x -= tw/2.0f; offset.y -= th/2.0f;</c>
    /// Returns unscaled text bounds and position in scaled matrix space, as well as final screen position and size.
    /// </summary>
    public static (Vector2 ScreenPos, Vector2 ScreenSize) LayoutGameClock(
        float screenW,
        float screenH,
        string text,
        float clockX = 0.50f,
        float clockY = 0.03f,
        float clockScale = 1.0f,
        int clockAlign = 0)
    {
        Vector2 twth = MeasureGameClockText(text);
        float effectiveScale = Math.Max(0.001f, clockScale);

        // In original engine (fps.cpp:959-966):
        // vec2 offset = vec2(gameclockx, gameclocky).mul(vec2(w, h).div(gameclockscale));
        // if(gameclockalign == 1) offset.x -= tw;
        // else if(gameclockalign == 0) offset.x -= tw/2.0f;
        // offset.y -= th/2.0f;
        // hudmatrix.scale(gameclockscale, gameclockscale, 1);
        // draw_text(buf, int(offset.x), int(offset.y), ...);
        //
        // In screen coordinates (multiplying offset by gameclockscale):
        // screenX = gameclockx * w + (align_offset) * gameclockscale;
        // screenY = gameclocky * h - (th / 2.0f) * gameclockscale;
        float ox = (clockX * screenW) / effectiveScale;
        if (clockAlign == 1)
            ox -= twth.X;
        else if (clockAlign == 0)
            ox -= twth.X / 2.0f;

        float oy = (clockY * screenH) / effectiveScale - twth.Y / 2.0f;

        Vector2 screenPos = new Vector2(ox * effectiveScale, oy * effectiveScale);
        Vector2 screenSize = new Vector2(twth.X * effectiveScale, twth.Y * effectiveScale);
        return (screenPos, screenSize);
    }

    /// <summary>
    /// Computes player status color matching engine <c>scoreboard.cpp:150: statuscolor</c>.
    /// Privilege colors: Admin = 0xFF8000, Auth = 0xC040C0, Master = 0x40FF80.
    /// If dead, privilege color is dimmed <c>(color >> 1) &amp; 0x7F7F7F</c>, or default dead color is 0x606060.
    /// </summary>
    public static Color GetStatusColor(int privilege, bool isDead, uint baseColorRgb = 0xFFFFFF)
    {
        uint color = baseColorRgb;
        if (privilege > 0)
        {
            color = privilege >= 3 ? 0xFF8000u : (privilege >= 2 ? 0xC040C0u : 0x40FF80u);
            if (isDead)
            {
                color = (color >> 1) & 0x7F7F7Fu;
            }
        }
        else if (isDead)
        {
            color = 0x606060u;
        }

        float r = ((color >> 16) & 0xFF) / 255.0f;
        float g = ((color >> 8) & 0xFF) / 255.0f;
        float b = (color & 0xFF) / 255.0f;
        return new Color(r, g, b);
    }

    /// <summary>
    /// Computes layout positions and sizes for spectator overlay in 1800p space, then scales to screen pixels.
    /// Matching engine <c>fps.cpp:1064-1077</c>:
    /// <c>
    /// text_bounds("  ", pw, ph);
    /// text_bounds("SPECTATOR", tw, th);
    /// th = max(th, ph);
    /// fpsent *f = followingplayer();
    /// text_bounds(f ? colorname(f) : " ", fw, fh);
    /// fh = max(fh, ph);
    /// draw_text("SPECTATOR", w*1800/h - tw - pw, 1650 - th - fh);
    /// if(f) draw_text(colorname(f), w*1800/h - fw - pw, 1650 - fh, ...);
    /// </c>
    /// </summary>
    public static (Vector2 SpecPos, Vector2 SpecSize, Vector2 FollowPos, Vector2 FollowSize) LayoutSpectatorOverlay(
        float screenW,
        float screenH,
        string targetPlayerName,
        float refHeight = ReferenceHeight)
    {
        float scale = screenH / refHeight;
        float refW = screenW / scale;

        // pw, ph = text_bounds("  ") -> 2 spaces = 2 * 30 = 60, height = 64
        float pw = 2.0f * DefaultFontCharWidth;
        float ph = DefaultFontHeight;

        float tw = "SPECTATOR".Length * DefaultFontCharWidth;
        float th = Math.Max(DefaultFontHeight, ph);

        float fw = string.IsNullOrEmpty(targetPlayerName) ? DefaultFontCharWidth : targetPlayerName.Length * DefaultFontCharWidth;
        float fh = Math.Max(DefaultFontHeight, ph);

        float specX = (refW - tw - pw) * scale;
        float specY = (IconY - th - fh) * scale;

        float followX = (refW - fw - pw) * scale;
        float followY = (IconY - fh) * scale;

        return (
            new Vector2(specX, specY),
            new Vector2(tw * scale, th * scale),
            new Vector2(followX, followY),
            new Vector2(fw * scale, fh * scale)
        );
    }

    /// <summary>
    /// Formats material description for cube selection matching engine <c>material.cpp:170: getmaterialdesc</c>.
    /// Checks MATF_VOLUME|MATF_INDEX, MATF_CLIP, MAT_DEATH, MAT_ALPHA.
    /// </summary>
    public static string GetMaterialDesc(ushort mat, string prefix = "")
    {
        ushort[] masks = new ushort[] { Octa.MATF_VOLUME | Octa.MATF_INDEX, Octa.MATF_CLIP, Octa.MAT_DEATH, Octa.MAT_ALPHA };
        string desc = "";
        for (int i = 0; i < masks.Length; i++)
        {
            ushort masked = (ushort)(mat & masks[i]);
            if (masked != 0)
            {
                string? name = FindMaterialName(masked);
                if (name != null)
                {
                    if (desc.Length > 0)
                        desc += ", ";
                    else
                        desc += prefix;
                    desc += name;
                }
            }
        }
        return desc;
    }

    /// <summary>
    /// Finds material name string matching engine <c>material.cpp:164: findmaterialname</c>.
    /// </summary>
    public static string? FindMaterialName(ushort mat)
    {
        return mat switch
        {
            Octa.MAT_AIR => "air",
            Octa.MAT_WATER => "water",
            Octa.MAT_WATER + 1 => "water2",
            Octa.MAT_WATER + 2 => "water3",
            Octa.MAT_WATER + 3 => "water4",
            Octa.MAT_GLASS => "glass",
            Octa.MAT_GLASS + 1 => "glass2",
            Octa.MAT_GLASS + 2 => "glass3",
            Octa.MAT_GLASS + 3 => "glass4",
            Octa.MAT_LAVA => "lava",
            Octa.MAT_LAVA + 1 => "lava2",
            Octa.MAT_LAVA + 2 => "lava3",
            Octa.MAT_LAVA + 3 => "lava4",
            Octa.MAT_CLIP => "clip",
            Octa.MAT_NOCLIP => "noclip",
            Octa.MAT_GAMECLIP => "gameclip",
            Octa.MAT_DEATH => "death",
            Octa.MAT_ALPHA => "alpha",
            _ => null
        };
    }

    /// <summary>
    /// Formats edit mode selection cube text matching engine <c>rendergl.cpp:2313</c>:
    /// <c>draw_textf("cube %s%d%s", FONTH/2, abovehud, selchildcount&lt;0 ? "1/" : "", abs(selchildcount), showmat &amp;&amp; selchildmat &gt; 0 ? getmaterialdesc(selchildmat, ": ") : "");</c>
    /// </summary>
    public static string FormatEditCube(int selChildCount, ushort selChildMat, bool showMat = true)
    {
        string countPrefix = selChildCount < 0 ? "1/" : "";
        string matDesc = showMat && selChildMat > 0 ? GetMaterialDesc(selChildMat, ": ") : "";
        return $"cube {countPrefix}{Math.Abs(selChildCount)}{matDesc}";
    }

    /// <summary>
    /// Formats edit mode quick stats and status (e.g. grid size, grid power, allfaces).
    /// </summary>
    public static string FormatEditQuickStats(int gridSize, int gridPower, bool allFaces, Vector3I? selPos = null)
    {
        string text = $"grid {gridSize} (power {gridPower})";
        if (allFaces) text += "  allfaces";
        if (selPos.HasValue) text += $"  pos ({selPos.Value.X} {selPos.Value.Y} {selPos.Value.Z})";
        return text;
    }
}


