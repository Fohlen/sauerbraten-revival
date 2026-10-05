using System.IO;
using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// A map model (<c>ET_MAPMODEL</c>). Position and yaw come from the entity;
/// <see cref="ModelPath"/> is the engine's registered model name, resolved through
/// the metadata catalogue.
///
/// If <see cref="GlbPath"/> points at an assimp-converted glTF (see
/// <c>tools/model-import</c>) it is loaded at runtime; otherwise a placeholder box
/// is shown so placement can still be verified.
/// </summary>
[Tool]
[GlobalClass]
public partial class MapModel : Node3D
{
    [Export] public string ModelPath { get; set; } = "";

    /// <summary>attr1: facing yaw in degrees (Sauerbraten convention).</summary>
    [Export] public float Yaw { get; set; }

    /// <summary>Imported glTF (<c>res://</c>) or, as a fallback, an OS path.</summary>
    [Export] public string? GlbPath { get; set; }
    [Export] public string? GlbOsPath { get; set; }
    [Export] public float ModelScale { get; set; } = 1.0f;
    [Export] public Vector3 ModelTranslation { get; set; }

    [Export] public bool ShowGizmo { get; set; } = true;
    [Export] public float GizmoSize { get; set; } = 16.0f;

    /// <summary>Skin/normal material, applied to the loaded model (built by the builder).</summary>
    public Material? SkinMaterial { get; set; }

    /// <summary>True once a converted glTF was loaded (false for a placeholder box).</summary>
    public bool ModelLoaded { get; private set; }

    public override void _Ready()
    {
        bool editor = Engine.IsEditorHint();
        bool loaded = false;

        bool haveRes = !string.IsNullOrEmpty(GlbPath) && ResourceLoader.Exists(GlbPath);
        bool haveOs = !string.IsNullOrEmpty(GlbOsPath) && File.Exists(GlbOsPath);
        if (haveRes || haveOs)
        {
            loaded = TryLoadModel();
            if (loaded) ModelLoaded = true;
            else GD.PushWarning($"[mapmodel] failed to load '{GlbPath ?? GlbOsPath}'");
        }
        else
        {
            GD.PushWarning($"[mapmodel] no converted glTF for '{ModelPath}' (glb={GlbPath ?? GlbOsPath ?? "<null>"})");
        }

        // Editor locator: dynamically-added nodes get no editor gizmo, so drop a
        // small marker at each mapmodel. At runtime it only appears if the mesh
        // failed to load, so it doesn't obscure the real model.
        if (ShowGizmo && (editor || !loaded)) AddChild(CreateGizmo());
    }

    private bool TryLoadModel()
    {
        // Prefer Godot's imported Resource (renders in the editor too); fall back
        // to parsing the glTF directly from disk.
        Node? scene = null;
        if (!string.IsNullOrEmpty(GlbPath) && ResourceLoader.Exists(GlbPath))
        {
            var packed = ResourceLoader.Load<PackedScene>(GlbPath);
            if (packed != null) scene = packed.Instantiate();
        }
        else if (!string.IsNullOrEmpty(GlbOsPath) && File.Exists(GlbOsPath))
        {
            var document = new GltfDocument();
            var state = new GltfState();
            if (document.AppendFromFile(GlbOsPath!, state) == Error.Ok) scene = document.GenerateScene(state);
        }
        if (scene == null) return false;

        var holder = new Node3D
        {
            Name = "Model",
            Scale = Vector3.One * ModelScale,
            Position = ModelTranslation,
        };

        // Flatten the generated glTF scene into plain MeshInstance3Ds. The
        // nested glTF scene subtree doesn't rasterise reliably in the editor's
        // tool viewport, whereas a flat MeshInstance3D does.
        if (CopyMeshes(scene, holder) == 0) return false;

        AddChild(holder);
        if (SkinMaterial != null) ApplyMaterial(holder, SkinMaterial);
        return true;
    }

    private static int CopyMeshes(Node source, Node3D target)
    {
        int count = 0;
        if (source is MeshInstance3D { Mesh: not null } mesh)
        {
            target.AddChild(new MeshInstance3D { Name = mesh.Name, Mesh = mesh.Mesh, Transform = mesh.Transform });
            count++;
        }
        foreach (var child in source.GetChildren()) count += CopyMeshes(child, target);
        return count;
    }

    private static void ApplyMaterial(Node node, Material material)
    {
        if (node is MeshInstance3D mesh) mesh.MaterialOverride = material;
        foreach (var child in node.GetChildren()) ApplyMaterial(child, material);
    }

    private MeshInstance3D CreateGizmo() => new()
    {
        Name = "EditorMarker",
        Mesh = new SphereMesh { Radius = Mathf.Max(GizmoSize, 1.0f) * 0.25f, Height = Mathf.Max(GizmoSize, 1.0f) * 0.5f },
        MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.6f, 0.1f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        },
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };
}
