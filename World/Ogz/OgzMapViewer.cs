using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;

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
/// It deliberately reads OGZ at runtime for tooling convenience — it is NOT the
/// shipping path. The shipped importer converts maps to the native format; see
/// docs/ogz-import.md.
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

	/// <summary>
	/// Third-person view weapon to attach to the player model's <c>Weapon</c> bone
    /// (render.cpp <c>vweps[]</c>, e.g. <c>vwep/rifle</c>). Empty attaches none. This is
    /// a live property: changing it from the remote inspector swaps the weapon on the
    /// spawned <see cref="PlayerModel"/> immediately.
    /// </summary>
    [Export]
    public string ViewWeapon
    {
        get => _viewWeapon;
        set
        {
            _viewWeapon = value ?? "";
            _playerModel?.SetViewWeapon(string.IsNullOrEmpty(_viewWeapon) ? null : _viewWeapon);
        }
    }

    /// <summary>Gun model used for the local first-person hudgun (e.g. "rifle").</summary>
    [Export] public string Hudgun { get; set; } = "rifle";

    private string _viewWeapon = "";
    private PlayerModel? _playerModel;

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

        World = new MapWorld
        {
            Name = "MapWorld",
            OgzPath = OgzPath,
            ChunkSize = ChunkSize,
            ShowEntityMarkers = ShowEntityMarkers,
        };
        AddChild(World);
        World.Build();
        if (!World.Built) return; // missing map, error already pushed

        ApplySky(World.Map!, World.Textures!);
        if (SpawnPlayer) AttachPlayer();
        if (FrameCamera) FrameSceneCamera(World.Centre, World.Radius);
    }

    /// <summary>
    /// Move a scene-authored <see cref="MapPlayer"/> under the Z-up world node and
	/// initialise it against the octree at the map's first <c>ET_PLAYERSTART</c>
	/// (falling back to the map centre). Physics then owns its position/orientation.
	/// </summary>
	private void AttachPlayer()
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
		AttachPlayerModel(player);

		var effects = new Effects.WeaponEffectsRenderer { Name = "WeaponEffects" };
		World.AddChild(effects);
		player.WeaponEffects = effects;

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

        var body = new PlayerModel { Name = "PlayerModel" };
        World!.AddChild(body);
        player.PlayerModel = body;
        player.HidePlaceholderBody = true;
        _playerModel = body;

        body.Configure(World.Models, World.Textures, PlayerModelIndex, PlayerTeam,
            viewWeapon: string.IsNullOrEmpty(_viewWeapon) ? null : _viewWeapon);
        player.SyncBodyModelNow();
        var hudgun = new HudgunModel { Name = "Hudgun" };
        hudgun.Configure(World.Models, World.Textures, PlayerModelIndex, Hudgun);
        if (player.GetNodeOrNull<Camera3D>("Camera3D") is { } camera)
        {
            camera.AddChild(hudgun);
            player.Hudgun = hudgun;
        }
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
