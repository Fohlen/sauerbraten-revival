using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Runtime;
using SauerbratenRevival.World.Weapons;
using HudgunModel = SauerbratenRevival.World.Models.HudgunModel;
using ModelCache = SauerbratenRevival.World.Models.ModelCache;
using PlayerModel = SauerbratenRevival.World.Models.PlayerModel;

namespace SauerbratenRevival.World.Runtime.Formats.Ogz;

/// <summary>
/// Scene root that builds a legacy <c>.ogz</c> via <see cref="MapWorld"/> and
/// applies its sky, leaving the camera/controller to the scene. The editor scene
/// attaches a <see cref="FreeFlyCamera"/>; the player scene attaches its own
/// physics controller (see <c>scenes/editor_scene.tscn</c> /
/// <c>scenes/player_scene.tscn</c>).
///
/// When <see cref="FrameCamera"/> is set, the direct-child node named
/// <c>Camera3D</c> is placed at a full-map overview once the world is built. A
/// controller whose camera is nested (e.g. under a player) is left alone.
///
/// It reads OGZ at runtime; classic maps are shipped and loaded this way in both
/// the viewer and the play scene. See docs/adr/0006-ogz-import.md.
///
/// It is a <c>[Tool]</c> script so the geometry also appears in the editor. Tool
/// scripts only rebuild when the scene is (re)opened, so after editing this script
/// close and reopen the scene (or restart the editor).
/// </summary>
[Tool]
[GlobalClass]
public partial class OgzMapViewer : Node3D
{
    /// <summary>Override map to build. Absolute, or package-relative (e.g. <c>base/church51.ogz</c>), resolved through <see cref="Content.ContentRoots.ResolveMap"/>; empty uses the default.</summary>
    [Export] public string OgzPath { get; set; } = "";
    [Export] public int ChunkSize { get; set; } = 512;

    /// <summary>Place the direct-child <c>Camera3D</c> at a full-map overview once built.</summary>
    [Export] public bool FrameCamera { get; set; } = true;

    /// <summary>Attach the direct-child <see cref="Game.Players.MapPlayer"/> to the world's octree and place it at a spawn point.</summary>
    [Export] public bool SpawnPlayer { get; set; } = false;

    /// <summary>When true, spawns player in coop-edit mode with an active EditorController.</summary>
    [Export] public bool EditMode { get; set; } = false;

    /// <summary>Player model table index to load (render.cpp <c>playermodel</c> 0..4); negative keeps the placeholder body.</summary>
    [Export] public int PlayerModelIndex { get; set; } = -1;

    /// <summary>Active game mode identifier (e.g. "ffa", "ctf", "effic_ctf", "insta_ctf").</summary>
    [Export] public string GameMode { get; set; } = "ffa";

    /// <summary>Pre-loaded demo package to play back through the local player and world.</summary>
    public DemoPackage? DemoToPlay { get; set; }

    /// <summary>Active demo replayer driving playback, if any.</summary>
    public Demo.DemoReplayer? ActiveReplayer { get; private set; }

    /// <summary>Currently followed player during demo playback.</summary>
    public Game.Players.MapPlayer? FollowedPlayer { get; private set; }

    private readonly List<Game.Players.MapPlayer> _demoPlayers = new();
    private int _followedIndex;
    private Camera3D? _playerCamera;
    private HudgunModel? _sharedHudgun;
    private World.Hud.HudController? _hud;

    /// <summary>When true, automatically begins recording a demo on player spawn.</summary>
    [Export] public bool AutoRecordDemo { get; set; } = false;

    /// <summary>Custom path for auto-recording demo. Empty uses default user://demos/ naming schema.</summary>
    [Export] public string AutoRecordDemoPath { get; set; } = "";

    /// <summary>
    /// Show edit-mode blue entity markers and facing arrows. When false (e.g. in
    /// player/game scenes), all <see cref="EntityMarker"/> nodes are hidden.
    /// </summary>
    [Export]
    public bool ShowEntityMarkers
    {
        get => _showEntityMarkers;
        set
        {
            _showEntityMarkers = value;
            if (World != null)
            {
                World.ShowEntityMarkers = value;
            }
        }
    }
    private bool _showEntityMarkers = true;

