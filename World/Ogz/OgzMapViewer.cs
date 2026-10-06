using System;
using System.IO;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.World.Ogz;

/// <summary>
/// Dev-only inspection scene: reads a legacy <c>.ogz</c> from the reference dump,
/// builds the runtime octree and displays the chunked geometry.
///
/// This deliberately reads OGZ at runtime for tooling convenience — it is NOT the
/// shipping path. The shipped importer converts maps to the native format; see
/// docs/ogz-import.md.
///
/// It is a <c>[Tool]</c> script so the geometry also appears in the editor. It
/// defaults to church51 from the reference dump (<c>SAUER_REF</c>) and can be
/// overridden via <see cref="OgzPath"/>. Diffuse textures come from the map's
/// metadata catalogue; the camera is the coop-edit flycam (see
/// <see cref="FreeFlyCamera"/>): WASD + mouse look.
///
/// Tool scripts only rebuild when the scene is (re)opened, so after editing this
/// script close and reopen <c>scenes/map_viewer.tscn</c> (or restart the editor).
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

    private static readonly Color MeshTint = new(0.8f, 0.8f, 0.85f);
    private bool _built;

    public override void _Ready()
    {
        if (_built) return;
        _built = true;

        string path = string.IsNullOrEmpty(OgzPath) ? DefaultPath() : ResolvePath(OgzPath);
        if (!File.Exists(path))
        {
            GD.PushError($"[viewer] OGZ not found: '{path}'. Set OgzPath or SAUER_REF.");
            return;
        }

        GD.Print($"[viewer] building {path} (editor={Engine.IsEditorHint()})");

        var map = OgzReader.Read(path);
        var octa = map.ToOcta();

        // Materials: committed catalogue for this map + textures loaded from the dump.
        string mapName = Path.GetFileNameWithoutExtension(path);
        var textures = new DumpTextureSource(ReferenceRoot);
        var catalogue = MetadataCatalogue.TryLoadForMap(mapName);
        MaterialSet? materials = catalogue != null ? MaterialBuilder.Build(catalogue, textures) : null;
        if (materials == null) GD.PushWarning($"[viewer] no metadata catalogue for '{mapName}'; showing untextured geometry.");
        ApplySky(map, textures);

        var chunked = new OctaMesher(octa, materials).BuildChunked(ChunkSize);

        // Converted mapmodels (assimp), imported by Godot as Resources.
        const string modelsResDir = "res://assets/models";
        string modelsDir = ProjectSettings.GlobalizePath(modelsResDir);
        var modelCatalog = ModelCatalog.Load(Path.Combine(modelsDir, "models.json"), modelsDir, modelsResDir);

        var world = new Node3D { Name = "World", RotationDegrees = new Vector3(-90, 0, 0) }; // Z-up -> Y-up
        AddChild(world);
        world.AddChild(LightBuilder.Build(map));
        world.AddChild(PlayerStartBuilder.Build(map));
        world.AddChild(TriggerBuilder.Build(map, catalogue));
        var mapModels = MapModelBuilder.Build(map, catalogue, modelCatalog, textures);
        world.AddChild(mapModels);
        int loadedModels = 0, totalModels = 0;
        foreach (var child in mapModels.GetChildren())
        {
            totalModels++;
            if (child is MapModel { ModelLoaded: true }) loadedModels++;
        }
        GD.Print($"[viewer] mapmodels: {loadedModels}/{totalModels} loaded");

        // Fallback so geometry is still visible if there is no catalogue.
        StandardMaterial3D? fallback = materials == null
            ? new StandardMaterial3D { AlbedoColor = MeshTint, CullMode = BaseMaterial3D.CullModeEnum.Disabled }
            : null;

        Aabb bounds = default;
        bool hasBounds = false;
        foreach (var chunk in chunked.Chunks)
        {
            world.AddChild(new MeshInstance3D
            {
                Name = $"Chunk_{chunk.Origin.X}_{chunk.Origin.Y}_{chunk.Origin.Z}",
                Mesh = chunk.Mesh,
                Position = new Vector3(chunk.Origin.X, chunk.Origin.Y, chunk.Origin.Z),
                MaterialOverride = fallback,
            });

            var aabb = chunk.Mesh.GetAabb();
            aabb.Position += new Vector3(chunk.Origin.X, chunk.Origin.Y, chunk.Origin.Z);
            bounds = hasBounds ? bounds.Merge(aabb) : aabb;
            hasBounds = true;
        }

        // Sauerbraten bounds are Z-up; the World node rotates them to Godot Y-up.
        Aabb sauerBounds = hasBounds ? bounds : new Aabb(Vector3.Zero, Vector3.One);
        Vector3 sauerCenter = sauerBounds.GetCenter();
        Vector3 centre = new(sauerCenter.X, sauerCenter.Z, -sauerCenter.Y);
        float radius = Mathf.Max(sauerBounds.Size.Length() * 0.5f, 1.0f);

        var camera = AddLightAndCamera(centre, radius);
#if TOOLS
        if (Engine.IsEditorHint() && FrameEditorCamera)
        {
            _frameCamera = camera;
            _frameTarget = centre;
            CallDeferred(nameof(FrameEditorViewport));
        }
#endif
        GD.Print($"[viewer] {path}: chunks={chunked.Chunks.Count} triangles={chunked.TriangleCount}");
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

    private static string ReferenceRoot =>
        System.Environment.GetEnvironmentVariable("SAUER_REF")
        ?? "/Users/fohlen/Downloads/sauerbraten-code-r6889";

    private static string DefaultPath() => Path.Combine(ReferenceRoot, "packages", "base", "church51.ogz");

    private static string ResolvePath(string ogzPath) =>
        Path.IsPathRooted(ogzPath) ? ogzPath : Path.Combine(ReferenceRoot, "packages", ogzPath);
}
