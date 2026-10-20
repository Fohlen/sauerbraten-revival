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
    /// imported model's up-axis into the Z-up world frame.
    /// </summary>
    public Vector3 ModelRotationDegrees { get; set; }

    /// <summary>Skin/normal material, applied to the loaded model.</summary>
    public Material? SkinMaterial { get; set; }

    /// <summary>
    /// Keep the imported scene's node hierarchy (skeleton, bone attachments, skinned
    /// mesh) instead of flattening to bare meshes. Required for skeletal animation;
    /// off for static models (mapmodels, items).
    /// </summary>
    public bool KeepSkeleton { get; set; }

    /// <summary>The imported <c>Skeleton3D</c>, when <see cref="KeepSkeleton"/> is set.</summary>
    public Skeleton3D? Skeleton { get; private set; }

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
    public virtual void Reload()
    {
        if (GetNodeOrNull("Model") is { } previous) previous.Free();
        ModelLoaded = false;
        bool haveRes = !string.IsNullOrEmpty(GlbPath) && ResourceLoader.Exists(GlbPath);
        bool haveOs = !string.IsNullOrEmpty(GlbOsPath) && File.Exists(GlbOsPath);
        if (!haveRes && !haveOs) return;
        if (TryLoadModel()) ModelLoaded = true;
        else GD.PushWarning($"[model] failed to load '{GlbPath ?? GlbOsPath}'");
    }

    /// <summary>
    /// Convert a Cube 2 <c>mdltrans</c> (<paramref name="translation"/>, Sauerbraten
    /// Z-up) into the glTF model frame the <see cref="ModelInstance"/> holder lives in.
    ///
    /// Entity nodes are oriented by <see cref="EntityFacing.ApplyYaw"/>, whose basis maps
    /// the imported model's Y-up (and -Z forward) onto the world's Z-up (and facing). The
    /// holder sits *inside* that basis, so a Sauerbraten-frame offset must be pre-rotated
    /// by its inverse — otherwise a purely vertical <c>mdltrans</c> (e.g. the teleporter's
    /// <c>0 0 -7</c>) becomes horizontal and orbits as the entity yaws.
    /// </summary>
    public static Vector3 OffsetToModelFrame(Vector3 translation) =>
        new(translation.X, translation.Z, -translation.Y);

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
            Position = OffsetToModelFrame(ModelTranslation),
            RotationDegrees = ModelRotationDegrees,
        };
        if (KeepSkeleton)
        {
            // Keep the full glTF hierarchy so the Skeleton3D and skinned mesh survive.
            scene.Name = "Imported";
            holder.AddChild(scene);
            Skeleton = FindSkeleton(scene);
        }
        else
        {
            if (CopyMeshes(scene, holder) == 0) { scene.Free(); return false; }
            scene.Free(); // meshes are shared resources; drop the temporary glTF scene
        }

        AddChild(holder);
        if (SkinMaterial != null) ApplyMaterial(holder, SkinMaterial);
        return true;
    }

    private static Skeleton3D? FindSkeleton(Node n)
    {
        if (n is Skeleton3D s) return s;
        foreach (var c in n.GetChildren())
            if (FindSkeleton(c) is { } found) return found;
        return null;
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