    /// <summary>The built world (geometry + octree), once <see cref="_Ready"/> has run.</summary>
    public MapWorld? World { get; private set; }

    /// <summary>The active map source supplying octree, geometry, and entities.</summary>
    public IMapSource? MapSource => World ?? _customSource;
    private IMapSource? _customSource;

    /// <summary>
    /// Optional progress callback invoked during stepped map loading (ADR 0011 §5).
    /// When null, map building executes synchronously.
    /// </summary>
    public Func<float, string, Task>? ProgressCallback { get; set; }

    /// <summary>
    /// Event fired when asynchronous map initialization and player spawning finishes.
    /// </summary>
    public event Action? MatchReady;

    private bool _built;
#if TOOLS
    private Camera3D? _frameCamera;
    private Vector3 _frameTarget;
#endif

    public override void _Ready()
    {
        if (_built) return;
        _built = true;

        if (!Engine.IsEditorHint() && DemoToPlay == null)
        {
            string[] rawArgs = OS.GetCmdlineUserArgs().Length > 0 ? OS.GetCmdlineUserArgs() : OS.GetCmdlineArgs();
            var cli = SauerbratenRevival.World.Demo.DemoCommandLine.Parse(rawArgs);
            if (!string.IsNullOrEmpty(cli.DemoPath))
            {
                string? resolved = SauerbratenRevival.World.Content.ContentRoots.TryResolveDemo(cli.DemoPath);
                if (resolved != null)
                {
                    try
                    {
                        DemoToPlay = DemoPackage.Load(resolved);
                        if (!string.IsNullOrWhiteSpace(DemoToPlay.MapName))
                        {
                            OgzPath = DemoToPlay.MapName;
                        }
                        if (!string.IsNullOrWhiteSpace(DemoToPlay.GameMode))
                        {
                            GameMode = DemoToPlay.GameMode;
                        }
                        EditMode = false;
                    }
                    catch (Exception ex)
                    {
                        GD.PushError($"[viewer] Failed to load demo '{cli.DemoPath}': {ex.Message}");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(cli.Map))
            {
                OgzPath = cli.Map;
            }
            if (!string.IsNullOrWhiteSpace(cli.Mode))
            {
                var cliModeInfo = GameModeCatalog.TryGet(cli.Mode);
                GameMode = cliModeInfo?.Id ?? cli.Mode;
                if (cliModeInfo != null && cliModeInfo.Has(ModeFlags.Edit))
                {
                    EditMode = true;
                }
            }

            if (!AutoRecordDemo && cli.Record)
            {
                AutoRecordDemo = true;
                AutoRecordDemoPath = cli.RecordPath ?? "";
            }
        }

        UserSettings userSettings = UserSettings.Load();
        // The game mode only applies to an actual play session; the edit viewer
        // leaves Mode unset so the raw mode-entity representation is shown (see
        // ModeEntityBuilder.AppliesToMode).
        Game.GameModeInfo? modeInfo = SpawnPlayer && !EditMode ? Game.GameModeCatalog.Get(GameMode) : null;

        if (ProgressCallback == null && !Engine.IsEditorHint() && DisplayServer.GetName() != "headless" && !EditMode)
        {
            var loadScene = GD.Load<PackedScene>("res://scenes/map_load.tscn");
            if (loadScene != null)
            {
                var standaloneLoad = loadScene.Instantiate<MapLoad>();
                AddChild(standaloneLoad);
                standaloneLoad.StartLoading(OgzPath, GameMode);
                ProgressCallback = standaloneLoad.StepProgressAsync;
                MatchReady += async () =>
                {
                    await ToSignal(GetTree().CreateTimer(0.1f), SceneTreeTimer.SignalName.Timeout);
                    standaloneLoad.Dismiss();
                    standaloneLoad.QueueFree();
                };
            }
        }

        if (ProgressCallback != null)
        {
            _ = InitAsync(userSettings, modeInfo);
            return;
        }

        foreach (var child in GetChildren())
        {
            if (child is IMapSource ms)
            {
                _customSource = ms;
                break;
            }
        }

        if (_customSource != null)
        {
            _customSource.Build();
            World = _customSource as MapWorld;
        }
        else
        {
            World = new MapWorld
            {
                Name = "MapWorld",
                OgzPath = OgzPath,
                ChunkSize = ChunkSize,
                ShowEntityMarkers = ShowEntityMarkers || EditMode,
                Mode = modeInfo,
                // Lighting quality is a user setting (`user://settings.cfg`).
                RadianceSettings = new Lighting.LightingSettings { Mode = userSettings.LightingMode },
            };
            AddChild(World);
            World.Build();
        }

        var mapSource = MapSource;
        if (mapSource == null || !mapSource.Built) return; // missing map, error already pushed

        if (World?.Map != null && World.Textures != null)
            ApplySky(World.Map, World.Textures);
        // Runtime only: spawning a player reparents scene-authored nodes, so never
        // mutate the edited scene from the Godot editor viewport.
        if (!Engine.IsEditorHint() && SpawnPlayer) AttachPlayer(userSettings, modeInfo, mapSource);
        if (FrameCamera && !SpawnPlayer) FrameSceneCamera(mapSource.Centre, mapSource.Radius);
    }

    private async Task InitAsync(UserSettings userSettings, Game.GameModeInfo? modeInfo)
    {
        foreach (var child in GetChildren())
        {
            if (child is IMapSource ms)
            {
                _customSource = ms;
                break;
            }
        }

        if (_customSource != null)
        {
            await _customSource.BuildAsync(ProgressCallback);
            World = _customSource as MapWorld;
        }
        else
        {
            World = new MapWorld
            {
                Name = "MapWorld",
                OgzPath = OgzPath,
                ChunkSize = ChunkSize,
                ShowEntityMarkers = ShowEntityMarkers || EditMode,
                Mode = modeInfo,
                RadianceSettings = new Lighting.LightingSettings { Mode = userSettings.LightingMode },
                ProgressCallback = ProgressCallback,
            };
            AddChild(World);
            await World.BuildAsync(ProgressCallback);
        }

        var mapSource = MapSource;
        if (mapSource == null || !mapSource.Built)
        {
            MatchReady?.Invoke();
            return;
        }

        if (World?.Map != null && World.Textures != null)
            ApplySky(World.Map, World.Textures);

        // Step 5: Spawning Player & Match Start (+20%)
        if (ProgressCallback != null)
        {
            await ProgressCallback(0.20f, "Spawning Player & Match Start");
        }

        if (!Engine.IsEditorHint() && SpawnPlayer) AttachPlayer(userSettings, modeInfo, mapSource);
        if (FrameCamera && !SpawnPlayer) FrameSceneCamera(mapSource.Centre, mapSource.Radius);

        MatchReady?.Invoke();
    }

    /// <summary>
    /// Move a scene-authored <see cref="Game.Players.MapPlayer"/> under the Z-up world node and
    /// initialise it against the octree at the map's first <c>ET_PLAYERSTART</c>
    /// (falling back to the map centre). Physics then owns its position/orientation.
    /// </summary>
    private void AttachPlayer(UserSettings userSettings, Game.GameModeInfo? modeInfo, IMapSource mapSource)
    {
        Game.Players.MapPlayer? player = null;
        foreach (var child in GetChildren())
            if (child is Game.Players.MapPlayer p) { player = p; break; }
        if (player == null)
        {
            GD.PushWarning($"[viewer] SpawnPlayer is set but no direct-child {nameof(Game.Players.MapPlayer)} node exists.");
            return;
        }

        player.Reparent(mapSource.WorldNode); // into the rotated Z-up world

        // The player node owns its team (set in the scene); the world only needs a
        // reference to it so flag visuals can colour relative to the local team.
        if (mapSource is IGameWorld gw)
        {
            gw.LocalPlayer = player;
            if (gw.Mode == null && modeInfo != null) gw.Mode = modeInfo;
        }
        if (modeInfo != null) player.Mode = modeInfo;

        int tag = Game.GameModeCatalog.GetSpawnGroup(modeInfo, player.Team);
        (Vector3 feet, float yaw) = mapSource.FindSpawn(tag: tag, player: player);
        var phys = mapSource.Physics ?? new Physics.OctaPhysics(mapSource.Octa ?? new Octa(10));
        player.Setup(phys, feet, yaw);
        mapSource.RegisterPlayer(player);
        player.ApplyLookSettings(userSettings); // sensitivity / accel / invert / FOV
        AttachPlayerModel(player, mapSource);

        var effects = new Effects.WeaponEffectsRenderer { Name = "WeaponEffects", Textures = mapSource.Textures };
        mapSource.WorldNode.AddChild(effects);
        mapSource.WeaponEffects = effects;
        player.WeaponEffects = effects;
        player.Items = mapSource.Items;
        player.Flags = mapSource.Flags;
        player.JumpPads = mapSource.JumpPads;
        player.Teleports = mapSource.Teleports;
        mapSource.Items?.SpawnItems(force: true);

        bool isEdit = EditMode || player.EditMode;
        Editor.EditorController? editor = null;
        if (isEdit)
        {
            player.EditMode = true;
            editor = mapSource.WorldNode.GetNodeOrNull<Editor.EditorController>("EditorController")
                ?? new Editor.EditorController { Name = "EditorController" };
            editor.BindMapSource(mapSource);
            if (editor.GetParent() == null)
                mapSource.WorldNode.AddChild(editor);
        }

        // Attach CanvasLayer and HUD controller
        var canvas = new CanvasLayer { Name = "HudCanvas" };
        var hud = new World.Hud.HudController
        {
            Name = "HUD",
            Player = player,
            IsEditMode = isEdit,
            // Default 10 minute match timer (10 * 60 * 1000 = 600000 ms) matching Sauerbraten standard maplimit.
            // In editor mode (coop edit / edit viewer), there is no match timer (untimed, -1).
            MapLimit = isEdit ? -1 : (mapSource.Clock?.NowMillis ?? 0) + 10 * 60 * 1000
        };
        hud.SetupTextures(mapSource.Textures ?? new Materials.ResourceTextureSource());
        canvas.AddChild(hud);
        AddChild(canvas);

        // The editor drives the edit HUD (grid size/power, selection cube count,
        // material, allfaces) every frame; without the link it only ever shows
        // the defaults from HudController.
        if (editor != null) editor.Hud = hud;

        _playerCamera = player.GetNodeOrNull<Camera3D>("Camera3D") ?? player.View.Camera;
        if (_playerCamera == null)
        {
            _playerCamera = new Camera3D { Name = "Camera3D" };
            player.AddChild(_playerCamera);
        }
        _hud = hud;

        GD.Print($"[viewer] player spawned at {player.Entity.O} yaw {player.Entity.Yaw} (edit={isEdit})");

        if (DemoToPlay != null)
        {
            var allPeers = new List<int>();
            if (DemoToPlay.Peers != null)
            {
                foreach (var p in DemoToPlay.Peers)
                {
                    if (!allPeers.Contains(p.PeerId)) allPeers.Add(p.PeerId);
                }
            }
            if (DemoToPlay.Commands != null)
            {
                foreach (var cmd in DemoToPlay.Commands)
                {
                    if (cmd.PeerId >= 0 && !allPeers.Contains(cmd.PeerId))
                    {
                        allPeers.Add(cmd.PeerId);
                    }
                    if (cmd.Op == WorldOp.PlayerDeath && cmd.PlayerDeath != null)
                    {
                        int vic = cmd.PlayerDeath.VictimPeerId;
                        if (vic >= 0 && !allPeers.Contains(vic)) allPeers.Add(vic);
                    }
                }
            }
            allPeers.Sort();

            if (allPeers.Count > 0)
            {
                int primaryPeerId = allPeers[0];
                player.PlayerId = primaryPeerId;
                Format.DemoPeerInfo? primaryPeerInfo = null;
                if (DemoToPlay.Peers != null)
                {
                    foreach (var p in DemoToPlay.Peers)
                    {
                        if (p.PeerId == primaryPeerId) { primaryPeerInfo = p; break; }
                    }
                }
                if (primaryPeerInfo != null)
                {
                    player.Name = primaryPeerInfo.Name;
                    if (!string.IsNullOrWhiteSpace(primaryPeerInfo.Team)) player.TeamName = primaryPeerInfo.Team;
                    if (primaryPeerInfo.Playermodel >= 0) player.PlayerModelIndex = primaryPeerInfo.Playermodel;
                }
            }

            _demoPlayers.Clear();
            _demoPlayers.Add(player);

            ActiveReplayer = new Demo.DemoReplayer(DemoToPlay, mapSource.Sink);
            ActiveReplayer.ApplyInitialPlacement(player);
            ActiveReplayer.Attach(player);

            SpawnRemoteDemoPlayers(mapSource, player, allPeers);

            if (mapSource is IGameWorld gameWorld)
            {
                ActiveReplayer.AttachWorld(gameWorld);
            }

            _followedIndex = 0;
            FollowPlayer(player);

            ActiveReplayer.Connect(
                Demo.DemoReplayer.SignalName.PlaybackFinished,
                Callable.From(() =>
                {
                    GD.Print($"[demo] playback finished ({DemoToPlay.DurationSeconds:F1}s, {DemoToPlay.TotalTicks} ticks)");
                }),
                (uint)ConnectFlags.OneShot);
            GD.Print($"[viewer] demo playback active ({DemoToPlay.TotalTicks} ticks, {DemoToPlay.TotalCommands} commands, {_demoPlayers.Count} players)");
        }
        else if (AutoRecordDemo)
        {
            player.StartDemoRecording(string.IsNullOrWhiteSpace(AutoRecordDemoPath) ? null : AutoRecordDemoPath);
        }
    }

    private void SpawnRemoteDemoPlayers(IMapSource mapSource, Game.Players.MapPlayer localPlayer, List<int> allPeers)
    {
        if (DemoToPlay == null || ActiveReplayer == null) return;

        foreach (int peerId in allPeers)
        {
            if (peerId == localPlayer.PlayerId) continue;

            Format.DemoPeerInfo? peerInfo = null;
            if (DemoToPlay.Peers != null)
            {
                foreach (var p in DemoToPlay.Peers)
                {
                    if (p.PeerId == peerId) { peerInfo = p; break; }
                }
            }

            var remotePlayer = new Game.Players.MapPlayer
            {
                Name = peerInfo?.Name ?? $"Player_{peerId}",
                PlayerId = peerId,
                Mode = localPlayer.Mode
            };

            if (peerInfo != null && !string.IsNullOrWhiteSpace(peerInfo.Team))
            {
                remotePlayer.TeamName = peerInfo.Team;
            }

            mapSource.WorldNode.AddChild(remotePlayer);

            var phys = mapSource.Physics ?? new Physics.OctaPhysics(mapSource.Octa ?? new Octa(10));
            remotePlayer.Setup(phys, Vector3.Zero, 0f);
            mapSource.RegisterPlayer(remotePlayer);

            remotePlayer.WeaponEffects = mapSource.WeaponEffects;
            remotePlayer.Items = mapSource.Items;
            remotePlayer.Flags = mapSource.Flags;
            remotePlayer.JumpPads = mapSource.JumpPads;
            remotePlayer.Teleports = mapSource.Teleports;

            int mdlIdx = (peerInfo != null && peerInfo.Playermodel >= 0) ? peerInfo.Playermodel : (PlayerModelIndex >= 0 ? PlayerModelIndex : 0);
            remotePlayer.PlayerModelIndex = mdlIdx;
            AttachPlayerModel(remotePlayer, mapSource, mdlIdx);

            _demoPlayers.Add(remotePlayer);
            ActiveReplayer.Attach(remotePlayer);
            GD.Print($"[viewer] spawned remote demo player '{remotePlayer.Name}' (peer {peerId}, team {remotePlayer.Team}, model {mdlIdx})");
        }
    }

    /// <summary>
    /// Switches camera view and HUD focus to follow a specific player during demo playback.
    /// In first person, the followed player's 3D body is hidden and hudgun is shown.
    /// All other players in the world show their 3D body models.
    /// </summary>
    public void FollowPlayer(Game.Players.MapPlayer target)
    {
        if (target == null) return;
        if (_playerCamera == null)
        {
            _playerCamera = target.GetNodeOrNull<Camera3D>("Camera3D") ?? target.View.Camera;
            if (_playerCamera == null)
            {
                _playerCamera = new Camera3D { Name = "Camera3D" };
                target.AddChild(_playerCamera);
            }
        }
        FollowedPlayer = target;

        for (int i = 0; i < _demoPlayers.Count; i++)
        {
            var p = _demoPlayers[i];
            if (p != target)
            {
                p.View.Camera = null;
                p.Hudgun = null;
                if (p.PlayerModel != null)
                {
                    p.PlayerModel.Visible = true;
                }
            }
        }

        if (target.PlayerModel != null)
        {
            target.PlayerModel.Visible = false;
        }

        if (_playerCamera.GetParent() != target)
        {
            _playerCamera.Reparent(target);
        }
        _playerCamera.Position = Vector3.Zero;
        _playerCamera.Basis = Basis.Identity;
        _playerCamera.Current = true;
        target.View.Camera = _playerCamera;

        if (_sharedHudgun != null)
        {
            if (_sharedHudgun.GetParent() == null)
            {
                _playerCamera.AddChild(_sharedHudgun);
            }
            else if (_sharedHudgun.GetParent() != _playerCamera)
            {
                _sharedHudgun.Reparent(_playerCamera);
            }
            target.Hudgun = _sharedHudgun;
            int mdlIdx = (target.PlayerModelIndex >= 0) ? target.PlayerModelIndex : (PlayerModelIndex >= 0 ? PlayerModelIndex : 0);
            _sharedHudgun.Configure(MapSource?.Models, MapSource?.Textures, mdlIdx, WeaponCatalog.Get((Gun)target.Combat.GunSelect).File, target.Team);
        }

        target.View.SyncCamera();
        target.View.SyncTransform();

        if (_hud != null)
        {
            _hud.Player = target;
            _hud.FollowingPlayerName = target.Name;
            _hud.IsSpectator = true;
        }

        GD.Print($"[viewer] following player '{target.Name}' (peer {target.PlayerId}, team {target.Team})");
    }

    /// <summary>
    /// Cycles to the next or previous followed player in the demo (matching engine nextfollow).
    /// </summary>
    public void NextFollow(int dir = 1)
    {
        if (_demoPlayers.Count <= 1) return;
        _followedIndex = (_followedIndex + dir + _demoPlayers.Count) % _demoPlayers.Count;
        FollowPlayer(_demoPlayers[_followedIndex]);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (DemoToPlay != null && _demoPlayers.Count > 1)
        {
            if (@event is InputEventMouseButton mb && mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    NextFollow(1);
                    GetViewport()?.SetInputAsHandled();
                }
                else if (mb.ButtonIndex == MouseButton.Right)
                {
                    NextFollow(-1);
                    GetViewport()?.SetInputAsHandled();
                }
            }
        }
    }

    /// <summary>
    /// Attach a converted body model to the player (engine <c>playermodels</c>, see
	/// render.cpp). Leaving <see cref="PlayerModelIndex"/> negative keeps the scene's
	/// placeholder body. The model is parented under the world as a <b>sibling</b> of
	/// the player so <see cref="Game.Players.MapPlayer.SyncBodyModel"/> can keep it upright at the
	/// feet, independent of the camera view pitch.
	/// </summary>
    private void AttachPlayerModel(Game.Players.MapPlayer player, IMapSource mapSource, int modelIndex = -1)
    {
        int mdlIdx = modelIndex >= 0 ? modelIndex : PlayerModelIndex;
        if (mdlIdx < 0) return;

        ModelCache.PreloadPlayermodel(mdlIdx, mapSource.Models, mapSource.Textures, player.Team);

        var gun = (Gun)player.Combat.GunSelect;

        var body = new PlayerModel { Name = $"PlayerModel_{player.PlayerId}" };
        mapSource.WorldNode.AddChild(body);
        player.PlayerModel = body;
        player.HidePlaceholderBody = true;

        // Derive both models from the one gun selection: the first-person HUD gun is
        // <mdl.hudguns>/<guns[].file> and the third-person view weapon is vweps[]
        // (render.cpp:425, :158). SelectWeapon then keeps them in sync for the rest of
        // the session — it is the single source of truth for the equipped gun.
        body.Configure(mapSource.Models, mapSource.Textures, mdlIdx, player.Team,
            viewWeapon: WeaponCatalog.Vwep(gun));
        player.SyncBodyModelNow();
        if (player.GetNodeOrNull<Camera3D>("Camera3D") is { } camera)
        {
            var hudgun = new HudgunModel { Name = "Hudgun" };
            hudgun.Configure(mapSource.Models, mapSource.Textures, mdlIdx, WeaponCatalog.Get(gun).File, player.Team);
            camera.AddChild(hudgun);
            player.Hudgun = hudgun;
            _sharedHudgun = hudgun;
        }
        player.SelectWeapon(gun);
        GD.Print($"[viewer] player model '{body.ModelPath}' (index {mdlIdx}, team {player.Team}) loaded={body.ModelLoaded} animated={body.Animated} weapon={body.ViewWeaponPath ?? "none"}");
    }


    private void ApplySky(OgzMap map, ITextureSource textures)
    {
        // Runtime only: don't dirty the edited scene's environment in the IDE.
        if (Engine.IsEditorHint()) return;
        var sky = SkyBuilder.Build(textures, map.GetVarString("skybox"));
        if (sky == null) return;
        var worldEnvironment = GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        if (worldEnvironment == null) return;
        worldEnvironment.Environment ??= new Godot.Environment();
        worldEnvironment.Environment.Sky = sky;
        worldEnvironment.Environment.BackgroundMode = Godot.Environment.BGMode.Sky;
    }


    private void FrameSceneCamera(Vector3 centre, float radius)
    {
        var camera = GetNodeOrNull<Camera3D>("Camera3D");
        if (camera == null) return;

        camera.Position = centre + new Vector3(0, radius * 0.6f, radius * 1.3f);
        camera.LookAt(centre, Vector3.Up);
        if (!Engine.IsEditorHint()) camera.Current = true;

#if TOOLS
        if (Engine.IsEditorHint())
        {
            _frameCamera = camera;
            _frameTarget = centre;
            CallDeferred(nameof(FrameEditorViewport));
        }
#endif
    }

#if TOOLS
    private void FrameEditorViewport()
    {
        // Move the editor's 3D viewport camera onto the scene camera so the
        // geometry is visible immediately when the scene is opened.
        if (_frameCamera == null) return;
        var viewport = EditorInterface.Singleton.GetEditorViewport3D(0);
        var editorCamera = viewport?.GetCamera3D();
        if (editorCamera == null) return;
        editorCamera.GlobalPosition = _frameCamera.GlobalPosition;
        editorCamera.LookAt(_frameTarget, Vector3.Up);
    }
#endif

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationPredelete || what == Window.NotificationWMCloseRequest)
        {
            World?.Dispose();
            World = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            World?.Dispose();
            World = null;
        }
        base.Dispose(disposing);
    }
}
