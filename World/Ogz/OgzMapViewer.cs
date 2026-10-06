using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Ogz;

/// <summary>
/// Dev-only inspection scene: builds a legacy <c>.ogz</c> via <see cref="MapWorld"/>
/// and views it with the coop-edit flycam (see <see cref="FreeFlyCamera"/>): WASD +
/// mouse look.
///
/// This deliberately reads OGZ at runtime for tooling convenience — it is NOT the
/// shipping path. The shipped importer converts maps to the native format; see
/// docs/ogz-import.md.
///
/// It is a <c>[Tool]</c> script so the geometry also appears in the editor. It
/// defaults to church51 from the reference dump (<c>SAUER_REF</c>) and can be
/// overridden via <see cref="OgzPath"/>. Tool scripts only rebuild when the scene
/// is (re)opened, so after editing this script close and reopen
/// <c>scenes/map_viewer.tscn</c> (or restart the editor).
/// </summary>
[Tool]
[GlobalClass]
public partial class OgzMapViewer : Node3D
{
    /// <summary>Override map to view. Absolute, or relative to the reference dump's <c>packages/</c> dir (e.g. <c>base/church51.ogz</c>); empty uses the default.</summary>
    [Export] public string OgzPath { get; set; } = "";
    [Export] public int ChunkSize { get; set; } = 512;

    /// <summary>In the editor, move the 3D viewport camera to frame the geometry on open.</summary>
    [Export] public bool FrameEditorCamera { get; set; } = true;

    /// <summary>The built world (geometry + octree), once <see cref="_Ready"/> has run.</summary>
    public MapWorld? World { get; private set; }

    private bool _built;

    public override void _Ready()
    {
        if (_built) return;
        _built = true;

        World = new MapWorld { Name = "MapWorld", OgzPath = OgzPath, ChunkSize = ChunkSize };
        AddChild(World);
        World.Build();
        if (!World.Built) return; // missing map, error already pushed

        ApplySky(World.Map!, World.Textures!);

        var camera = AddLightAndCamera(World.Centre, World.Radius);
#if TOOLS
        if (Engine.IsEditorHint() && FrameEditorCamera)
        {
            _frameCamera = camera;
            _frameTarget = World.Centre;
            CallDeferred(nameof(FrameEditorViewport));
        }
#endif
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

    private Camera3D AddLightAndCamera(Vector3 centre, float radius)
    {
        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-50, -30, 0),
            ShadowEnabled = true,
        });

        var camera = new FreeFlyCamera
        {
            Name = "Camera3D",
            Fov = 75,
            Far = Mathf.Max(radius * 8.0f, 1000.0f),
        };
        camera.Position = centre + new Vector3(0, radius * 0.6f, radius * 1.3f);
        AddChild(camera);
        camera.LookAt(centre, Vector3.Up);
        if (!Engine.IsEditorHint()) camera.Current = true;
        return camera;
    }

#if TOOLS
    private Camera3D? _frameCamera;
    private Vector3 _frameTarget;

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
