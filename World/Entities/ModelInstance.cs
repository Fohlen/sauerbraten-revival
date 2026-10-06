using System.IO;
using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Loads an assimp-converted glTF: prefers Godot's imported Resource (renders in
/// the editor too), falls back to parsing the file from disk, applies the Cube2
/// <c>mdlscale</c>/<c>mdltrans</c> and an optional skin material. Shared by
/// <see cref="MapModel"/> and <see cref="Item"/>.
/// </summary>
[Tool]
[GlobalClass]
public partial class ModelInstance : Node3D
{
    /// <summary>Imported glTF (<c>res://</c>).</summary>
    [Export] public string? GlbPath { get; set; }

    /// <summary>OS path of the glTF (runtime fallback).</summary>
    [Export] public string? GlbOsPath { get; set; }

    [Export] public float ModelScale { get; set; } = 1.0f;
    [Export] public Vector3 ModelTranslation { get; set; }

    /// <summary>Skin/normal material, applied to the loaded model.</summary>
    public Material? SkinMaterial { get; set; }

    /// <summary>True once a converted glTF was loaded.</summary>
    public bool ModelLoaded { get; private set; }

    public override void _Ready()
    {
        bool haveRes = !string.IsNullOrEmpty(GlbPath) && ResourceLoader.Exists(GlbPath);
        bool haveOs = !string.IsNullOrEmpty(GlbOsPath) && File.Exists(GlbOsPath);
        if (!haveRes && !haveOs) return;

        if (TryLoadModel()) ModelLoaded = true;
        else GD.PushWarning($"[model] failed to load '{GlbPath ?? GlbOsPath}'");
    }

    private bool TryLoadModel()
    {
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
}
