using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Lighting;

namespace SauerbratenRevival;

/// <summary>
/// Full-screen settings view matching the Doom-style wireframe layout:
/// - Breadcrumb header ("MAIN MENU / # SETTINGS")
/// - Navigation tabs ("CONTROLS", "VIDEO", "AUDIO")
/// - Categorized scrollable sections with custom rows:
///     - Toggle checkboxes with On/Off labels
///     - Horizontal range sliders with numeric readouts
///     - Dropdown select menus
///     - Highlighted/focused row states with dynamic description tooltip at the bottom
/// - Bottom actions: [ BACK ], [ RESTORE DEFAULTS ], [ PROFILE ]
/// All values round-trip through <see cref="UserSettings"/> and apply live to the engine.
/// </summary>
public partial class SettingsMenu : CanvasLayer
{
    /// <summary>Emitted when the player requests to close the settings view.</summary>
    [Signal]
    public delegate void CloseRequestedEventHandler();

    /// <summary>Emitted whenever any setting value changes and is persisted.</summary>
    [Signal]
    public delegate void SettingsChangedEventHandler();

    private static readonly (int Width, int Height)[] StandardResolutions =
    {
        (1024, 768),
        (1280, 720),
        (1366, 768),
        (1600, 900),
        (1920, 1080),
        (2560, 1440),
        (3840, 2160),
    };

    private const string DefaultTooltip = "Hover or select a setting to view details.";

    // Active state
    private UserSettings _settings = new();
    private bool _suppressEvents;
    private readonly List<Action> _rowUnfocusActions = new();

    // Node references
    private Button _controlsTab = null!;
    private Button _videoTab = null!;
    private Button _audioTab = null!;

    private ScrollContainer _scrollContainer = null!;
    private Control _controlsSection = null!;
    private Control _videoSection = null!;
    private Control _audioSection = null!;

    // Controls section controls
    private HSlider _sensitivitySlider = null!;
    private Label _sensitivityValue = null!;
    private HSlider _sensitivityScaleSlider = null!;
    private Label _sensitivityScaleValue = null!;
    private HSlider _mouseAccelSlider = null!;
    private Label _mouseAccelValue = null!;
    private CheckBox _invertMouseCheck = null!;
    private Label _invertMouseStatus = null!;

    // Video section controls
    private OptionButton _screenModeOption = null!;
    private OptionButton _resolutionOption = null!;
    private CheckBox _vsyncCheck = null!;
    private Label _vsyncStatus = null!;
    private HSlider _maxFpsSlider = null!;
    private Label _maxFpsValue = null!;
    private HSlider _fovSlider = null!;
    private Label _fovValue = null!;
    private OptionButton _lightingModeOption = null!;

    // Audio section controls
    private CheckBox _soundEnabledCheck = null!;
    private Label _soundEnabledStatus = null!;
    private HSlider _soundVolumeSlider = null!;
    private Label _soundVolumeValue = null!;

    // Bottom area
    private Label _tooltipLabel = null!;
    private Button _backButton = null!;
    private Button _restoreDefaultsButton = null!;
    private Button _profileButton = null!;

    // Styleboxes for rows
    private StyleBoxFlat _rowNormalStyle = null!;
    private StyleBoxFlat _rowFocusedStyle = null!;
    private StyleBoxFlat _tabActiveStyle = null!;
    private StyleBoxFlat _tabInactiveStyle = null!;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;

        InitStyles();
        ResolveNodes();
        InitDropdownItems();
        WireSignals();

