using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Ogz;

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
    /// <summary>Override map to build. Absolute, or relative to the reference dump's <c>packages/</c> dir (e.g. <c>base/church51.ogz</c>); empty uses the default.</summary>
    [Export] public string OgzPath { get; set; } = "";
    [Export] public int ChunkSize { get; set; } = 512;

    /// <summary>Place the direct-child <c>Camera3D</c> at a full-map overview once built.</summary>
    [Export] public bool FrameCamera { get; set; } = true;

    /// <summary>Attach the direct-child <see cref="MapPlayer"/> to the world's octree and place it at a spawn point.</summary>
    [Export] public bool SpawnPlayer { get; set; } = false;

    /// <summary>Player model table index to load (render.cpp <c>playermodel</c> 0..4); negative keeps the placeholder body.</summary>
    [Export] public int PlayerModelIndex { get; set; } = -1;

    /// <summary>Team skin for the player model (0 = FFA, 1 = blue, 2 = red).</summary>
    [Export] public int PlayerTeam { get; set; } = 0;

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
        World = new MapWorld
        {
            Name = "MapWorld",
            OgzPath = OgzPath,
            ChunkSize = ChunkSize,
            ShowEntityMarkers = ShowEntityMarkers,
            // Lighting quality is a user setting (`user://settings.cfg`).
            RadianceSettings = new Lighting.RadianceSettings { Mode = userSettings.LightingMode },
        };
        AddChild(World);
        World.Build();
        if (!World.Built) return; // missing map, error already pushed

        ApplySky(World.Map!, World.Textures!);
        if (SpawnPlayer) AttachPlayer(userSettings);
        if (FrameCamera) FrameSceneCamera(World.Centre, World.Radius);
    }

    /// <summary>
    /// Move a scene-authored <see cref="MapPlayer"/> under the Z-up world node and
    /// initialise it against the octree at the map's first <c>ET_PLAYERSTART</c>
    /// (falling back to the map centre). Physics then owns its position/orientation.
    /// </summary>
    private void AttachPlayer(UserSettings userSettings)
    {
        MapPlayer? player = null;
        foreach (var child in GetChildren())
            if (child is MapPlayer p) { player = p; break; }
        if (player == null)
        {
            GD.PushWarning($"[viewer] SpawnPlayer is set but no direct-child {nameof(MapPlayer)} node exists.");
            return;
        }

        player.Reparent(World!); // into the rotated Z-up world

        (Vector3 feet, float yaw) = FindSpawn(World!.Map!);
        player.Setup(World.Octa!, feet, yaw);
        player.ApplyLookSettings(userSettings); // sensitivity / accel / invert / FOV
        AttachPlayerModel(player);

        var effects = new Effects.WeaponEffectsRenderer { Name = "WeaponEffects" };
        World.AddChild(effects);
        player.WeaponEffects = effects;
        player.Items = World.Items;
        World.Items?.SpawnItems(Game.GameModeCatalog.Get("ffa"), force: true);

        // Attach CanvasLayer and HUD controller
        var canvas = new CanvasLayer { Name = "HudCanvas" };
        var hud = new World.Hud.HudController
        {
            Name = "HUD",
            Player = player,
            // Default 10 minute match timer (10 * 60 * 1000 = 600000 ms) matching Sauerbraten standard maplimit
            MapLimit = (int)Time.GetTicksMsec() + 10 * 60 * 1000
        };
        hud.SetupTextures(World.Textures ?? new Materials.ResourceTextureSource());
        canvas.AddChild(hud);
        AddChild(canvas);

        GD.Print($"[viewer] player spawned at {player.Entity.O} yaw {player.Entity.Yaw}");
    }

    /// <summary>
    /// Attach a converted body model to the player (engine <c>playermodels</c>, see
	/// render.cpp). Leaving <see cref="PlayerModelIndex"/> negative keeps the scene's
	/// placeholder body. The model is parented under the world as a <b>sibling</b> of
	/// the player so <see cref="MapPlayer.SyncBodyModel"/> can keep it upright at the
	/// feet, independent of the camera view pitch.
	/// </summary>
	private void AttachPlayerModel(MapPlayer player)
    {
        if (PlayerModelIndex < 0) return;

        var gun = (Gun)player.Combat.GunSelect;

        var body = new PlayerModel { Name = "PlayerModel" };
        World!.AddChild(body);
        player.PlayerModel = body;
        player.HidePlaceholderBody = true;

        // Derive both models from the one gun selection: the first-person HUD gun is
        // <mdl.hudguns>/<guns[].file> and the third-person view weapon is vweps[]
        // (render.cpp:425, :158). SelectWeapon then keeps them in sync for the rest of
        // the session — it is the single source of truth for the equipped gun.
        body.Configure(World.Models, World.Textures, PlayerModelIndex, PlayerTeam,
            viewWeapon: WeaponCatalog.Vwep(gun));
        player.SyncBodyModelNow();
        var hudgun = new HudgunModel { Name = "Hudgun" };
        hudgun.Configure(World.Models, World.Textures, PlayerModelIndex, WeaponCatalog.Get(gun).File);
        if (player.GetNodeOrNull<Camera3D>("Camera3D") is { } camera)
        {
            camera.AddChild(hudgun);
            player.Hudgun = hudgun;
        }
        player.SelectWeapon(gun);
        GD.Print($"[viewer] player model '{body.ModelPath}' (index {PlayerModelIndex}, team {PlayerTeam}) loaded={body.ModelLoaded} animated={body.Animated} weapon={body.ViewWeaponPath ?? "none"}");
    }

    private (Vector3 Feet, float Yaw) FindSpawn(OgzMap map)
    {
        foreach (var (_, e) in map.OfType(Entities.EntityType.PlayerStart))
            return (e.Position, e.Attr1);
        return (World!.Bounds.GetCenter(), 0f);
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
