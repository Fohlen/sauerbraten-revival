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

    [Export] public string? GlbPath { get; set; }
    [Export] public float ModelScale { get; set; } = 1.0f;
    [Export] public Vector3 ModelTranslation { get; set; }

    [Export] public bool ShowGizmo { get; set; } = true;
    [Export] public float GizmoSize { get; set; } = 16.0f;

    /// <summary>Skin/normal material, applied to the loaded model (built by the builder).</summary>
    public Material? SkinMaterial { get; set; }

    public override void _Ready()
    {
        if (!string.IsNullOrEmpty(GlbPath) && File.Exists(GlbPath))
        {
            if (TryLoadModel()) return;
            GD.PushWarning($"[mapmodel] failed to load '{GlbPath}'");
        }
        else if (!string.IsNullOrEmpty(ModelPath))
        {
            GD.PushWarning($"[mapmodel] no converted glTF for '{ModelPath}'");
        }
        if (ShowGizmo) AddChild(CreateGizmo());
    }

    private bool TryLoadModel()
    {
        var document = new GltfDocument();
        var state = new GltfState();
        if (document.AppendFromFile(GlbPath!, state) != Error.Ok) return false;

        var scene = document.GenerateScene(state);
        if (scene == null) return false;

        var holder = new Node3D
        {
            Name = "Model",
            Scale = Vector3.One * ModelScale,
            Position = ModelTranslation,
        };
        holder.AddChild(scene);
        AddChild(holder);

        if (SkinMaterial != null) ApplyMaterial(scene, SkinMaterial);
        return true;
    }

    private static void ApplyMaterial(Node node, Material material)
    {
        if (node is MeshInstance3D mesh) mesh.MaterialOverride = material;
        foreach (var child in node.GetChildren()) ApplyMaterial(child, material);
    }

    private MeshInstance3D CreateGizmo() => new()
    {
        Name = "Gizmo",
        Mesh = new BoxMesh { Size = Vector3.One * Mathf.Max(GizmoSize, 1.0f) },
        MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.6f, 0.1f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        },
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };
}
