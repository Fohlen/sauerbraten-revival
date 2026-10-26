using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Content;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Models;

namespace SauerbratenRevival;

/// <summary>
/// Prototype front-end: the main menu shown at startup and as an ESC overlay on
/// top of a paused match. It owns no world state — it only lets the player pick a
/// map/mode/team/playermodel and emits <see cref="StartMatchEventHandler"/>; the
/// shell (<see cref="Main"/>) instantiates the play scene.
///
/// The map picker follows the engine's per-mode <c>MapList_*</c> pools from
/// <c>data/lists.cfg</c> via <see cref="MapListCatalog"/> (CTF modes only offer
/// CTF maps, capture/hold modes the capture pool, the rest the deathmatch pool).
///
/// The right-hand "stage" is a self-contained <see cref="SubViewport"/> with its
/// own 3D world, so the character preview does not show or disturb the game world
/// when the menu is an overlay.
/// </summary>
public partial class MainMenu : CanvasLayer
{
    /// <summary>Emitted when the player wants to start a match.</summary>
    /// <param name="map">Package-relative map, e.g. <c>base/metl3</c> (no extension resolution by the viewer).</param>
    /// <param name="mode">Game mode id understood by <see cref="GameModeCatalog"/>.</param>
    /// <param name="team">0 = FFA, 1 = blue, 2 = red.</param>
    /// <param name="playerModel">Engine playermodel index 0..4.</param>
    /// <param name="playerName">Display name typed into the floating nametag (not yet used in-game).</param>
    [Signal]
    public delegate void StartMatchEventHandler(string map, string mode, int team, int playerModel, string playerName);

    /// <summary>Emitted when a paused match should resume (or the menu should close).</summary>
    [Signal]
    public delegate void ResumeRequestedEventHandler();

    /// <summary>Emitted when the player asks to quit the application.</summary>
    [Signal]
    public delegate void QuitRequestedEventHandler();

    /// <summary>Mode ids offered under the CASUAL tab.</summary>
    private static readonly string[] CasualModes = { "ffa", "insta", "insta_ctf", "effic_ctf" };

    /// <summary>Mode ids offered under the COMPETITIVE tab.</summary>
    private static readonly string[] CompModes = { "effic_ctf", "ctf", "insta_ctf", "effic_team" };

    /// <summary>Display names for <see cref="Playermodels"/> indices.</summary>
    private static readonly string[] ModelNames = { "MR. FIXIT", "SNOUTX10K", "OGRO", "INKY", "CAPTAIN CANNON" };

    private readonly List<string> _maps = new(); // current mode's map pool (engine MapList_*)
    private string _mapListId = "";       // pool id the selection index belongs to
    private int _mapIndex;
    private int _modeIndex = 2;          // insta ctf under CASUAL
    private bool _competitive;
    private int _team = 1;               // blue
    private int _modelIndex;
    private float _stageYaw = 180f;      // start roughly facing the camera
    private bool _dragging;
    private bool _previewBuilt;

    private ModelCatalog? _models;
    private ITextureSource? _textures;
    private PlayerModel? _preview;
    private readonly Dictionary<string, Material> _skinCache = new();

    // Node references (resolved in _Ready).
    private Label _netStatus = null!;
    private Button _settingsButton = null!;
    private Button _quitButton = null!;
    private Button _settingsBackButton = null!;
    private Button _soundToggle = null!;
    private PanelContainer _settingsPanel = null!;

    private Button _casualButton = null!;
    private Button _compButton = null!;
    private Button _resumeButton = null!;
    private Button _quickJoinButton = null!;

    private Button _modePrevButton = null!;
    private Button _modeNextButton = null!;
    private Label _modeValue = null!;

    private Button _mapPrevButton = null!;
    private Button _mapNextButton = null!;
    private Label _mapLabel = null!;
    private Label _mapValue = null!;
    private TextureRect _mapPreview = null!;

    private Control _dragCatcher = null!;
    private Node3D _stagePivot = null!;
    private Camera3D _camera = null!;
    private LineEdit _nametag = null!;

    private Button _modelPrevButton = null!;
    private Button _modelNextButton = null!;
    private Label _modelValue = null!;

