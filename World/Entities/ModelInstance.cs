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

    /// <summary>
    /// Extra rotation (degrees) applied to the loaded geometry. Used to bring an
    /// imported model's up-axis into the Z-up world frame — glTF is Y-up, so a
    /// Y-up model needs (0, 0, 90) or similar here.
    /// </summary>
    public Vector3 ModelRotationDegrees { get; set; }

    /// <summary>
    /// Normalise the loaded geometry so it is centred on the origin horizontally and
    /// its lowest point sits at the node origin (the feet). Imported MD5 bodies are
    /// off-centre and centred vertically, so without this they sink through the floor
    /// and sit to one side.
    /// </summary>
    public bool GroundAtFeet { get; set; }

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

    /// <summary>
    /// (Re)load the configured glTF, replacing any previously loaded geometry.
    /// <see cref="_Ready"/> calls this once; callers that set the path fields after
    /// the node is already in the tree (e.g. <see cref="PlayerModel"/>) can invoke it
    /// again to swap models at runtime.
    /// </summary>
    public void Reload()
    {
        if (GetNodeOrNull("Model") is { } previous) previous.Free();
        ModelLoaded = false;
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
            RotationDegrees = ModelRotationDegrees,
        };
        if (CopyMeshes(scene, holder) == 0) return false;
        scene.Free(); // meshes are shared resources; drop the temporary glTF scene

        AddChild(holder);
        if (SkinMaterial != null) ApplyMaterial(holder, SkinMaterial);
        if (GroundAtFeet) GroundHolder(holder);
        return true;
    }

    /// <summary>
    /// Normalise the loaded geometry's vertical placement: shift the holder so the
    /// lowest mesh vertex sits at the node origin. Imported MD5 bodies are centred on
    /// their origin vertically, so without this half the model sinks through the floor.
    /// The shift is along the holder's parent local <b>Y</b> — the model body frame's up
    /// axis (converted models are Y-up). No live tree is required.
    /// </summary>
    private static void GroundHolder(Node3D holder)
    {
        bool has = false;
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        CollectBounds(holder, holder.Transform, ref has, ref min, ref max);
        if (has && min.Y < 0f) holder.Position += new Vector3(0f, -min.Y, 0f);
    }

    /// <summary>
    /// Walk <paramref name="node"/>, carrying <paramref name="toHolderParent"/> — the
    /// transform from <paramref name="node"/>'s local space into the holder's parent
    /// frame — and accumulate the mesh-corner bounds.
    /// </summary>
    private static void CollectBounds(Node node, Transform3D toHolderParent, ref bool has, ref Vector3 min, ref Vector3 max)
    {
        if (node is MeshInstance3D { Mesh: not null } mesh)
        {
            var a = mesh.GetAabb();
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = toHolderParent * a.GetEndpoint(i);
                min = new Vector3(Mathf.Min(min.X, p.X), Mathf.Min(min.Y, p.Y), Mathf.Min(min.Z, p.Z));
                max = new Vector3(Mathf.Max(max.X, p.X), Mathf.Max(max.Y, p.Y), Mathf.Max(max.Z, p.Z));
                has = true;
            }
        }
        foreach (var child in node.GetChildren())
        {
            var childXform = child is Node3D c3 ? toHolderParent * c3.Transform : toHolderParent;
            CollectBounds(child, childXform, ref has, ref min, ref max);
        }
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
