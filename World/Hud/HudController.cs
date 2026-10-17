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

    private TextureRect? _armorIcon;
    private Label? _armorLabel;

    private TextureRect? _weaponIcon;
    private Label? _ammoLabel;

    private TextureRect? _quadIcon;

    private TextureRect? _flagIcon;
    private Label? _flagLabel;

    private Texture2D? _itemsTexture;
    private ArmourType? _lastArmourType;
    private Gun? _lastGun;
    private HudIcon? _lastFlagIcon;

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

    /// <summary>Whether the current player is carrying a flag (CTF/Hold).</summary>
    public bool HasFlag { get; set; }

    /// <summary>Whether current mode is hold mode (m_hold in Sauerbraten).</summary>
    public bool IsHoldMode { get; set; }

    /// <summary>The team ID of the flag currently held (1 = good/blue, 2 = evil/red, 0 = neutral).</summary>
    public int HeldFlagTeam { get; set; }

    /// <summary>Remaining hold seconds countdown for hold mode (max(HOLDSECS - counter/1000, 0)).</summary>
    public int HoldRemainingSeconds { get; set; }

    /// <summary>Whether current mode is efficiency mode (no pistol).</summary>
    public bool IsEfficiency { get; set; }

    /// <summary>Whether quick weapon cycling HUD is enabled (VARP ammohud in fps.cpp).</summary>
    public bool AmmoHud { get; set; } = true;

    /// <summary>Whether full ammo bar strip is enabled (VARP ammobar in fps.cpp).</summary>
    public bool AmmoBar { get; set; } = false;

    /// <summary>Ammo bar alignment (-1 = left/top, 0 = center, 1 = right/bottom) (VARP ammobaralign).</summary>
    public int AmmoBarAlign { get; set; } = 0;

    /// <summary>Ammo bar orientation (horizontal vs vertical) (VARP ammobarhorizontal).</summary>
    public bool AmmoBarHorizontal { get; set; } = false;

    /// <summary>Ammo bar flip offset direction (VARP ammobarflip).</summary>
    public bool AmmoBarFlip { get; set; } = false;

    /// <summary>Ammo bar hide empty weapons (VARP ammobarhideempty).</summary>
    public bool AmmoBarHideEmpty { get; set; } = true;

    /// <summary>Ammo bar spacing between weapon icons (VARP ammobarsep).</summary>
    public float AmmoBarSep { get; set; } = 20.0f;

    /// <summary>Ammo bar spacing between icon and counter text (VARP ammobarcountsep).</summary>
    public float AmmoBarCountSep { get; set; } = 20.0f;

    /// <summary>Ammo bar counter font scale multiplier (FVARP ammobarcountscale).</summary>
    public float AmmoBarCountScale { get; set; } = 1.5f;

    /// <summary>Ammo bar normalized screen X position (FVARP ammobarx).</summary>
    public float AmmoBarX { get; set; } = 0.025f;

    /// <summary>Ammo bar normalized screen Y position (FVARP ammobary).</summary>
    public float AmmoBarY { get; set; } = 0.5f;

    /// <summary>Ammo bar overall scale multiplier (FVARP ammobarscale).</summary>
    public float AmmoBarScale { get; set; } = 0.5f;

    /// <summary>Weapon layout for ammo hud 'up' stack (3 weapons).</summary>
    public int[] AmmoHudUp { get; set; } = new int[] { (int)Gun.Chaingun, (int)Gun.RocketLauncher, (int)Gun.GrenadeLauncher };

    /// <summary>Weapon layout for ammo hud 'down' stack (3 weapons).</summary>
    public int[] AmmoHudDown { get; set; } = new int[] { (int)Gun.Rifle, (int)Gun.Shotgun, (int)Gun.Pistol };

    /// <summary>Weapon layout for ammo hud 'cycle' row (7 weapons).</summary>
    public int[] AmmoHudCycle { get; set; } = new int[] { -1, -1, -1, -1, -1, -1, -1 };

    /// <summary>Whether the match clock / game timer is displayed (VARP gameclock in fps.cpp:944).</summary>
    public bool GameClock { get; set; } = true;

    /// <summary>Game clock scale multiplier (FVARP gameclockscale in fps.cpp:945, default 1.0).</summary>
    public float GameClockScale { get; set; } = 1.0f;

    /// <summary>Game clock text color (HVARP gameclockcolour in fps.cpp:946, default 0xFFFFFF).</summary>
    public uint GameClockColour { get; set; } = 0xFFFFFF;

    /// <summary>Game clock alpha transparency (VARP gameclockalpha in fps.cpp:947, default 255).</summary>
    public int GameClockAlpha { get; set; } = 255;

    /// <summary>Game clock warning color when time is under 1 minute (HVARP gameclocklowcolour in fps.cpp:948, default 0xFFA050).</summary>
    public uint GameClockLowColour { get; set; } = 0xFFA050;

    /// <summary>Game clock horizontal alignment (-1 = left, 0 = center, 1 = right) (VARP gameclockalign in fps.cpp:949, default 0).</summary>
    public int GameClockAlign { get; set; } = 0;

    /// <summary>Game clock normalized screen X position (FVARP gameclockx in fps.cpp:950, default 0.50).</summary>
    public float GameClockX { get; set; } = 0.50f;

    /// <summary>Game clock normalized screen Y position (FVARP gameclocky in fps.cpp:951, default 0.03).</summary>
    public float GameClockY { get; set; } = 0.03f;

    /// <summary>Match expiration timestamp in milliseconds (engine maplimit in fps.cpp:6, client.cpp:564). Negative if untimed/infinite.</summary>
    public int MapLimit { get; set; } = -1;

    /// <summary>Current match/engine elapsed timestamp in milliseconds (engine lastmillis). If negative, falls back to Player.LastMillis or Time.GetTicksMsec().</summary>
    public int LastMillis { get; set; } = -1;

    // Quick ammo hud nodes container
    private Control? _ammoHudRoot;
    private readonly System.Collections.Generic.List<TextureRect> _ammoHudIcons = new();

    // Ammo bar nodes container
    private Control? _ammoBarRoot;
    private readonly System.Collections.Generic.List<(TextureRect Icon, Label Label)> _ammoBarSlots = new();

    // Game clock / match timer node
    private Label? _gameClockLabel;

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
        SetupAmmoHudNodes();
        SetupAmmoBarNodes();
        SetupGameClockNode();
        UpdateAtlasTextures();
    }

    private void SetupGameClockNode()
    {
        _gameClockLabel = new Label
        {
            Name = "GameClockLabel",
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_gameClockLabel);
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

        // Armor Icon
        _armorIcon = new TextureRect
        {
            Name = "ArmorIcon",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        _statusBarRoot.AddChild(_armorIcon);

        // Armor Number
        _armorLabel = new Label
        {
            Name = "ArmorLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
            Visible = false
        };
        _statusBarRoot.AddChild(_armorLabel);

        // Active Weapon Icon
        _weaponIcon = new TextureRect
        {
            Name = "WeaponIcon",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        _statusBarRoot.AddChild(_weaponIcon);

        // Active Weapon Ammo Number
        _ammoLabel = new Label
        {
            Name = "AmmoLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
            Visible = false
        };
        _statusBarRoot.AddChild(_ammoLabel);

        // Quad Damage Icon
        _quadIcon = new TextureRect
        {
            Name = "QuadIcon",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        _statusBarRoot.AddChild(_quadIcon);

        // Flag Indicator Icon
        _flagIcon = new TextureRect
        {
            Name = "FlagIcon",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        _statusBarRoot.AddChild(_flagIcon);

        // Flag Hold Counter Number
        _flagLabel = new Label
        {
            Name = "FlagLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
            Visible = false
        };
        _statusBarRoot.AddChild(_flagLabel);
    }

    private void SetupAmmoHudNodes()
    {
        _ammoHudRoot = new Control
        {
            Name = "AmmoHud",
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_ammoHudRoot);

        // Pool up to 13 possible ammohud icons (3 up + 3 down + 7 cycle)
        for (int i = 0; i < 16; i++)
        {
            var iconRect = new TextureRect
            {
                Name = $"AmmoHudIcon_{i}",
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
                Visible = false
            };
            _ammoHudRoot.AddChild(iconRect);
            _ammoHudIcons.Add(iconRect);
        }
    }

    private void SetupAmmoBarNodes()
    {
        _ammoBarRoot = new Control
        {
            Name = "AmmoBar",
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_ammoBarRoot);

        // Pool slots for all 6 player guns (SG through Pistol)
        int numPlayerGuns = (int)Gun.Pistol - (int)Gun.Shotgun + 1;
        for (int i = 0; i < numPlayerGuns; i++)
        {
            var iconRect = new TextureRect
            {
                Name = $"AmmoBarIcon_{i}",
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
                Visible = false
            };
            _ammoBarRoot.AddChild(iconRect);

            var label = new Label
            {
                Name = $"AmmoBarLabel_{i}",
                MouseFilter = MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visible = false
            };
            _ammoBarRoot.AddChild(label);

            _ammoBarSlots.Add((iconRect, label));
        }
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
            _healthIcon.Texture = CreateAtlasTexture(HudIcon.Health);
        }

        if (_armorIcon != null && _lastArmourType.HasValue)
        {
            _armorIcon.Texture = CreateAtlasTexture(StatusBarRenderer.GetArmorIcon(_lastArmourType.Value));
        }

        if (_weaponIcon != null && _lastGun.HasValue)
        {
            _weaponIcon.Texture = CreateAtlasTexture(StatusBarRenderer.GetGunIcon(_lastGun.Value));
        }

        if (_quadIcon != null)
        {
            _quadIcon.Texture = CreateAtlasTexture(HudIcon.Quad);
        }

        if (_flagIcon != null && _lastFlagIcon.HasValue)
        {
            _flagIcon.Texture = CreateAtlasTexture(_lastFlagIcon.Value);
        }
    }

    private AtlasTexture? CreateAtlasTexture(HudIcon icon)
    {
        if (_itemsTexture == null) return null;

        var atlas = new AtlasTexture
        {
            Atlas = _itemsTexture,
            Region = StatusBarRenderer.GetIconUv(icon)
        };
        Vector2 texSize = _itemsTexture.GetSize();
        if (texSize.X > 0 && texSize.Y > 0)
        {
            Rect2 uv = StatusBarRenderer.GetIconUv(icon);
            atlas.Region = new Rect2(uv.Position.X * texSize.X, uv.Position.Y * texSize.Y, uv.Size.X * texSize.X, uv.Size.Y * texSize.Y);
        }
        return atlas;
    }

    public override void _Process(double delta)
    {
        UpdateCrosshair();
        UpdateStatusBar();
        UpdateGameClock();
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

        // Armor Icon & text (fps.cpp:914, 937: if(d->state!=CS_DEAD && d->armour))
        int armour = isDead ? 0 : Player.Combat.Armour;
        ArmourType armourType = Player.Combat.ArmourType;

        if (armour > 0)
        {
            if (_armorIcon != null)
            {
                if (_lastArmourType != armourType)
                {
                    _lastArmourType = armourType;
                    _armorIcon.Texture = CreateAtlasTexture(StatusBarRenderer.GetArmorIcon(armourType));
                }
                _armorIcon.Visible = true;
                _armorIcon.Position = new Vector2((StatusBarRenderer.IconX + StatusBarRenderer.IconStep) * scale, StatusBarRenderer.IconY * scale);
                _armorIcon.Size = new Vector2(StatusBarRenderer.IconSize * scale, StatusBarRenderer.IconSize * scale);
            }

            if (_armorLabel != null)
            {
                _armorLabel.Visible = true;
                _armorLabel.Text = armour.ToString();
                _armorLabel.Modulate = Colors.White;
                float armorTextX = (StatusBarRenderer.IconX + StatusBarRenderer.IconStep + StatusBarRenderer.IconSize + StatusBarRenderer.IconSpace) * scale;
                float iconY = StatusBarRenderer.IconY * scale;
                float iconHeight = StatusBarRenderer.IconSize * scale;
                _armorLabel.Position = new Vector2(armorTextX, iconY);
                _armorLabel.Size = new Vector2(200f * scale, iconHeight);
                _armorLabel.VerticalAlignment = VerticalAlignment.Center;
                int fontSize = Mathf.Max(12, (int)(64f * scale));
                _armorLabel.AddThemeFontSizeOverride("font_size", fontSize);
            }
        }
        else
        {
            if (_armorIcon != null) _armorIcon.Visible = false;
            if (_armorLabel != null) _armorLabel.Visible = false;
        }

        // Active Weapon Icon & Ammo text (fps.cpp:915, 938: if(d->state!=CS_DEAD))
        if (!isDead)
        {
            Gun gun = (Gun)Player.Combat.GunSelect;
            int ammo = Player.Combat.SelectedAmmo;

            if (_weaponIcon != null)
            {
                if (_lastGun != gun)
                {
                    _lastGun = gun;
                    _weaponIcon.Texture = CreateAtlasTexture(StatusBarRenderer.GetGunIcon(gun));
                }
                _weaponIcon.Visible = true;
                _weaponIcon.Position = new Vector2((StatusBarRenderer.IconX + 2 * StatusBarRenderer.IconStep) * scale, StatusBarRenderer.IconY * scale);
                _weaponIcon.Size = new Vector2(StatusBarRenderer.IconSize * scale, StatusBarRenderer.IconSize * scale);
            }

            if (_ammoLabel != null)
            {
                _ammoLabel.Visible = true;
                _ammoLabel.Text = ammo.ToString();
                _ammoLabel.Modulate = Colors.White;
                float ammoTextX = (StatusBarRenderer.IconX + 2 * StatusBarRenderer.IconStep + StatusBarRenderer.IconSize + StatusBarRenderer.IconSpace) * scale;
                float iconY = StatusBarRenderer.IconY * scale;
                float iconHeight = StatusBarRenderer.IconSize * scale;
                _ammoLabel.Position = new Vector2(ammoTextX, iconY);
                _ammoLabel.Size = new Vector2(200f * scale, iconHeight);
                _ammoLabel.VerticalAlignment = VerticalAlignment.Center;
                int fontSize = Mathf.Max(12, (int)(64f * scale));
                _ammoLabel.AddThemeFontSizeOverride("font_size", fontSize);
            }

            // Quad Damage Icon (fps.cpp:939: if(d->quadmillis) drawicon(HICON_QUAD, HICON_X + 3*HICON_STEP, HICON_Y))
            bool hasQuad = Player.Combat.QuadMillis > 0;
            if (hasQuad)
            {
                if (_quadIcon != null)
                {
                    if (_quadIcon.Texture == null)
                    {
                        _quadIcon.Texture = CreateAtlasTexture(HudIcon.Quad);
                    }
                    _quadIcon.Visible = true;
                    _quadIcon.Position = new Vector2((StatusBarRenderer.IconX + 3 * StatusBarRenderer.IconStep) * scale, StatusBarRenderer.IconY * scale);
                    _quadIcon.Size = new Vector2(StatusBarRenderer.IconSize * scale, StatusBarRenderer.IconSize * scale);
                }
            }
            else
            {
                if (_quadIcon != null) _quadIcon.Visible = false;
            }

            // CTF / Mode flag: flag indicator icon with hold counter or carrier status
            // (ctf.h:547-565: drawhud in ctfmode)
            // Position: int x = HICON_X + 3*HICON_STEP + (d->quadmillis ? HICON_SIZE + HICON_SPACE : 0);
            // drawicon(m_hold ? HICON_NEUTRAL_FLAG : (f.team==ctfteamflag(player1->team) ? HICON_BLUE_FLAG : HICON_RED_FLAG), x, HICON_Y);
            if (HasFlag)
            {
                HudIcon flagIconType = StatusBarRenderer.GetFlagIcon(IsHoldMode, HeldFlagTeam, Player.Team);
                if (_flagIcon != null)
                {
                    if (_lastFlagIcon != flagIconType || _flagIcon.Texture == null)
                    {
                        _lastFlagIcon = flagIconType;
                        _flagIcon.Texture = CreateAtlasTexture(flagIconType);
                    }
                    _flagIcon.Visible = true;

                    float flagX = StatusBarRenderer.IconX + 3 * StatusBarRenderer.IconStep + (hasQuad ? StatusBarRenderer.IconSize + StatusBarRenderer.IconSpace : 0f);
                    _flagIcon.Position = new Vector2(flagX * scale, StatusBarRenderer.IconY * scale);
                    _flagIcon.Size = new Vector2(StatusBarRenderer.IconSize * scale, StatusBarRenderer.IconSize * scale);
                }

                if (_flagLabel != null)
                {
                    if (IsHoldMode)
                    {
                        // In Sauerbraten hold mode: draw_textf("%d", (x + HICON_SIZE + HICON_SPACE)/2, HICON_TEXTY/2, max(HOLDSECS - f.holdcounter()/1000, 0));
                        _flagLabel.Visible = true;
                        _flagLabel.Text = Math.Max(0, HoldRemainingSeconds).ToString();
                        _flagLabel.Modulate = Colors.White;

                        float flagX = StatusBarRenderer.IconX + 3 * StatusBarRenderer.IconStep + (hasQuad ? StatusBarRenderer.IconSize + StatusBarRenderer.IconSpace : 0f);
                        float flagTextX = (flagX + StatusBarRenderer.IconSize + StatusBarRenderer.IconSpace) * scale;
                        float iconY = StatusBarRenderer.IconY * scale;
                        float iconHeight = StatusBarRenderer.IconSize * scale;
                        _flagLabel.Position = new Vector2(flagTextX, iconY);
                        _flagLabel.Size = new Vector2(200f * scale, iconHeight);
                        _flagLabel.VerticalAlignment = VerticalAlignment.Center;
                        int fontSize = Mathf.Max(12, (int)(64f * scale));
                        _flagLabel.AddThemeFontSizeOverride("font_size", fontSize);
                    }
                    else
                    {
                        _flagLabel.Visible = false;
                    }
                }
            }
            else
            {
                if (_flagIcon != null) _flagIcon.Visible = false;
                if (_flagLabel != null) _flagLabel.Visible = false;
            }
        }
        else
        {
            if (_weaponIcon != null) _weaponIcon.Visible = false;
            if (_ammoLabel != null) _ammoLabel.Visible = false;
            if (_quadIcon != null) _quadIcon.Visible = false;
            if (_flagIcon != null) _flagIcon.Visible = false;
            if (_flagLabel != null) _flagLabel.Visible = false;
        }

        UpdateAmmoHud(scale, isDead);
        UpdateAmmoBar(viewportSize, isDead);
    }

    /// <summary>
    /// Updates the quick weapon selector / cycling HUD (drawammohud in fps.cpp:860-899).
    /// </summary>
    public void UpdateAmmoHud(float scale, bool isDead)
    {
        if (_ammoHudRoot == null) return;

        if (isDead || !AmmoHud || Player == null)
        {
            _ammoHudRoot.Visible = false;
            return;
        }

        _ammoHudRoot.Visible = true;
        var items = StatusBarRenderer.LayoutAmmoHud(
            Player.Combat.Ammo,
            Player.Combat.GunSelect,
            AmmoHudUp,
            AmmoHudDown,
            AmmoHudCycle);

        for (int i = 0; i < _ammoHudIcons.Count; i++)
        {
            var iconRect = _ammoHudIcons[i];
            if (i < items.Count)
            {
                var item = items[i];
                iconRect.Visible = true;
                iconRect.Texture = CreateAtlasTexture(StatusBarRenderer.GetGunIcon(item.Gun));
                iconRect.Position = item.Position * scale;
                iconRect.Size = new Vector2(item.Size * scale, item.Size * scale);
                iconRect.Modulate = Colors.White;
            }
            else
            {
                iconRect.Visible = false;
            }
        }
    }

    /// <summary>
    /// Updates the full ammo bar strip (drawammobar in fps.cpp:1028-1055).
    /// </summary>
    public void UpdateAmmoBar(Vector2 viewportSize, bool isDead)
    {
        if (_ammoBarRoot == null) return;

        if (isDead || !AmmoBar || IsInsta || Player == null)
        {
            _ammoBarRoot.Visible = false;
            return;
        }

        _ammoBarRoot.Visible = true;
        var items = StatusBarRenderer.LayoutAmmoBar(
            Player.Combat.Ammo,
            Player.Combat.GunSelect,
            viewportSize.X,
            viewportSize.Y,
            IsInsta,
            IsEfficiency,
            AmmoBarAlign,
            AmmoBarHorizontal,
            AmmoBarFlip,
            AmmoBarHideEmpty,
            AmmoBarSep,
            AmmoBarCountSep,
            AmmoBarCountScale,
            AmmoBarX,
            AmmoBarY,
            AmmoBarScale);

        // In Sauerbraten:
        // text_bounds("000", fw, fh) -> fw = 3 * 30 = 90, fh = 64
        // labeloffset = HICON_SIZE / 2.0f + ammobarcountsep + ammobarcountscale * (ammobarhorizontal ? fh : fw) / 2.0f;
        // offsetdir = (ammobarhorizontal ? vec2(0, 1) : vec2(1, 0)).mul(ammobarflip ? -1 : 1);
        // labelorigin = vec2(offsetdir).mul(labeloffset).add(center);
        // pushhudmatrix(); hudmatrix.translate(labelorigin.x, labelorigin.y, 0); hudmatrix.scale(ammobarcountscale, ammobarcountscale, 1);
        // textdrawpos = vec2(-tw, -th).div(2);
        // All of drawammobar is wrapped in hudmatrix.scale(ammobarscale, ammobarscale, 1).
        float fw = 3.0f * StatusBarRenderer.DefaultFontCharWidth;
        float fh = StatusBarRenderer.DefaultFontHeight;
        float labelOffset = (StatusBarRenderer.IconSize / 2f + AmmoBarCountSep + AmmoBarCountScale * (AmmoBarHorizontal ? fh : fw) / 2f);
        Vector2 offsetDir = (AmmoBarHorizontal ? new Vector2(0, 1) : new Vector2(1, 0)) * (AmmoBarFlip ? -1f : 1f);

        for (int i = 0; i < _ammoBarSlots.Count; i++)
        {
            var (iconRect, label) = _ammoBarSlots[i];
            if (i < items.Count)
            {
                var item = items[i];
                iconRect.Visible = true;
                label.Visible = true;

                // Center in bar-space -> scaled to screen coordinates
                Vector2 screenCenter = item.Center * AmmoBarScale;
                float iconSize = StatusBarRenderer.IconSize * AmmoBarScale;
                Vector2 iconDrawPos = screenCenter - new Vector2(iconSize / 2f, iconSize / 2f);

                iconRect.Texture = CreateAtlasTexture(StatusBarRenderer.GetGunIcon(item.Gun));
                iconRect.Position = iconDrawPos;
                iconRect.Size = new Vector2(iconSize, iconSize);
                iconRect.Modulate = new Color(1f, 1f, 1f, item.Alpha);

                // Label origin:
                Vector2 labelOrigin = (item.Center + offsetDir * labelOffset) * AmmoBarScale;
                // Font size scales with AmmoBarScale * AmmoBarCountScale
                float totalCountScale = AmmoBarScale * AmmoBarCountScale;
                int fontSize = Mathf.Max(10, (int)(StatusBarRenderer.DefaultFontHeight * totalCountScale));
                label.AddThemeFontSizeOverride("font_size", fontSize);
                label.Text = item.Ammo.ToString();
                label.Modulate = new Color(item.Color.R, item.Color.G, item.Color.B, item.Alpha);

                // Center label over labelOrigin
                Vector2 textSize = label.GetCombinedMinimumSize();
                label.Position = labelOrigin - textSize / 2f;
                label.Size = textSize;
            }
            else
            {
                iconRect.Visible = false;
                label.Visible = false;
            }
        }
    }

    /// <summary>
    /// Updates the game clock and match timer (engine <c>fps.cpp:953-971: drawgameclock</c>).
    /// </summary>
    public void UpdateGameClock()
    {
        if (_gameClockLabel == null) return;

        // In Sauerbraten: if(gameclock) drawgameclock(w, h);
        // Only drawn if gameclock is enabled and MapLimit is active (>= 0).
        if (!GameClock || MapLimit < 0)
        {
            _gameClockLabel.Visible = false;
            return;
        }

        Vector2 viewportSize = GetViewportRect().Size;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0) return;

        int currentMillis = LastMillis >= 0
            ? LastMillis
            : (Player != null && Player.LastMillis > 0 ? Player.LastMillis : (int)Time.GetTicksMsec());

        var (mins, secs, formatted) = StatusBarRenderer.FormatGameClock(MapLimit, currentMillis);
        Color clockColor = StatusBarRenderer.GetGameClockColor(mins, GameClockColour, GameClockLowColour, GameClockAlpha);

        _gameClockLabel.Visible = true;
        _gameClockLabel.Text = formatted;
        _gameClockLabel.Modulate = clockColor;

        var (screenPos, screenSize) = StatusBarRenderer.LayoutGameClock(
            viewportSize.X,
            viewportSize.Y,
            formatted,
            GameClockX,
            GameClockY,
            GameClockScale,
            GameClockAlign);

        int fontSize = Mathf.Max(12, (int)(StatusBarRenderer.DefaultFontHeight * GameClockScale));
        _gameClockLabel.AddThemeFontSizeOverride("font_size", fontSize);
        _gameClockLabel.Position = screenPos;
        _gameClockLabel.Size = screenSize;
        _gameClockLabel.HorizontalAlignment = GameClockAlign switch
        {
            -1 => HorizontalAlignment.Left,
            1 => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        };
        _gameClockLabel.VerticalAlignment = VerticalAlignment.Center;
    }
}