    private Button _teamFfaButton = null!;
    private Button _teamBlueButton = null!;
    private Button _teamRedButton = null!;

    public override void _Ready()
    {
        // The menu must keep working while the game tree is paused (ESC overlay).
        ProcessMode = ProcessModeEnum.Always;
        ResolveNodes();
        WireSignals();
        RefreshAll();
    }

    private void ResolveNodes()
    {
        _netStatus = GetNode<Label>("%NetStatusLabel");
        _settingsButton = GetNode<Button>("%SettingsButton");
        _quitButton = GetNode<Button>("%QuitButton");
        _settingsPanel = GetNode<PanelContainer>("%SettingsPanel");
        _settingsBackButton = GetNode<Button>("%SettingsBackButton");
        _soundToggle = GetNode<Button>("%SoundToggle");

        _casualButton = GetNode<Button>("%CasualButton");
        _compButton = GetNode<Button>("%CompButton");
        _resumeButton = GetNode<Button>("%ResumeButton");
        _quickJoinButton = GetNode<Button>("%QuickJoinButton");

        _modePrevButton = GetNode<Button>("%ModePrevButton");
        _modeNextButton = GetNode<Button>("%ModeNextButton");
        _modeValue = GetNode<Label>("%ModeValueLabel");

        _mapPrevButton = GetNode<Button>("%MapPrevButton");
        _mapNextButton = GetNode<Button>("%MapNextButton");
        _mapLabel = GetNode<Label>("%MapLabel");
        _mapValue = GetNode<Label>("%MapValueLabel");
        _mapPreview = GetNode<TextureRect>("%MapPreview");

        _dragCatcher = GetNode<Control>("%DragCatcher");
        _stagePivot = GetNode<Node3D>("%StagePivot");
        _camera = GetNode<Camera3D>("%PreviewCamera");
        _nametag = GetNode<LineEdit>("%NametagEdit");

        _modelPrevButton = GetNode<Button>("%ModelPrevButton");
        _modelNextButton = GetNode<Button>("%ModelNextButton");
        _modelValue = GetNode<Label>("%ModelValueLabel");

        _teamFfaButton = GetNode<Button>("%TeamFfaButton");
        _teamBlueButton = GetNode<Button>("%TeamBlueButton");
        _teamRedButton = GetNode<Button>("%TeamRedButton");
    }

    private void WireSignals()
    {
        _casualButton.Toggled += pressed => { if (pressed) SetCompetitive(false); };
        _compButton.Toggled += pressed => { if (pressed) SetCompetitive(true); };

        _quickJoinButton.Pressed += Launch;
        _resumeButton.Pressed += () => EmitSignal(SignalName.ResumeRequested);

        _modePrevButton.Pressed += () => StepMode(-1);
        _modeNextButton.Pressed += () => StepMode(1);

        _mapPrevButton.Pressed += () => StepMap(-1);
        _mapNextButton.Pressed += () => StepMap(1);

        _modelPrevButton.Pressed += () => StepModel(-1);
        _modelNextButton.Pressed += () => StepModel(1);

        _teamFfaButton.Toggled += pressed => { if (pressed) SetTeam(0); };
        _teamBlueButton.Toggled += pressed => { if (pressed) SetTeam(1); };
        _teamRedButton.Toggled += pressed => { if (pressed) SetTeam(2); };

        _dragCatcher.GuiInput += OnStageInput;

        _settingsButton.Pressed += ToggleSettings;
        _settingsBackButton.Pressed += ToggleSettings;
        _soundToggle.Pressed += ToggleSound;
        _quitButton.Pressed += () => EmitSignal(SignalName.QuitRequested);
    }

    /// <summary>
    /// Show the menu. <paramref name="inGame"/> adds the "resume match" affordance
    /// (and flags the paused net status); the shell pauses the tree before calling.
    /// </summary>
    public void Open(bool inGame)
    {
        Visible = true;
        _resumeButton.Visible = inGame;
        _netStatus.Text = inGame ? "LOCAL  ·  MATCH PAUSED" : "LOCAL  ·  OFFLINE";
        if (!_previewBuilt) BuildPreview();
        RefreshAll();
        if (inGame) _resumeButton.GrabFocus();
    }

