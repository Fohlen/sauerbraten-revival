using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Models;

/// <summary>
/// The local player's first-person hudgun (render.cpp <c>drawhudmodel</c>).
/// Cube2 stores the view model in its Z-up frame; the camera-local basis maps
/// Cube2's right/up/forward axes onto Godot's right/up/-Z camera axes.
/// </summary>
[GlobalClass]
public partial class HudgunModel : Node3D
{
    private Vector3 _hudgunOffset = Vector3.Zero;
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

    private Vector3 _hudgunRotationDegrees = Vector3.Zero;
    [Export]
    public Vector3 HudgunRotationDegrees
    {
        get => _hudgunRotationDegrees;
        set { _hudgunRotationDegrees = value; ApplyTransform(); }
    }

    public string? ModelPath { get; private set; }
    public bool ModelLoaded { get; private set; }
    public IReadOnlyList<ModelInstance> Parts => _parts;

    /// <summary>Active hudgun submodel name (e.g. "rifle", "shotg").</summary>
    public string CurrentGun => _gunName;

    private static readonly Basis BaseHudBasis = new(Vector3.Up, Mathf.Pi / 2f);

    /// <summary>
    /// Maps Cube 2 model translation axes to camera-local space:
    /// model forward (tx) -> camera forward (-Z),
    /// model right (ty)   -> camera right (+X),
    /// model up (tz)      -> camera up (+Y).
    /// </summary>
    public static Vector3 CubeVectorToCamera(Vector3 vector) =>
        new(vector.Y, vector.Z, -vector.X);

    private readonly List<ModelInstance> _parts = new();
    private IModelCatalog? _models;
    private ModelInfo? _info;
    private ITextureSource? _textures;
    private Dictionary<string, Material>? _skinCache;
    private int _modelIndex;
    private string _gunName = "";
    private string _currentAnim = "";
    private int _lastActionSeen = -1;

    /// <summary>Configure before adding this node to the camera.</summary>
    public void Configure(
        IModelCatalog? models,
        ITextureSource? textures,
        int modelIndex,
        string gun,
        Dictionary<string, Material>? skinCache = null)
    {
        _models = models;
        _textures = textures;
        _modelIndex = modelIndex;
        _gunName = gun;
        ModelPath = $"{Playermodels.Get(modelIndex).Hudguns}/{gun}";
        _info = models?.Get(ModelPath);
        _skinCache = skinCache ?? new Dictionary<string, Material>();
        if (IsInsideTree()) BuildModel();
    }

    /// <summary>Switch to a different gun model at runtime (e.g. "shotg", "rifle").</summary>
    public void SetGun(string gun)
    {
        if (gun == _gunName) return;
        _gunName = gun;
        ModelPath = $"{Playermodels.Get(_modelIndex).Hudguns}/{gun}";
        _info = _models?.Get(ModelPath);
        _currentAnim = "";
        _lastActionSeen = -1;
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
                // Part 1 glTF has its own <MD5_Root> (-90 deg X rotation). Because it is
                // parented beneath Part 0's already-transformed bone, invert that redundant
                // rotation so the weapon aligns forward with the hand.
                part.Basis = new Basis(Vector3.Right, Mathf.Pi / 2f);
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
        Position = CubeVectorToCamera(_info.Translation * _info.Scale) + HudgunOffset;
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

    /// <summary>
    /// Play a named clip across all parts that author it (e.g. "gun idle", "gun shoot").
    /// </summary>
    public void Play(string name, bool loop = true, bool restart = false)
    {
        for (int i = 0; i < _parts.Count; i++)
        {
            var part = _parts[i];
            var partInfo = (_info != null && i < _info.Parts.Count) ? _info.Parts[i] : null;
            var player = FindAnimationPlayer(part);
            if (player == null || !player.HasAnimation(name)) continue;
            var animation = player.GetAnimation(name);
            animation.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            float defaultSpeed = name == "gun shoot" ? 60f : 10f;
            float fps = partInfo?.AnimSpeeds.GetValueOrDefault(name, defaultSpeed) ?? defaultSpeed;
            player.SpeedScale = fps / 24f;
            if (restart || player.CurrentAnimation != name || !player.IsPlaying())
            {
                if (restart && player.CurrentAnimation == name) player.Seek(0, update: true);
                player.Play(name);
            }
        }
        _currentAnim = name;
    }

    /// <summary>
    /// Update hudgun animations for the active player weapon state (render.cpp drawhudgun).
    /// Plays "gun shoot" while an attack is active, otherwise "gun idle".
    /// </summary>
    public void UpdateAnim(int now, int lastAction, int lastAttackGun, int currentGun, int attackDelay)
    {
        bool shooting = lastAction > 0 && lastAttackGun == currentGun && now - lastAction < attackDelay;
        string targetAnim = shooting ? "gun shoot" : "gun idle";
        bool isNewAction = shooting && lastAction != _lastActionSeen;
        if (targetAnim != _currentAnim || isNewAction)
        {
            Play(targetAnim, loop: !shooting, restart: isNewAction);
            _lastActionSeen = lastAction;
        }
    }

    private static AnimationPlayer? FindAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer player) return player;
        foreach (var child in node.GetChildren())
            if (FindAnimationPlayer(child) is { } found) return found;
        return null;
    }
}
