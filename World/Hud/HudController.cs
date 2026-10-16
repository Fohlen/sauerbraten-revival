using System;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Hud;

/// <summary>
/// Root controller for the in-game HUD overlay (crosshair, status bar icons, counters).
/// Attached to a CanvasLayer over the main viewport.
/// </summary>
public partial class HudController : Control
{
    private TextureRect? _crosshairRect;
    private readonly CrosshairRenderer _crosshairRenderer = new();

    // Bottom status bar elements
    private Control? _statusBarRoot;
    private TextureRect? _healthIcon;
    private Label? _healthLabel;
    private Label? _maxHealthLabel;

    private Texture2D? _itemsTexture;

    public CrosshairRenderer Crosshair => _crosshairRenderer;

    /// <summary>The player whose status and view drive this HUD.</summary>
    public MapPlayer? Player { get; set; }

    /// <summary>Candidate targets in the world (for teammate and hit detection).</summary>
    public System.Collections.Generic.IReadOnlyList<IHitscanTarget>? Targets { get; set; }

    /// <summary>Whether the current game mode is team-based.</summary>
    public bool IsTeamPlay { get; set; }

    /// <summary>Whether health numbers change color with low health (VARP healthcolors in fps.cpp).</summary>
    public bool HealthColors { get; set; } = true;

    /// <summary>Whether current mode is insta-gib (disables low-health coloring).</summary>
    public bool IsInsta { get; set; }

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

        SetupStatusBarNodes();
        UpdateAtlasTextures();
    }

    private void SetupStatusBarNodes()
    {
        _statusBarRoot = new Control
        {
            Name = "StatusBar",
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_statusBarRoot);

        // Health Icon
        _healthIcon = new TextureRect
        {
            Name = "HealthIcon",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _statusBarRoot.AddChild(_healthIcon);

        // Health Number
        _healthLabel = new Label
        {
            Name = "HealthLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusBarRoot.AddChild(_healthLabel);

        // Max Health boost overlay (e.g. "/200")
        _maxHealthLabel = new Label
        {
            Name = "MaxHealthLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusBarRoot.AddChild(_maxHealthLabel);
    }

    /// <summary>Initialize textures using the project's texture source.</summary>
    public void SetupTextures(ITextureSource textures)
    {
        _crosshairRenderer.LoadTextures(textures);
        _itemsTexture = textures.Get("hud/items.png");
        UpdateAtlasTextures();
    }

    private void UpdateAtlasTextures()
    {
        if (_itemsTexture == null) return;

        if (_healthIcon != null)
        {
            var atlas = new AtlasTexture
            {
                Atlas = _itemsTexture,
                Region = StatusBarRenderer.GetIconUv(HudIcon.Health)
            };
            // GetIconUv returns normalized 0..1 coordinates for a 4x4 grid; convert to pixel region on items.png (256x256)
            Vector2 texSize = _itemsTexture.GetSize();
            if (texSize.X > 0 && texSize.Y > 0)
            {
                Rect2 uv = StatusBarRenderer.GetIconUv(HudIcon.Health);
                atlas.Region = new Rect2(uv.Position.X * texSize.X, uv.Position.Y * texSize.Y, uv.Size.X * texSize.X, uv.Size.Y * texSize.Y);
            }
            _healthIcon.Texture = atlas;
        }
    }

    public override void _Process(double delta)
    {
        UpdateCrosshair();
        UpdateStatusBar();
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

    /// <summary>Updates health icon and numeric counter in the bottom status bar.</summary>
    public void UpdateStatusBar()
    {
        if (_statusBarRoot == null || Player == null) return;

        Vector2 viewportSize = GetViewportRect().Size;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0) return;

        // Sauerbraten coordinates scale by viewport height / 1800 (fps.cpp:1060)
        float scale = viewportSize.Y / StatusBarRenderer.ReferenceHeight;

        bool isDead = Player.Entity.State == PhysEnt.CsDead || Player.Entity.Dead;
        int health = isDead ? 0 : Player.Combat.Health;

        // Health Icon: placed at (HICON_X, HICON_Y) with size HICON_SIZE (game.h:320-324)
        if (_healthIcon != null)
        {
            _healthIcon.Position = new Vector2(StatusBarRenderer.IconX * scale, StatusBarRenderer.IconY * scale);
            _healthIcon.Size = new Vector2(StatusBarRenderer.IconSize * scale, StatusBarRenderer.IconSize * scale);
        }

        // Health text color
        Color healthColor = StatusBarRenderer.GetHealthColor(health, isDead, HealthColors, IsInsta);

        // Health Label: in Sauerbraten fps.cpp:906, text matrix is scaled by 2,
        // placed at (HICON_X + HICON_SIZE + HICON_SPACE) / 2 and HICON_TEXTY / 2.
        // In unscaled coordinate space, position is (HICON_X + HICON_SIZE + HICON_SPACE, HICON_TEXTY).
        if (_healthLabel != null)
        {
            _healthLabel.Text = health.ToString();
            _healthLabel.Modulate = healthColor;
            float textX = (StatusBarRenderer.IconX + StatusBarRenderer.IconSize + StatusBarRenderer.IconSpace) * scale;
            float iconY = StatusBarRenderer.IconY * scale;
            float iconHeight = StatusBarRenderer.IconSize * scale;
            _healthLabel.Position = new Vector2(textX, iconY);
            _healthLabel.Size = new Vector2(200f * scale, iconHeight);
            _healthLabel.VerticalAlignment = VerticalAlignment.Center;
            int fontSize = Mathf.Max(12, (int)(64f * scale));
            _healthLabel.AddThemeFontSizeOverride("font_size", fontSize);
        }

        // Max health boost label (fps.cpp:920-932: if(d->state != CS_DEAD && d->maxhealth > 100))
        if (_maxHealthLabel != null)
        {
            if (!isDead && Player.Combat.MaxHealth > 100)
            {
                _maxHealthLabel.Visible = true;
                _maxHealthLabel.Text = $"/{Player.Combat.MaxHealth}";
                _maxHealthLabel.Modulate = healthColor;

                // Positioned right after current health text (fps.cpp:929)
                Vector2 healthTextSize = _healthLabel?.GetCombinedMinimumSize() ?? Vector2.Zero;
                float maxX = (_healthLabel?.Position.X ?? 0f) + healthTextSize.X + 8f * scale;
                float maxY = (StatusBarRenderer.TextY + 16f) * scale;
                _maxHealthLabel.Position = new Vector2(maxX, maxY);
                int maxFontSize = Mathf.Max(10, (int)(42f * scale));
                _maxHealthLabel.AddThemeFontSizeOverride("font_size", maxFontSize);
            }
            else
            {
                _maxHealthLabel.Visible = false;
            }
        }
    }
}