    /// <summary>Hide the menu (the shell resumes the game and releases the mouse).</summary>
    public void Close()
    {
        Visible = false;
        _settingsPanel.Visible = false;
    }

    // ---- selection ---------------------------------------------------------

    private string[] ActiveModes => _competitive ? CompModes : CasualModes;

    private string CurrentMode => ActiveModes[_modeIndex];

    private void SetCompetitive(bool competitive)
    {
        if (_competitive == competitive) return;
        _competitive = competitive;
        _modeIndex = 0;
        RefreshMode();
    }

    private void StepMode(int delta)
    {
        _modeIndex = Wrap(_modeIndex + delta, ActiveModes.Length);
        RefreshMode();
    }

    private void StepMap(int delta)
    {
        if (_maps.Count == 0) return;
        _mapIndex = Wrap(_mapIndex + delta, _maps.Count);
        RefreshMap();
    }

    private void StepModel(int delta)
    {
        _modelIndex = Wrap(_modelIndex + delta, Playermodels.All.Length);
        RefreshModel();
        ApplyPreviewModel();
    }

    private void SetTeam(int team)
    {
        if (_team == team) return;
        _team = team;
        ApplyPreviewModel();
    }

    private static int Wrap(int value, int length) => ((value % length) + length) % length;

    // ---- launching --------------------------------------------------------

    private void Launch()
    {
        if (_maps.Count == 0) return;

        string map = "base/" + _maps[_mapIndex];
        string mode = CurrentMode;

        // A team mode cannot run with the FFA skin choice; default to blue rather
        // than handing the mode an invalid team.
        ModeFlags flags = GameModeCatalog.TryGet(mode)?.Flags ?? ModeFlags.None;
        int team = (flags & ModeFlags.Team) != 0 && _team == 0 ? 1 : _team;

        string name = _nametag.Text.Trim();
        if (name.Length == 0) name = "PLAYER";

        GD.Print($"[menu] launching map={map} mode={mode} team={team} model={_modelIndex} name={name}");
        EmitSignal(SignalName.StartMatch, map, mode, team, _modelIndex, name);
    }

    // ---- preview stage ----------------------------------------------------

    private void BuildPreview()
    {
        _previewBuilt = true;
        _models = ModelCatalog.LoadFromProject();
        _textures = new ResourceTextureSource();

        _stagePivot.RotationDegrees = new Vector3(0f, _stageYaw, 0f);

        // Converted player models come out of the glTF importer Y-up, which is what
        // the stage viewport uses; the turntable pivot then yaws around screen up.
        _preview = new PlayerModel { Name = "PreviewModel" };
        _stagePivot.AddChild(_preview);
        ApplyPreviewModel();
        GD.Print($"[menu] preview model '{_preview.ModelPath}' (index {_modelIndex}, team {_team}) loaded={_preview.ModelLoaded} animated={_preview.Animated}");
    }

    private void ApplyPreviewModel()
    {
        if (_preview == null) return;
        _preview.Configure(_models, _textures, _modelIndex, _team, _skinCache);
        _preview.Play("idle");
        FramePreview();
    }

    /// <summary>
    /// Frame the camera to the freshly loaded model's bounds, so every playermodel
    /// (each has its own engine <c>mdlscale</c>) is presented at the same on-screen
    /// size with the head below the nametag and the feet above the selector bar.
    /// </summary>
    private void FramePreview()
    {
        if (_preview == null) return;
        _preview.Position = Vector3.Zero;

        bool any = false;
        Aabb bounds = default;
        foreach (Node node in _preview.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D { Mesh: not null } mesh) continue;
            Aabb world = mesh.GlobalTransform * mesh.GetAabb();
            bounds = any ? bounds.Merge(world) : world;
            any = true;
        }
        if (!any) return;

