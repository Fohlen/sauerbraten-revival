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
    private bool _statusBarDirty = true;
    private Vector2 _lastViewportSize;
    private int _lastSelectedAmmo = -1;
    private int _lastArmour = -1;
    private bool _lastQuadActive;

    private MapPlayer? _player;
    private int _observedHealth = PlayerState.DefaultMaxHealth;
    private bool _observedDead;
    private Gun _observedGun = Gun.Pistol;

    public CrosshairRenderer Crosshair => _crosshairRenderer;

    /// <summary>The player whose status and view drive this HUD.</summary>
    public MapPlayer? Player
    {
        get => _player;
        set
        {
            if (ReferenceEquals(_player, value)) return;
            DisconnectPlayerSignals(_player);
            _player = value;
            ConnectPlayerSignals(_player);
            _statusBarDirty = true;
            if (IsInsideTree() && _statusBarRoot != null)
            {
                UpdateStatusBar();
                UpdateCrosshair();
            }
        }
    }

    private void ConnectPlayerSignals(MapPlayer? player)
    {
        if (player == null || !IsInstanceValid(player)) return;
        _observedHealth = player.Health;
        _observedDead = player.Entity.Dead;
        _observedGun = (Gun)player.GunSelect;
        player.HealthChanged += OnPlayerHealthChanged;
        player.Combat.ArmourChanged += OnPlayerArmourChanged;
        player.Died += OnPlayerDied;
        player.Respawned += OnPlayerRespawned;
        player.WeaponChanged += OnPlayerWeaponChanged;
        player.DamageDealt += OnPlayerDamageDealt;
    }

    private void DisconnectPlayerSignals(MapPlayer? player)
    {
        if (player == null || !IsInstanceValid(player)) return;
        player.HealthChanged -= OnPlayerHealthChanged;
        player.Combat.ArmourChanged -= OnPlayerArmourChanged;
        player.Died -= OnPlayerDied;
        player.Respawned -= OnPlayerRespawned;
        player.WeaponChanged -= OnPlayerWeaponChanged;
        player.DamageDealt -= OnPlayerDamageDealt;
    }

    private void OnPlayerArmourChanged(int oldArmour, int newArmour)
    {
        _statusBarDirty = true;
        if (!IsInstanceValid(this) || !IsInsideTree() || _statusBarRoot == null) return;
        UpdateStatusBar();
    }

    private void OnPlayerHealthChanged(int oldHealth, int newHealth)
    {
        _observedHealth = newHealth;
        _statusBarDirty = true;
        if (!IsInstanceValid(this) || !IsInsideTree() || _statusBarRoot == null) return;
        UpdateStatusBar();
        UpdateCrosshair();
    }

    private void OnPlayerDied()
    {
        _observedDead = true;
        _observedHealth = 0;
        _statusBarDirty = true;
        if (!IsInstanceValid(this) || !IsInsideTree() || _statusBarRoot == null) return;
        UpdateStatusBar();
        UpdateCrosshair();
    }

    private void OnPlayerRespawned()
    {
        _observedDead = false;
        if (_player != null)
        {
            _observedHealth = _player.Health;
            _observedGun = (Gun)_player.GunSelect;
        }
        _statusBarDirty = true;
        if (!IsInstanceValid(this) || !IsInsideTree() || _statusBarRoot == null) return;
        UpdateStatusBar();
        UpdateCrosshair();
    }

    private void OnPlayerWeaponChanged(int gunId)
    {
        _observedGun = (Gun)gunId;
        _statusBarDirty = true;
        if (!IsInstanceValid(this) || !IsInsideTree() || _statusBarRoot == null) return;
        UpdateStatusBar();
    }

    private void OnPlayerDamageDealt(int targetId, int damage)
    {
        if (!IsInstanceValid(this) || !IsInsideTree() || _crosshairRect == null) return;
        UpdateCrosshair();
    }

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

    /// <summary>Current match/engine elapsed timestamp in milliseconds (engine lastmillis). If negative, falls back to Player.LastMillis (the shared world clock).</summary>
    public int LastMillis { get; set; } = -1;

    // --- HUD Score Display (fpsgame/scoreboard.cpp:484-568) ---

    /// <summary>Whether the in-game HUD score is displayed (VARP hudscore in scoreboard.cpp:484).</summary>
    public bool HudScore { get; set; } = true;

    /// <summary>HUD score scale multiplier (FVARP hudscorescale in scoreboard.cpp:485, default 1.0).</summary>
    public float HudScoreScale { get; set; } = 1.0f;

    /// <summary>HUD score horizontal alignment (-1 = left, 0 = center, 1 = right) (VARP hudscorealign in scoreboard.cpp:486, default 0).</summary>
    public int HudScoreAlign { get; set; } = 0;

    /// <summary>HUD score normalized screen X position (FVARP hudscorex in scoreboard.cpp:487, default 0.50).</summary>
    public float HudScoreX { get; set; } = 0.50f;

    /// <summary>HUD score normalized screen Y position (FVARP hudscorey in scoreboard.cpp:488, default 0.03).</summary>
    public float HudScoreY { get; set; } = 0.03f;

    /// <summary>HUD score player/friendly team color (HVARP hudscoreplayercolour in scoreboard.cpp:489, default 0x60A0FF).</summary>
    public uint HudScorePlayerColour { get; set; } = 0x60A0FF;

    /// <summary>HUD score enemy team color (HVARP hudscoreenemycolour in scoreboard.cpp:490, default 0xFF4040).</summary>
    public uint HudScoreEnemyColour { get; set; } = 0xFF4040;

    /// <summary>HUD score alpha transparency (VARP hudscorealpha in scoreboard.cpp:491, default 255).</summary>
    public int HudScoreAlpha { get; set; } = 255;

    /// <summary>HUD score separation spacing between scores (VARP hudscoresep in scoreboard.cpp:492, default 200).</summary>
    public float HudScoreSep { get; set; } = 200.0f;

    /// <summary>Primary score to display (player's team or best score).</summary>
    public int Score1 { get; set; } = int.MinValue;

    /// <summary>Secondary score to display (enemy team or 2nd best score).</summary>
    public int Score2 { get; set; } = int.MinValue;

    /// <summary>Whether Score1 is the local player's / friendly team's score.</summary>
    public bool Score1IsPlayer { get; set; } = true;

    // --- Spectator & Edit Mode HUD ---

    /// <summary>Whether spectator overlay should be drawn (or driven by Player.Entity.State == CsSpectator).</summary>
    public bool IsSpectator { get; set; }

    /// <summary>Name of target player currently followed in spectator mode (followingplayer()).</summary>
    public string? FollowingPlayerName { get; set; }

    /// <summary>Privilege level of followed target player (0 = none, 1 = master, 2 = auth, 3 = admin).</summary>
    public int FollowingPlayerPrivilege { get; set; }

    /// <summary>Whether followed target player is dead.</summary>
    public bool FollowingPlayerDead { get; set; }

    /// <summary>Base color for followed target player's name (default 0xFFFFFF).</summary>
    public uint FollowingPlayerColor { get; set; } = 0xFFFFFF;

    /// <summary>Whether the editor mode HUD is active.</summary>
    public bool IsEditMode { get; set; }

    /// <summary>Current editor grid size (cube units).</summary>
    public int EditGridSize { get; set; } = 8;

    /// <summary>Current editor grid power (gridsize = 1 &lt;&lt; power).</summary>
    public int EditGridPower { get; set; } = 3;

    /// <summary>Count of selected children in the edit selection (mirrors selchildcount in octaedit.cpp).</summary>
    public int EditSelChildCount { get; set; }

    /// <summary>Active selection material (mirrors selchildmat in octaedit.cpp).</summary>
    public ushort EditSelChildMat { get; set; }

    /// <summary>Whether to show material in cube selection string (showmat in Sauerbraten).</summary>
    public bool EditShowMat { get; set; } = true;

    /// <summary>Whether allfaces mode is enabled in the editor.</summary>
    public bool EditAllFaces { get; set; }

    /// <summary>Selection position in the editor for quick stats.</summary>
    public Vector3I? EditSelPos { get; set; }

    /// <summary>Optional quick edit stats / status string override (or auto-formatted if null).</summary>
    public string? EditQuickStats { get; set; }

    // Quick ammo hud nodes container
    private Control? _ammoHudRoot;
    private readonly System.Collections.Generic.List<TextureRect> _ammoHudIcons = new();

    // Ammo bar nodes container
    private Control? _ammoBarRoot;
    private readonly System.Collections.Generic.List<(TextureRect Icon, Label Label)> _ammoBarSlots = new();

    // Game clock / match timer node
    private Label? _gameClockLabel;

    // HUD score nodes
    private Label? _hudScoreLabel1;
    private Label? _hudScoreLabel2;

    // Spectator overlay nodes
    private Control? _spectatorRoot;
    private Label? _spectatorLabel;
    private Label? _followingLabel;

    // Edit mode HUD nodes
    private Control? _editHudRoot;
    private Label? _editCubeLabel;
    private Label? _editStatsLabel;

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
        SetupHudScoreNodes();
        SetupSpectatorNodes();
        SetupEditHudNodes();
        UpdateAtlasTextures();
        if (_player != null)
        {
            UpdateStatusBar();
            UpdateCrosshair();
        }
    }

    public override void _ExitTree()
    {
        DisconnectPlayerSignals(_player);
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

    private void SetupHudScoreNodes()
    {
        _hudScoreLabel1 = new Label
        {
            Name = "HudScoreLabel1",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        AddChild(_hudScoreLabel1);

        _hudScoreLabel2 = new Label
        {
            Name = "HudScoreLabel2",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        AddChild(_hudScoreLabel2);
    }

    private void SetupSpectatorNodes()
    {
        _spectatorRoot = new Control
        {
            Name = "SpectatorHud",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        AddChild(_spectatorRoot);

        _spectatorLabel = new Label
        {
            Name = "SpectatorLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            Text = "SPECTATOR",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _spectatorRoot.AddChild(_spectatorLabel);

        _followingLabel = new Label
        {
            Name = "FollowingLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _spectatorRoot.AddChild(_followingLabel);
    }

    private void SetupEditHudNodes()
    {
        _editHudRoot = new Control
        {
            Name = "EditHud",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        AddChild(_editHudRoot);

        _editCubeLabel = new Label
        {
            Name = "EditCubeLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        _editHudRoot.AddChild(_editCubeLabel);

        _editStatsLabel = new Label
        {
            Name = "EditStatsLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        _editHudRoot.AddChild(_editStatsLabel);
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
        if (ShouldRefreshStatusBar())
        {
            UpdateStatusBar();
        }
        UpdateGameClock();
        UpdateHudScore();
        UpdateSpectatorOverlay();
        UpdateEditHud();
    }

    private bool ShouldRefreshStatusBar()
    {
        if (_statusBarDirty) return true;
        if (_player == null) return false;
        if (GetViewportRect().Size != _lastViewportSize) return true;
        if (_player.Combat.SelectedAmmo != _lastSelectedAmmo) return true;
        if (_player.Combat.Armour != _lastArmour) return true;
        if ((_player.Combat.QuadMillis > 0) != _lastQuadActive) return true;
        if (_player.Flags != null && _player.Flags.Flags.Count > 0) return true;
        return false;
    }

    /// <summary>Updates crosshair position, texture, size and modulation.</summary>
    public void UpdateCrosshair()
    {
        if (_crosshairRect == null || Player == null) return;

        Vector2 viewportSize = GetViewportRect().Size;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0) return;

        int nowMillis = Player.LastMillis;

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

        bool isDead = Player.Entity.Dead;
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

            if (Player.Flags != null)
            {
                bool carrying = false;
                int heldTeam = 0;
                int holdRemaining = 0;
                for (int i = 0; i < Player.Flags.Flags.Count; i++)
                {
                    var f = Player.Flags.Flags[i];
                    if (f.Owner == Player.PlayerId)
                    {
                        carrying = true;
                        heldTeam = f.Team;
                        int now = Player.LastMillis;
                        holdRemaining = Math.Max(0, Game.FlagManager.HoldSecs - f.HoldCounter(now) / 1000);
                        break;
                    }
                }
                HasFlag = carrying;
                HeldFlagTeam = heldTeam;
                HoldRemainingSeconds = holdRemaining;
                if (Player.Mode != null)
                {
                    IsHoldMode = Player.Mode.Has(Game.ModeFlags.Hold);
                }
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

        _statusBarDirty = false;
        _lastViewportSize = viewportSize;
        _lastSelectedAmmo = Player.Combat.SelectedAmmo;
        _lastArmour = Player.Combat.Armour;
        _lastQuadActive = Player.Combat.QuadMillis > 0;
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
            : (Player?.LastMillis ?? 0);

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

    /// <summary>
    /// Updates the top HUD team/match score display (engine <c>fpsgame/scoreboard.cpp:484-568: drawhudscore</c>).
    /// </summary>
    public void UpdateHudScore()
    {
        if (_hudScoreLabel1 == null || _hudScoreLabel2 == null) return;

        if (Player?.Flags != null)
        {
            int playerTeam = Player.Team;
            int enemyTeam = playerTeam == 1 ? 2 : 1;
            Score1 = Player.Flags.GetScore(playerTeam);
            Score2 = Player.Flags.GetScore(enemyTeam);
            Score1IsPlayer = true;
        }

        if (!HudScore || Score1 == int.MinValue)
        {
            _hudScoreLabel1.Visible = false;
            _hudScoreLabel2.Visible = false;
            return;
        }

        Vector2 viewportSize = GetViewportRect().Size;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0) return;

        string text1 = Score1.ToString();
        string text2 = Score2 != int.MinValue ? Score2.ToString() : "";

        var (pos1, size1, pos2, size2) = StatusBarRenderer.LayoutHudScore(
            viewportSize.X,
            viewportSize.Y,
            text1,
            text2,
            HudScoreX,
            HudScoreY,
            HudScoreScale,
            HudScoreAlign,
            HudScoreSep);

        int fontSize = Mathf.Max(12, (int)(StatusBarRenderer.DefaultFontHeight * HudScoreScale));

        uint rgb1 = Score1IsPlayer ? HudScorePlayerColour : HudScoreEnemyColour;
        Color col1 = StatusBarRenderer.GetHudScoreColor(rgb1, HudScoreAlpha);
        _hudScoreLabel1.Visible = true;
        _hudScoreLabel1.Text = text1;
        _hudScoreLabel1.Modulate = col1;
        _hudScoreLabel1.Position = pos1;
        _hudScoreLabel1.Size = size1;
        _hudScoreLabel1.AddThemeFontSizeOverride("font_size", fontSize);
        _hudScoreLabel1.HorizontalAlignment = HorizontalAlignment.Center;
        _hudScoreLabel1.VerticalAlignment = VerticalAlignment.Center;

        if (Score2 != int.MinValue)
        {
            uint rgb2 = !Score1IsPlayer ? HudScorePlayerColour : HudScoreEnemyColour;
            Color col2 = StatusBarRenderer.GetHudScoreColor(rgb2, HudScoreAlpha);
            _hudScoreLabel2.Visible = true;
            _hudScoreLabel2.Text = text2;
            _hudScoreLabel2.Modulate = col2;
            _hudScoreLabel2.Position = pos2;
            _hudScoreLabel2.Size = size2;
            _hudScoreLabel2.AddThemeFontSizeOverride("font_size", fontSize);
            _hudScoreLabel2.HorizontalAlignment = HorizontalAlignment.Center;
            _hudScoreLabel2.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            _hudScoreLabel2.Visible = false;
        }
    }

    /// <summary>
    /// Updates spectator mode overlay ("SPECTATOR" and followed player name)
    /// matching engine <c>fps.cpp:1064-1077</c>.
    /// </summary>
    public void UpdateSpectatorOverlay()
    {
        if (_spectatorRoot == null || _spectatorLabel == null || _followingLabel == null) return;

        bool isSpec = IsSpectator || (Player != null && Player.Entity.State == PhysEnt.CsSpectator);
        if (!isSpec)
        {
            _spectatorRoot.Visible = false;
            return;
        }

        Vector2 viewportSize = GetViewportRect().Size;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0) return;

        _spectatorRoot.Visible = true;

        float scale = viewportSize.Y / StatusBarRenderer.ReferenceHeight;
        int fontSize = Mathf.Max(12, (int)(StatusBarRenderer.DefaultFontHeight * scale));

        string targetName = FollowingPlayerName ?? "";
        var (specPos, specSize, followPos, followSize) = StatusBarRenderer.LayoutSpectatorOverlay(
            viewportSize.X,
            viewportSize.Y,
            targetName,
            StatusBarRenderer.ReferenceHeight);

        _spectatorLabel.Position = specPos;
        _spectatorLabel.Size = specSize;
        _spectatorLabel.AddThemeFontSizeOverride("font_size", fontSize);

        if (!string.IsNullOrEmpty(targetName))
        {
            _followingLabel.Visible = true;
            _followingLabel.Text = targetName;
            _followingLabel.Position = followPos;
            _followingLabel.Size = followSize;
            _followingLabel.AddThemeFontSizeOverride("font_size", fontSize);

            Color targetColor = StatusBarRenderer.GetStatusColor(FollowingPlayerPrivilege, FollowingPlayerDead, FollowingPlayerColor);
            _followingLabel.Modulate = targetColor;
        }
        else
        {
            _followingLabel.Visible = false;
        }
    }

    /// <summary>
    /// Updates edit mode HUD (cube count, material, and quick stats)
    /// matching engine <c>rendergl.cpp:2310-2327</c>.
    /// </summary>
    public void UpdateEditHud()
    {
        if (_editHudRoot == null || _editCubeLabel == null || _editStatsLabel == null) return;

        bool isEdit = IsEditMode || (Player != null && Player.Entity.State == PhysEnt.CsEditing);
        if (!isEdit)
        {
            _editHudRoot.Visible = false;
            return;
        }

        Vector2 viewportSize = GetViewportRect().Size;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0) return;

        _editHudRoot.Visible = true;

        float scale = viewportSize.Y / StatusBarRenderer.ReferenceHeight;
        int fontSize = Mathf.Max(12, (int)(StatusBarRenderer.DefaultFontHeight * scale));

        // Format cube line
        string cubeText = StatusBarRenderer.FormatEditCube(EditSelChildCount, EditSelChildMat, EditShowMat);
        _editCubeLabel.Text = cubeText;
        _editCubeLabel.AddThemeFontSizeOverride("font_size", fontSize);

        // Format quick edit stats / status
        string statsText = EditQuickStats ?? StatusBarRenderer.FormatEditQuickStats(EditGridSize, EditGridPower, EditAllFaces, EditSelPos);
        _editStatsLabel.Text = statsText;
        _editStatsLabel.AddThemeFontSizeOverride("font_size", fontSize);

        // Layout above bottom status bar matching rendergl.cpp:2310-2327:
        // Positioned at X = FONTH / 2
        // cubeText is abovehud - FONTH
        // quick stats / edithud is abovehud - max(th, FONTH)
        float fontH = StatusBarRenderer.DefaultFontHeight * scale;
        float marginX = fontH / 2.0f;
        float bottomY = StatusBarRenderer.IconY * scale;

        Vector2 statsTextSize = _editStatsLabel.GetCombinedMinimumSize();
        float statsHeight = Math.Max(statsTextSize.Y, fontH);

        float statsY = bottomY - fontH - statsHeight;
        float cubeY = bottomY - fontH;

        _editStatsLabel.Position = new Vector2(marginX, statsY);
        _editStatsLabel.Size = new Vector2(viewportSize.X - marginX * 2, statsHeight);

        _editCubeLabel.Position = new Vector2(marginX, cubeY);
        _editCubeLabel.Size = new Vector2(viewportSize.X - marginX * 2, fontH);
    }
}

