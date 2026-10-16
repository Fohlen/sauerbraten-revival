using System;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Hud;

/// <summary>
/// Root controller for the in-game HUD overlay (crosshair, status icons, weapon bars).
/// Attached to a CanvasLayer over the main viewport.
/// </summary>
public partial class HudController : Control
{
    private TextureRect? _crosshairRect;
    private readonly CrosshairRenderer _crosshairRenderer = new();

    public CrosshairRenderer Crosshair => _crosshairRenderer;

    /// <summary>The player whose status and view drive this HUD.</summary>
    public MapPlayer? Player { get; set; }

    /// <summary>Candidate targets in the world (for teammate and hit detection).</summary>
    public System.Collections.Generic.IReadOnlyList<IHitscanTarget>? Targets { get; set; }

    /// <summary>Whether the current game mode is team-based.</summary>
    public bool IsTeamPlay { get; set; }

    public override void _Ready()
    {
        // Full screen root layout
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _crosshairRect = GetNodeOrNull<TextureRect>("Crosshair");
        if (_crosshairRect == null)
        {
            _crosshairRect = new TextureRect
            {
                Name = "Crosshair",
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore
            };
            AddChild(_crosshairRect);
        }
    }

    /// <summary>Initialize textures using the project's texture source.</summary>
    public void SetupTextures(ITextureSource textures)
    {
        _crosshairRenderer.LoadTextures(textures);
    }

    public override void _Process(double delta)
    {
        UpdateCrosshair();
    }

    /// <summary>Updates crosshair position, texture, size and modulation.</summary>
    public void UpdateCrosshair()
    {
        if (_crosshairRect == null || Player == null) return;

        Vector2 viewportSize = GetViewportRect().Size;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0) return;

        int nowMillis = Player.LastMillis > 0 ? Player.LastMillis : (int)Time.GetTicksMsec();

        // Check if player is aiming directly at a candidate target
        IHitscanTarget? aimedTarget = null;
        if (Targets != null && Targets.Count > 0)
        {
            Vector3 from = Player.Entity.O;
            Vector3 forward = Movement.VecFromYawPitch(Player.Entity.Yaw, Player.Pitch, 1, 0);
            if (forward.LengthSquared() < 1e-6f) forward = new Vector3(0f, 1f, 0f);
            Vector3 to = from + forward.Normalized() * 2048f;
            aimedTarget = WeaponHitscan.IntersectClosest(from, to, Targets, out _);
        }

        int index = _crosshairRenderer.SelectCrosshair(
            Player,
            nowMillis,
            IsTeamPlay,
            aimedTarget,
            out Color color);

        if (index < 0)
        {
            _crosshairRect.Visible = false;
            return;
        }

        _crosshairRect.Visible = true;
        _crosshairRect.Texture = _crosshairRenderer.GetTexture(index);
        _crosshairRect.Modulate = color;

        float size = _crosshairRenderer.CalculateSize(viewportSize.X);
        _crosshairRect.Size = new Vector2(size, size);
        _crosshairRect.Position = (viewportSize - _crosshairRect.Size) * 0.5f;
    }
}