        // Default closed
        Visible = false;
    }

    private void InitStyles()
    {
        _rowNormalStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.082f, 0.094f, 0.122f, 0.85f),
            BorderColor = new Color(0.18f, 0.204f, 0.259f, 0.8f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4,
            ContentMarginLeft = 16,
            ContentMarginTop = 8,
            ContentMarginRight = 16,
            ContentMarginBottom = 8,
        };

        _rowFocusedStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.157f, 0.173f, 0.224f, 0.95f),
            BorderColor = new Color(0.863f, 0.451f, 0.161f, 1.0f), // Sauerbraten accent orange
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4,
            ContentMarginLeft = 16,
            ContentMarginTop = 8,
            ContentMarginRight = 16,
            ContentMarginBottom = 8,
        };

        _tabActiveStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.16f, 0.18f, 0.23f, 1f),
            BorderColor = new Color(0.863f, 0.451f, 0.161f, 1f),
            BorderWidthBottom = 3,
            ContentMarginLeft = 20,
            ContentMarginTop = 10,
            ContentMarginRight = 20,
            ContentMarginBottom = 10,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
        };

        _tabInactiveStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.10f, 0.13f, 0.6f),
            BorderColor = Colors.Transparent,
            ContentMarginLeft = 20,
            ContentMarginTop = 10,
            ContentMarginRight = 20,
            ContentMarginBottom = 10,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
        };
    }

    private void ResolveNodes()
    {
        _controlsTab = GetNode<Button>("%ControlsTab");
        _videoTab = GetNode<Button>("%VideoTab");
        _audioTab = GetNode<Button>("%AudioTab");

        _scrollContainer = GetNode<ScrollContainer>("%SettingsScroll");
        _controlsSection = GetNode<Control>("%ControlsSection");
        _videoSection = GetNode<Control>("%VideoSection");
        _audioSection = GetNode<Control>("%AudioSection");

        // Controls
        _sensitivitySlider = GetNode<HSlider>("%SensitivitySlider");
        _sensitivityValue = GetNode<Label>("%SensitivityValue");
        _sensitivityScaleSlider = GetNode<HSlider>("%SensitivityScaleSlider");
        _sensitivityScaleValue = GetNode<Label>("%SensitivityScaleValue");
        _mouseAccelSlider = GetNode<HSlider>("%MouseAccelSlider");
        _mouseAccelValue = GetNode<Label>("%MouseAccelValue");
        _invertMouseCheck = GetNode<CheckBox>("%InvertMouseCheck");
        _invertMouseStatus = GetNode<Label>("%InvertMouseStatus");

        // Video
        _screenModeOption = GetNode<OptionButton>("%ScreenModeOption");
        _resolutionOption = GetNode<OptionButton>("%ResolutionOption");
        _vsyncCheck = GetNode<CheckBox>("%VsyncCheck");
        _vsyncStatus = GetNode<Label>("%VsyncStatus");
        _maxFpsSlider = GetNode<HSlider>("%MaxFpsSlider");
        _maxFpsValue = GetNode<Label>("%MaxFpsValue");
        _fovSlider = GetNode<HSlider>("%FovSlider");
        _fovValue = GetNode<Label>("%FovValue");
        _lightingModeOption = GetNode<OptionButton>("%LightingModeOption");

        // Audio
        _soundEnabledCheck = GetNode<CheckBox>("%SoundEnabledCheck");
        _soundEnabledStatus = GetNode<Label>("%SoundEnabledStatus");
        _soundVolumeSlider = GetNode<HSlider>("%SoundVolumeSlider");
        _soundVolumeValue = GetNode<Label>("%SoundVolumeValue");

        // Bottom
        _tooltipLabel = GetNode<Label>("%TooltipLabel");
        _backButton = GetNode<Button>("%BackButton");
        _restoreDefaultsButton = GetNode<Button>("%RestoreDefaultsButton");
        _profileButton = GetNode<Button>("%ProfileButton");
    }

    private void InitDropdownItems()
    {
        // Display Mode
        _screenModeOption.Clear();
        _screenModeOption.AddItem("WINDOWED", (int)ScreenMode.Windowed);
        _screenModeOption.AddItem("EXCLUSIVE FULLSCREEN", (int)ScreenMode.ExclusiveFullscreen);
        _screenModeOption.AddItem("BORDERLESS FULLSCREEN", (int)ScreenMode.BorderlessFullscreen);

        // Lighting Mode
        _lightingModeOption.Clear();
        _lightingModeOption.AddItem("RADIANCE HINTS (HIGH QUALITY)", (int)LightingQualityMode.RadianceHints);
        _lightingModeOption.AddItem("STATIC AMBIENT (PERFORMANCE)", (int)LightingQualityMode.StaticAmbient);
    }

    private void WireSignals()
    {
        // Tab clicks
        _controlsTab.Pressed += () => ScrollToSection(_controlsSection, _controlsTab);
        _videoTab.Pressed += () => ScrollToSection(_videoSection, _videoTab);
        _audioTab.Pressed += () => ScrollToSection(_audioSection, _audioTab);

        // Track scroll position to update active tab
        _scrollContainer.GetVScrollBar().ValueChanged += _ => OnScrollUpdated();

        // Wire row interactive controls
        _sensitivitySlider.ValueChanged += val =>
        {
            if (_suppressEvents) return;
            _sensitivityValue.Text = val.ToString("0.00");
            MutateSettings(s => s with { Sensitivity = (float)val });
        };

        _sensitivityScaleSlider.ValueChanged += val =>
        {
            if (_suppressEvents) return;
            _sensitivityScaleValue.Text = val.ToString("0.00");
            MutateSettings(s => s with { SensitivityScale = (float)val });
        };

        _mouseAccelSlider.ValueChanged += val =>
        {
            if (_suppressEvents) return;
            _mouseAccelValue.Text = val.ToString("0.00");
            MutateSettings(s => s with { MouseAccel = (float)val });
        };

        _invertMouseCheck.Toggled += pressed =>
        {
            if (_suppressEvents) return;
            _invertMouseStatus.Text = pressed ? "On" : "Off";
            MutateSettings(s => s with { InvertMouse = pressed });
        };

        _screenModeOption.ItemSelected += idx =>
        {
            if (_suppressEvents) return;
            var mode = (ScreenMode)_screenModeOption.GetItemId((int)idx);
            MutateSettings(s => s with { ScreenMode = mode });
        };

        _resolutionOption.ItemSelected += idx =>
        {
            if (_suppressEvents) return;
            int itemId = _resolutionOption.GetItemId((int)idx);
            if (itemId >= 0 && itemId < StandardResolutions.Length)
            {
                var (w, h) = StandardResolutions[itemId];
                MutateSettings(s => s with { WindowWidth = w, WindowHeight = h });
            }
        };

        _vsyncCheck.Toggled += pressed =>
        {
            if (_suppressEvents) return;
            _vsyncStatus.Text = pressed ? "On" : "Off";
            MutateSettings(s => s with { Vsync = pressed });
        };

        _maxFpsSlider.ValueChanged += val =>
        {
            if (_suppressEvents) return;
            int fps = (int)val;
            _maxFpsValue.Text = fps == 0 ? "Unlimited" : $"{fps} FPS";
            MutateSettings(s => s with { MaxFps = fps });
        };

        _fovSlider.ValueChanged += val =>
        {
            if (_suppressEvents) return;
            _fovValue.Text = $"{Mathf.RoundToInt((float)val)}°";
            MutateSettings(s => s with { Fov = (float)val });
        };

        _lightingModeOption.ItemSelected += idx =>
        {
            if (_suppressEvents) return;
            var mode = (LightingQualityMode)_lightingModeOption.GetItemId((int)idx);
            MutateSettings(s => s with { LightingMode = mode });
        };

        _soundEnabledCheck.Toggled += pressed =>
        {
            if (_suppressEvents) return;
            _soundEnabledStatus.Text = pressed ? "On" : "Off";
            MutateSettings(s => s with { SoundEnabled = pressed });
        };

        _soundVolumeSlider.ValueChanged += val =>
        {
            if (_suppressEvents) return;
            int vol = (int)val;
            int pct = Mathf.RoundToInt((float)vol / GameAudio.MaxSoundVol * 100f);
            _soundVolumeValue.Text = $"{pct}%";
            MutateSettings(s => s with { SoundVolume = vol });
        };

        // Bottom actions
        _backButton.Pressed += Close;
        _restoreDefaultsButton.Pressed += RestoreDefaults;
        _profileButton.Pressed += () =>
        {
            _tooltipLabel.Text = "Settings profile: Local config stored at user://settings.cfg";
        };

        // Setup hover and focus highlights for each row
        SetupRow(GetNode<PanelContainer>("%Row_Sensitivity"), GetNode<Label>("%SensitivityLabel"),
            "Mouse Sensitivity", "Base mouse sensitivity for looking and aiming (engine sensitivity).",
            _sensitivitySlider);

        SetupRow(GetNode<PanelContainer>("%Row_SensitivityScale"), GetNode<Label>("%SensitivityScaleLabel"),
            "Sensitivity Scale", "Multiplier applied to mouse look speed (engine sensitivityscale).",
            _sensitivityScaleSlider);

        SetupRow(GetNode<PanelContainer>("%Row_MouseAccel"), GetNode<Label>("%MouseAccelLabel"),
            "Mouse Acceleration", "Additional look speed scaling proportional to mouse movement speed (0 disables).",
            _mouseAccelSlider);

        SetupRow(GetNode<PanelContainer>("%Row_InvertMouse"), GetNode<Label>("%InvertMouseLabel"),
            "Invert Mouse", "Invert the vertical look axis (pitch).",
            _invertMouseCheck);

        SetupRow(GetNode<PanelContainer>("%Row_DisplayMode"), GetNode<Label>("%DisplayModeLabel"),
            "Display Mode", "Window presentation mode: Windowed, Exclusive Fullscreen, or Borderless.",
            _screenModeOption);

        SetupRow(GetNode<PanelContainer>("%Row_Resolution"), GetNode<Label>("%ResolutionLabel"),
            "Resolution", "Windowed display resolution in physical pixels.",
            _resolutionOption);

        SetupRow(GetNode<PanelContainer>("%Row_Vsync"), GetNode<Label>("%VsyncLabel"),
            "V-Sync", "Synchronize frame rate with the monitor refresh rate to eliminate tearing.",
            _vsyncCheck);

        SetupRow(GetNode<PanelContainer>("%Row_MaxFps"), GetNode<Label>("%MaxFpsLabel"),
            "Max Frame Rate", "Maximum frames per second cap (0 = unlimited).",
            _maxFpsSlider);

        SetupRow(GetNode<PanelContainer>("%Row_Fov"), GetNode<Label>("%FovLabel"),
            "Field of View", "Horizontal camera field of view in degrees.",
            _fovSlider);

        SetupRow(GetNode<PanelContainer>("%Row_Lighting"), GetNode<Label>("%LightingLabel"),
            "Lighting Quality", "World lighting mode (real-time radiance hints or lightweight static ambient).",
            _lightingModeOption);

        SetupRow(GetNode<PanelContainer>("%Row_SoundEnabled"), GetNode<Label>("%SoundEnabledLabel"),
            "Sound Enabled", "Master toggle for game audio effects and ambient sounds.",
            _soundEnabledCheck);

        SetupRow(GetNode<PanelContainer>("%Row_SoundVolume"), GetNode<Label>("%SoundVolumeLabel"),
            "Master Volume", "Master volume level for all game sound channels.",
            _soundVolumeSlider);
    }

    private void SetupRow(PanelContainer rowPanel, Label nameLabel, string title, string description, Control focusTarget)
    {
        rowPanel.AddThemeStyleboxOverride("panel", _rowNormalStyle);
        nameLabel.Text = $"  {title}";

        void OnFocus()
        {
            // Clear any other active highlight
            foreach (Action unfocus in _rowUnfocusActions) unfocus();

            rowPanel.AddThemeStyleboxOverride("panel", _rowFocusedStyle);
            nameLabel.Text = $"> {title}";
            nameLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 1f));
            _tooltipLabel.Text = description;
        }

        void OnUnfocus()
        {
            rowPanel.AddThemeStyleboxOverride("panel", _rowNormalStyle);
            nameLabel.Text = $"  {title}";
            nameLabel.RemoveThemeColorOverride("font_color");
        }

        _rowUnfocusActions.Add(OnUnfocus);

        rowPanel.MouseEntered += OnFocus;
        rowPanel.MouseExited += () =>
        {
            if (!focusTarget.HasFocus()) OnUnfocus();
        };

        focusTarget.FocusEntered += OnFocus;
        focusTarget.FocusExited += OnUnfocus;
    }

    /// <summary>Open and display the settings view.</summary>
    public void Open()
    {
        Visible = true;
        _tooltipLabel.Text = DefaultTooltip;
        RefreshFromSettings();
        SetActiveTab(_controlsTab);
        _controlsTab.GrabFocus();
    }

    /// <summary>Close the settings view and emit <see cref="CloseRequested"/>.</summary>
    public void Close()
    {
        Visible = false;
        EmitSignal(SignalName.CloseRequested);
    }

    /// <summary>Reset all settings to engine defaults, write to disk, apply live, and update UI.</summary>
    public void RestoreDefaults()
    {
        var def = new UserSettings();
        def.Save();
        _settings = def;

        ApplyLive(_settings);
        PopulateControls(_settings);

        _tooltipLabel.Text = "Settings restored to factory defaults.";
        EmitSignal(SignalName.SettingsChanged);
    }

    /// <summary>Read persistent settings from disk and update all UI controls.</summary>
    public void RefreshFromSettings()
    {
        _settings = UserSettings.Load();
        PopulateControls(_settings);
    }

    private void PopulateControls(UserSettings s)
    {
        _suppressEvents = true;
        try
        {
            // Controls
            _sensitivitySlider.Value = s.Sensitivity;
            _sensitivityValue.Text = s.Sensitivity.ToString("0.00");

            _sensitivityScaleSlider.Value = s.SensitivityScale;
            _sensitivityScaleValue.Text = s.SensitivityScale.ToString("0.00");

            _mouseAccelSlider.Value = s.MouseAccel;
            _mouseAccelValue.Text = s.MouseAccel.ToString("0.00");

            _invertMouseCheck.ButtonPressed = s.InvertMouse;
            _invertMouseStatus.Text = s.InvertMouse ? "On" : "Off";

            // Video - Display Mode
            for (int i = 0; i < _screenModeOption.ItemCount; i++)
            {
                if (_screenModeOption.GetItemId(i) == (int)s.ScreenMode)
                {
                    _screenModeOption.Select(i);
                    break;
                }
            }

            // Video - Resolutions
            PopulateResolutionDropdown(s.WindowWidth, s.WindowHeight);

            // Video - Vsync
            _vsyncCheck.ButtonPressed = s.Vsync;
            _vsyncStatus.Text = s.Vsync ? "On" : "Off";

            // Video - Max FPS
            _maxFpsSlider.Value = s.MaxFps;
            _maxFpsValue.Text = s.MaxFps == 0 ? "Unlimited" : $"{s.MaxFps} FPS";

            // Video - FOV
            _fovSlider.Value = s.Fov;
            _fovValue.Text = $"{Mathf.RoundToInt(s.Fov)}°";

            // Video - Lighting Mode
            for (int i = 0; i < _lightingModeOption.ItemCount; i++)
            {
                if (_lightingModeOption.GetItemId(i) == (int)s.LightingMode)
                {
                    _lightingModeOption.Select(i);
                    break;
                }
            }

            // Audio - Sound
            _soundEnabledCheck.ButtonPressed = s.SoundEnabled;
            _soundEnabledStatus.Text = s.SoundEnabled ? "On" : "Off";

            // Audio - Volume
            _soundVolumeSlider.Value = s.SoundVolume;
            int pct = Mathf.RoundToInt((float)s.SoundVolume / GameAudio.MaxSoundVol * 100f);
            _soundVolumeValue.Text = $"{pct}%";
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void PopulateResolutionDropdown(int currentW, int currentH)
    {
        _resolutionOption.Clear();
        int selectedIndex = -1;

        for (int i = 0; i < StandardResolutions.Length; i++)
        {
            var (w, h) = StandardResolutions[i];
            _resolutionOption.AddItem($"{w} x {h}", i);
            if (w == currentW && h == currentH) selectedIndex = i;
        }

        if (selectedIndex == -1)
        {
            // Custom resolution not in standard list
            int customId = StandardResolutions.Length;
            _resolutionOption.AddItem($"{currentW} x {currentH} (Custom)", customId);
            _resolutionOption.Select(_resolutionOption.ItemCount - 1);
        }
        else
        {
            _resolutionOption.Select(selectedIndex);
        }
    }

    private void MutateSettings(Func<UserSettings, UserSettings> update)
    {
        _settings = update(_settings);
        _settings.Save();
        ApplyLive(_settings);
        EmitSignal(SignalName.SettingsChanged);
    }

    private void ApplyLive(UserSettings s)
    {
        ScreenSettings.Apply(s);
        GameAudio.Apply(s.SoundEnabled, s.SoundVolume);

        // If MapPlayer exists in tree, push updated look settings
        if (GetTree().Root.FindChild("Player", true, false) is MapPlayer player)
        {
            player.ApplyLookSettings(s);
        }
    }

    // ---- Navigation / Tabs -------------------------------------------------

    private void ScrollToSection(Control section, Button activeTab)
    {
        SetActiveTab(activeTab);
        _scrollContainer.ScrollVertical = (int)section.Position.Y;
    }

    private void OnScrollUpdated()
    {
        int scrollY = _scrollContainer.ScrollVertical;
        float videoY = _videoSection.Position.Y - 20;
        float audioY = _audioSection.Position.Y - 20;

        if (scrollY >= audioY)
        {
            SetActiveTab(_audioTab);
        }
        else if (scrollY >= videoY)
        {
            SetActiveTab(_videoTab);
        }
        else
        {
            SetActiveTab(_controlsTab);
        }
    }

    private void SetActiveTab(Button activeTab)
    {
        Button[] tabs = { _controlsTab, _videoTab, _audioTab };
        foreach (Button tab in tabs)
        {
            bool isActive = tab == activeTab;
            tab.AddThemeStyleboxOverride("normal", isActive ? _tabActiveStyle : _tabInactiveStyle);
            tab.AddThemeStyleboxOverride("hover", isActive ? _tabActiveStyle : _tabInactiveStyle);
            tab.AddThemeStyleboxOverride("pressed", _tabActiveStyle);
            tab.AddThemeStyleboxOverride("focus", _tabActiveStyle);
            tab.AddThemeColorOverride("font_color", isActive ? new Color(1f, 1f, 1f, 1f) : new Color(0.65f, 0.68f, 0.74f, 1f));
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible) return;

        if (@event.IsActionPressed("ui_cancel") && !@event.IsEcho())
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
