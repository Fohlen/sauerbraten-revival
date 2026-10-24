using System;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Runtime.Formats.Ogz;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;
using MapPlayer = SauerbratenRevival.World.Game.Players.MapPlayer;

namespace SauerbratenRevival.World.Hud;

/// <summary>
/// Crosshair selection logic and renderer ported from Cube 2: Sauerbraten
/// (<c>src/fpsgame/fps.cpp:selectcrosshair</c> and <c>src/engine/rendergl.cpp:drawcrosshair</c>).
/// </summary>
public sealed class CrosshairRenderer
{
    public const int CrosshairDefault = 0;
    public const int CrosshairTeammate = 1;
    public const int CrosshairHit = 2;

    public const string DefaultPath = "crosshairs/default.png";
    public const string TeammatePath = "crosshairs/default_teammate.png";
    public const string HitPath = "crosshairs/default_hit.png";

    /// <summary>Original engine <c>crosshairsize</c> (rendergl.cpp VARP, default 30).</summary>
    public int CrosshairSize { get; set; } = 30;

    /// <summary>Original engine <c>crosshairfx</c> (rendergl.cpp VARP, default 1).</summary>
    public bool CrosshairFx { get; set; } = true;

    /// <summary>Original engine <c>crosshaircolors</c> (rendergl.cpp VARP, default 1).</summary>
    public bool CrosshairColors { get; set; } = true;

    /// <summary>Original engine <c>teamcrosshair</c> (fps.cpp VARP, default 1).</summary>
    public bool TeamCrosshair { get; set; } = true;

    /// <summary>Original engine <c>hitcrosshair</c> duration in ms (fps.cpp VARP, default 425).</summary>
    public int HitCrosshairDuration { get; set; } = 425;

    public Texture2D? TexDefault { get; set; }
    public Texture2D? TexTeammate { get; set; }
    public Texture2D? TexHit { get; set; }

    public void LoadTextures(ITextureSource textures)
    {
        TexDefault = textures.Get(DefaultPath);
        TexTeammate = textures.Get(TeammatePath);
        TexHit = textures.Get(HitPath);
    }

    /// <summary>
    /// Evaluates which crosshair texture and tint color to display according to engine rules:
    /// returns crosshair index (0: default, 1: teammate, 2: hit) and modulated color.
    /// Returns -1 if crosshair should be hidden (dead or spectator).
    /// </summary>
    public int SelectCrosshair(
        MapPlayer player,
        int nowMillis,
        bool isTeamPlay,
        IHitscanTarget? aimedTarget,
        out Color color)
    {
        color = Colors.White;

        // In fps.cpp:1123: if(d->state==CS_SPECTATOR || d->state==CS_DEAD) return -1;
        if (player.Entity.Dead)
            return -1;

        if (player.Entity.State != PhysEnt.CsAlive)
            return CrosshairDefault;

        int crosshair = CrosshairDefault;

        // 1. Hit confirmation crosshair (fps.cpp:1128: if(lasthit && lastmillis - lasthit < hitcrosshair) crosshair = 2;)
        if (player.LastHit > 0 && (nowMillis - player.LastHit) < HitCrosshairDuration)
        {
            crosshair = CrosshairHit;
        }
        else if (TeamCrosshair && isTeamPlay && aimedTarget is MapPlayer otherPlayer)
        {
            // 2. Teammate crosshair (fps.cpp:1129-1137)
            if (otherPlayer.Team == player.Team)
            {
                crosshair = CrosshairTeammate;
                color = new Color(0f, 0f, 1f); // vec(0, 0, 1) in engine
            }
        }

        // 3. Health color warning (fps.cpp:1139-1143)
        // if(crosshair!=1 && !editmode && !m_insta)
        if (crosshair != CrosshairTeammate)
        {
            if (player.Combat.Health <= 25)
                color = new Color(1f, 0f, 0f);
            else if (player.Combat.Health <= 50)
                color = new Color(1f, 0.5f, 0f);
        }

        // 4. Gunwait cooldown dimming (fps.cpp:1144: if(d->gunwait) color.mul(0.5f);)
        if (player.Combat.GunWait > 0)
        {
            color = new Color(color.R * 0.5f, color.G * 0.5f, color.B * 0.5f, color.A);
        }

        if (!CrosshairFx) crosshair = CrosshairDefault;
        if (!CrosshairFx || !CrosshairColors) color = Colors.White;

        return crosshair;
    }

    /// <summary>Gets the loaded texture for the given crosshair index.</summary>
    public Texture2D? GetTexture(int index) => index switch
    {
        CrosshairHit => TexHit ?? TexDefault,
        CrosshairTeammate => TexTeammate ?? TexDefault,
        _ => TexDefault
    };

    /// <summary>
    /// Computes pixel size matching engine formula <c>chsize = crosshairsize * w / 900.0f</c> (rendergl.cpp:2151).
    /// </summary>
    public float CalculateSize(float screenWidth)
    {
        return CrosshairSize * screenWidth / 900.0f;
    }
}