        // The model node's origin is at the character's feet (as in-game), but the
        // imported bind-pose AABB is centred on the model mid-height, so only its
        // height is used: aim the camera at half that height and back off enough
        // to leave room for the nametag above the head and the controls below the
        // feet (~50% of the viewport height; fov 34, KEEP_HEIGHT).
        float height = Mathf.Max(bounds.Size.Y, 1f);
        float distance = height * 3.4f;
        _camera.Position = new Vector3(0f, height * 0.5f, distance);
        _camera.RotationDegrees = Vector3.Zero;
    }

    /// <summary>Drag anywhere on the stage to spin the character (turntable).</summary>
    private void OnStageInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _dragging = button.Pressed;
                break;
            case InputEventMouseMotion motion when _dragging:
                _stageYaw += motion.Relative.X * 0.4f;
                _stagePivot.RotationDegrees = new Vector3(0f, _stageYaw, 0f);
                break;
        }
    }

    // ---- settings ---------------------------------------------------------

    private void ToggleSettings() => _settingsPanel.Visible = !_settingsPanel.Visible;

    private void ToggleSound()
    {
        UserSettings settings = UserSettings.Load();
        settings = settings with { SoundEnabled = !settings.SoundEnabled };
        settings.Save();
        GameAudio.Apply(settings.SoundEnabled, settings.SoundVolume);
        RefreshSound();
    }

    // ---- refresh ----------------------------------------------------------

    private void RefreshAll()
    {
        _casualButton.ButtonPressed = !_competitive;
        _compButton.ButtonPressed = _competitive;
        RefreshMode();
        RefreshModel();
        RefreshTeam();
        RefreshSound();
    }

    private void RefreshMode()
    {
        string id = CurrentMode;
        GameModeInfo? info = GameModeCatalog.TryGet(id);
        _modeValue.Text = (info?.Name ?? id).ToUpperInvariant();
        RefreshMapList(info);
    }

    /// <summary>
    /// Swap in the map pool the selected mode uses (engine <c>ML_ModeMaps</c> /
    /// <see cref="MapListCatalog.ForMode"/>) and restart the picker on a new pool.
    /// Stepping between two modes that share a pool (ctf → insta ctf) keeps the
    /// current map.
    /// </summary>
    private void RefreshMapList(GameModeInfo? mode)
    {
        MapList? list = MapListCatalog.ForMode(mode);
        string listId = list?.Id ?? "";
        bool changed = listId != _mapListId;
        _mapListId = listId;

        _maps.Clear();
        if (list != null) _maps.AddRange(list.Maps);
        if (changed) _mapIndex = 0;

        _mapLabel.Text = list == null
            ? "MAP"
            : $"MAP  ·  {list.Name.ToUpperInvariant()}  ·  {_maps.Count}";
        if (changed) GD.Print($"[menu] map pool '{listId}' ({_maps.Count} maps)");
        RefreshMap();
    }

    private void RefreshMap()
    {
        _quickJoinButton.Disabled = _maps.Count == 0;

        if (_maps.Count == 0)
        {
            _mapValue.Text = "NO MAPS";
            _mapPreview.Texture = null;
            return;
        }

        _mapIndex = Wrap(_mapIndex, _maps.Count);
        string map = _maps[_mapIndex];
        _mapValue.Text = map.ToUpperInvariant();
        _mapPreview.Texture = LoadMapThumbnail(map);
    }

    private void RefreshModel()
    {
        _modelValue.Text = ModelNames[_modelIndex];
    }

    private void RefreshTeam()
    {
        _teamFfaButton.ButtonPressed = _team == 0;
        _teamBlueButton.ButtonPressed = _team == 1;
        _teamRedButton.ButtonPressed = _team == 2;
    }

    private void RefreshSound()
    {
        _soundToggle.Text = UserSettings.Load().SoundEnabled ? "SOUND: ON" : "SOUND: OFF";
    }

    /// <summary>Load the map's shipped screenshot, preferring the imported resource.</summary>
    private static Texture2D? LoadMapThumbnail(string map)
    {
        string relative = $"base/{map}.jpg";
        string resPath = ContentRoots.ProjectPath(ContentKind.Map, relative);
        if (ResourceLoader.Exists(resPath)) return ResourceLoader.Load<Texture2D>(resPath);

        // Not imported (fresh checkout): read the raw image from whichever tree has it.
        string? diskPath = ContentRoots.TryResolve(ContentKind.Map, relative);
        if (diskPath == null) return null;
        var image = new Image();
        return image.Load(diskPath) == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
    }
}
