using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Runtime;
using SauerbratenRevival.World.Weapons;
using HudgunModel = SauerbratenRevival.World.Models.HudgunModel;
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

    private bool _built;
#if TOOLS
    private Camera3D? _frameCamera;
    private Vector3 _frameTarget;
#endif

    public override void _Ready()
    {
        if (_built) return;
        _built = true;

        UserSettings userSettings = UserSettings.Load();
        // The game mode only applies to an actual play session; the edit viewer
        // leaves Mode unset so the raw mode-entity representation is shown (see
        // ModeEntityBuilder.AppliesToMode).
        Game.GameModeInfo? modeInfo = SpawnPlayer && !EditMode ? Game.GameModeCatalog.Get(GameMode) : null;

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
                RadianceSettings = new Lighting.RadianceSettings { Mode = userSettings.LightingMode },
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

        (Vector3 feet, float yaw) = mapSource.FindSpawn();
        // The player node owns its team (set in the scene); the world only needs a
        // reference to it so flag visuals can colour relative to the local team.
        if (mapSource is IGameWorld gw) gw.LocalPlayer = player;
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
        if (isEdit)
        {
            player.EditMode = true;
            var editor = mapSource.WorldNode.GetNodeOrNull<Editor.EditorController>("EditorController")
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
            // Seeded from the world's simulation clock, which the game clock is compared against.
            MapLimit = (mapSource.Clock?.NowMillis ?? 0) + 10 * 60 * 1000
        };
        hud.SetupTextures(mapSource.Textures ?? new Materials.ResourceTextureSource());
        canvas.AddChild(hud);
        AddChild(canvas);

        GD.Print($"[viewer] player spawned at {player.Entity.O} yaw {player.Entity.Yaw} (edit={isEdit})");
    }

    /// <summary>
    /// Attach a converted body model to the player (engine <c>playermodels</c>, see
	/// render.cpp). Leaving <see cref="PlayerModelIndex"/> negative keeps the scene's
	/// placeholder body. The model is parented under the world as a <b>sibling</b> of
	/// the player so <see cref="Game.Players.MapPlayer.SyncBodyModel"/> can keep it upright at the
	/// feet, independent of the camera view pitch.
	/// </summary>
	private void AttachPlayerModel(Game.Players.MapPlayer player, IMapSource mapSource)
    {
        if (PlayerModelIndex < 0) return;

        var gun = (Gun)player.Combat.GunSelect;

        var body = new PlayerModel { Name = "PlayerModel" };
        mapSource.WorldNode.AddChild(body);
        player.PlayerModel = body;
        player.HidePlaceholderBody = true;

        // Derive both models from the one gun selection: the first-person HUD gun is
        // <mdl.hudguns>/<guns[].file> and the third-person view weapon is vweps[]
        // (render.cpp:425, :158). SelectWeapon then keeps them in sync for the rest of
        // the session — it is the single source of truth for the equipped gun.
        body.Configure(mapSource.Models, mapSource.Textures, PlayerModelIndex, player.Team,
            viewWeapon: WeaponCatalog.Vwep(gun));
        player.SyncBodyModelNow();
        var hudgun = new HudgunModel { Name = "Hudgun" };
        hudgun.Configure(mapSource.Models, mapSource.Textures, PlayerModelIndex, WeaponCatalog.Get(gun).File);
        if (player.GetNodeOrNull<Camera3D>("Camera3D") is { } camera)
        {
            camera.AddChild(hudgun);
            player.Hudgun = hudgun;
        }
        player.SelectWeapon(gun);
        GD.Print($"[viewer] player model '{body.ModelPath}' (index {PlayerModelIndex}, team {player.Team}) loaded={body.ModelLoaded} animated={body.Animated} weapon={body.ViewWeaponPath ?? "none"}");
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
}
