using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// The local player's first-person hudgun (render.cpp <c>drawhudmodel</c>).
/// Cube2 stores the view model in its Z-up frame; the camera-local basis maps
/// Cube2's right/up/forward axes onto Godot's right/up/-Z camera axes.
/// </summary>
[GlobalClass]
public partial class HudgunModel : Node3D
{
    private static readonly Basis CubeToCamera = new(Vector3.Right, -Mathf.Pi / 2f);
    private static readonly Basis EngineHudYaw = new(Vector3.Back, Mathf.Pi / 2f);
    private static readonly Basis BaseHudBasis =
        CubeToCamera * EngineHudYaw * CubeToCamera.Inverse();

    private Vector3 _hudgunOffset = new(-2.5f, -2.5f, -2f);
    [Export]
    public Vector3 HudgunOffset
    {
        get => _hudgunOffset;
        set { _hudgunOffset = value; ApplyTransform(); }
    }

    private float _hudgunScale = 1f;
    [Export]
    public float HudgunScale
    {
        get => _hudgunScale;
        set { _hudgunScale = value; ApplyTransform(); }
    }

    private Vector3 _hudgunRotationDegrees = new(90f, 0f, 0f);
    [Export]
    public Vector3 HudgunRotationDegrees
    {
        get => _hudgunRotationDegrees;
        set { _hudgunRotationDegrees = value; ApplyTransform(); }
    }

    public string? ModelPath { get; private set; }
    public bool ModelLoaded { get; private set; }
    public IReadOnlyList<ModelInstance> Parts => _parts;

    public static Vector3 CubeVectorToCamera(Vector3 vector) => CubeToCamera * vector;

    private readonly List<ModelInstance> _parts = new();
    private ModelInfo? _info;
    private ITextureSource? _textures;
    private Dictionary<string, Material>? _skinCache;

    /// <summary>Configure before adding this node to the camera.</summary>
    public void Configure(
        ModelCatalog? models,
        ITextureSource? textures,
        int modelIndex,
        string gun,
        Dictionary<string, Material>? skinCache = null)
    {
        ModelPath = $"{Playermodels.Get(modelIndex).Hudguns}/{gun}";
        _info = models?.Get(ModelPath);
        _textures = textures;
        _skinCache = skinCache ?? new Dictionary<string, Material>();
        if (IsInsideTree()) BuildModel();
    }

    public override void _Ready() => BuildModel();

    private void BuildModel()
    {
        foreach (var part in _parts)
            if (GodotObject.IsInstanceValid(part)) part.Free();
        _parts.Clear();
        ModelLoaded = false;

        if (_info == null || _info.Parts.Count == 0)
        {
            GD.PushWarning($"[hudgun] linked model '{ModelPath}' is missing from the model catalogue");
            return;
        }

        ApplyTransform();

        for (int i = 0; i < _info.Parts.Count; i++)
        {
            var info = _info.Parts[i];
            if (info.GlbPath == null && info.GlbOsPath == null)
            {
                GD.PushWarning($"[hudgun] model '{ModelPath}' part {i} has no converted glTF");
                return;
            }

            var part = new ModelInstance
            {
                Name = $"Part{i}",
                GlbPath = info.GlbPath,
                GlbOsPath = info.GlbOsPath,
                KeepSkeleton = true,
                SkinMaterial = ModelSkin.Build(info, _textures, _skinCache!),
            };
            Node3D? parent = this;
            if (info.LinkParent >= 0)
            {
                if (info.LinkParent >= _parts.Count || string.IsNullOrEmpty(info.LinkTag) ||
                    !_info.Parts[info.LinkParent].Tags.TryGetValue(info.LinkTag, out string? bone) ||
                    FindAnchor(_parts[info.LinkParent], bone) is not { } anchor)
                {
                    GD.PushWarning($"[hudgun] cannot resolve link '{info.LinkTag}' for '{ModelPath}' part {i}");
                    return;
                }
                parent = anchor;
            }

            parent.AddChild(part);
            _parts.Add(part);
            StartIdle(part, info);
        }
        ModelLoaded = _parts.Count == _info.Parts.Count &&
                      _parts.TrueForAll(part => part.ModelLoaded);
    }

    private void ApplyTransform()
    {
        if (_info == null) return;
        Scale = Vector3.One * _info.Scale * HudgunScale;
        Position = CubeVectorToCamera(EngineHudYaw * (_info.Translation * _info.Scale)) + HudgunOffset;
        Basis = Basis.FromEuler(HudgunRotationDegrees * Mathf.Pi / 180f) * BaseHudBasis;
    }

    private static Node3D? FindAnchor(ModelInstance part, string bone)
    {
        if (part.Skeleton != null && part.Skeleton.FindBone(bone) >= 0)
        {
            var attachment = new BoneAttachment3D { Name = $"HudgunLink_{bone}" };
            part.Skeleton.AddChild(attachment);
            attachment.BoneName = bone;
            return attachment;
        }
        return part.FindChild(bone, recursive: true, owned: false) as Node3D;
    }

    private static void StartIdle(ModelInstance part, ModelInfo info)
    {
        var player = FindAnimationPlayer(part);
        if (player == null || !player.HasAnimation("gun idle")) return;
        var animation = player.GetAnimation("gun idle");
        animation.LoopMode = Animation.LoopModeEnum.Linear;
        player.SpeedScale = info.AnimSpeeds.GetValueOrDefault("gun idle", 10f) / 24f;
        player.Play("gun idle");
    }

    private static AnimationPlayer? FindAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer player) return player;
        foreach (var child in node.GetChildren())
            if (FindAnimationPlayer(child) is { } found) return found;
        return null;
    }
}
