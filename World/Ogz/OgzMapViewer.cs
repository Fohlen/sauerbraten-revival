using System;
using System.IO;
using Godot;
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
/// overridden via <see cref="OgzPath"/>. Geometry is untextured.
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
        var chunked = new OctaMesher(octa).BuildChunked(ChunkSize);

        // No culling for inspection: geometry is visible regardless of winding.
        var material = new StandardMaterial3D
        {
            AlbedoColor = MeshTint,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        var world = new Node3D { Name = "World", RotationDegrees = new Vector3(-90, 0, 0) }; // Z-up -> Y-up
        AddChild(world);

        Aabb bounds = default;
        bool hasBounds = false;
        foreach (var chunk in chunked.Chunks)
        {
            world.AddChild(new MeshInstance3D
            {
                Name = $"Chunk_{chunk.Origin.X}_{chunk.Origin.Y}_{chunk.Origin.Z}",
                Mesh = chunk.Mesh,
                Position = new Vector3(chunk.Origin.X, chunk.Origin.Y, chunk.Origin.Z),
                MaterialOverride = material,
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

    private Camera3D AddLightAndCamera(Vector3 centre, float radius)
    {
        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-50, -30, 0),
            ShadowEnabled = true,
        });

        var camera = new Camera3D { Name = "Camera3D", Fov = 75 };
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
