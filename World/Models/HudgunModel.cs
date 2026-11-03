using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Models;

/// <summary>
/// The local player's first-person hudgun (render.cpp <c>drawhudmodel</c>).
/// Cube2 stores the view model in its Z-up frame; the camera-local basis maps
/// Cube2's right/up/forward axes onto Godot's right/up/-Z camera axes.
///
/// Pre-builds and retains the weapon rigs for the active player model in an internal
/// bank (ADR 0011). Switching weapons toggles subtree visibility and process mode,
/// eliminating runtime node instantiations, heap allocations, and GPU skinning buffer rebuilds.
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
    public bool ModelLoaded => _activeRig != null && _activeRig.Loaded;
    public IReadOnlyList<ModelInstance> Parts => _activeRig != null ? _activeRig.Parts : _emptyParts;

    /// <summary>Active hudgun submodel name (e.g. "rifle", "shotg").</summary>
    public string CurrentGun => _gunName;

    /// <summary>Number of weapon rigs currently pre-built and retained in the bank.</summary>
    public int CachedWeaponCount => _weaponBank.Count;

    private static readonly Basis BaseHudBasis = new(Vector3.Up, Mathf.Pi / 2f);
    private static readonly IReadOnlyList<ModelInstance> _emptyParts = System.Array.Empty<ModelInstance>();

    /// <summary>
    /// Maps Cube 2 model translation axes to camera-local space:
    /// model forward (tx) -> camera forward (-Z),
    /// model right (ty)   -> camera right (+X),
    /// model up (tz)      -> camera up (+Y).
    /// </summary>
    public static Vector3 CubeVectorToCamera(Vector3 vector) =>
        new(vector.Y, vector.Z, -vector.X);

    private sealed class WeaponRig
    {
        public string GunName { get; }
        public string ModelPath { get; }
        public ModelInfo Info { get; }
        public Node3D Container { get; }
        public List<ModelInstance> Parts { get; } = new();
        public bool Loaded { get; set; }

        public WeaponRig(string gunName, string modelPath, ModelInfo info, Node3D container)
        {
            GunName = gunName;
            ModelPath = modelPath;
            Info = info;
            Container = container;
        }
    }

    private readonly Dictionary<string, WeaponRig> _weaponBank = new();
    private WeaponRig? _activeRig;
    private IModelCatalog? _models;
    private ModelInfo? _info;
    private ITextureSource? _textures;
    private IDictionary<string, Material>? _skinCache;
    private int _modelIndex;
    private int _team;
    private string _gunName = "";
    private string _currentAnim = "";
    private int _lastActionSeen = -1;

    [Export]
    public int Team
    {
        get => _team;
        set
        {
            if (_team == value) return;
            _team = value;
            ApplyTeamSkins();
        }
    }

    /// <summary>Configure before adding this node to the camera.</summary>
    public void Configure(
        IModelCatalog? models,
        ITextureSource? textures,
        int modelIndex,
        string gun,
        int team = 0,
        IDictionary<string, Material>? skinCache = null)
    {
        ClearBank();
        _models = models;
        _textures = textures;
        _modelIndex = modelIndex;
        _gunName = gun;
        _team = team;
        ModelPath = $"{Playermodels.Get(modelIndex).Hudguns}/{gun}";
        _info = models?.Get(ModelPath);
        _skinCache = skinCache ?? ModelCache.SkinCache;
        if (IsInsideTree()) BuildBank();
    }

    /// <summary>Switch to a different gun model at runtime (e.g. "shotg", "rifle").</summary>
    public void SetGun(string gun)
    {
        if (gun == _gunName && _activeRig != null) return;
        _gunName = gun;
        _currentAnim = "";
        _lastActionSeen = -1;
        if (IsInsideTree()) ActivateGun(gun);
    }

    public override void _Ready()
    {
        if (_weaponBank.Count == 0) BuildBank();
        else if (_activeRig == null && !string.IsNullOrEmpty(_gunName)) ActivateGun(_gunName);
    }

    private void ClearBank()
    {
        foreach (var rig in _weaponBank.Values)
        {
            if (GodotObject.IsInstanceValid(rig.Container))
                rig.Container.Free();
        }
        _weaponBank.Clear();
        _activeRig = null;
        _info = null;
    }

    private void ApplyTeamSkins()
    {
        foreach (var rig in _weaponBank.Values)
        {
            ApplyTeamSkinsToRig(rig);
        }
    }

    private void ApplyTeamSkinsToRig(WeaponRig rig)
    {
        if (_textures == null) return;
        var cache = _skinCache ?? ModelCache.SkinCache;
        for (int i = 0; i < rig.Parts.Count && i < rig.Info.Parts.Count; i++)
        {
            var part = rig.Parts[i];
            var partInfo = rig.Info.Parts[i];
            var material = ModelSkin.Build(partInfo, _textures, cache, _team);
            part.ApplyMaterial(material);
        }
    }

    private void BuildBank()
    {
        if (_models == null) return;
        var pm = Playermodels.Get(_modelIndex);
        string hudgunsDir = pm.Hudguns;

        // Pre-build all 7 player weapons if available in the model catalog
        for (int g = Guns.First; g <= Guns.Last; g++)
        {
            var weapon = WeaponCatalog.Get((Gun)g);
            if (string.IsNullOrEmpty(weapon.File)) continue;
            string modelPath = $"{hudgunsDir}/{weapon.File}";
            if (_models.Get(modelPath) != null)
            {
                GetOrCreateRig(weapon.File);
            }
        }

        // Also ensure requested initial _gunName is built even if not in standard Guns range
        if (!string.IsNullOrEmpty(_gunName))
        {
            GetOrCreateRig(_gunName);
            ActivateGun(_gunName);
        }
    }

    private WeaponRig? GetOrCreateRig(string gunName)
    {
        if (_weaponBank.TryGetValue(gunName, out var existing))
            return existing;

        if (_models == null) return null;
        string modelPath = $"{Playermodels.Get(_modelIndex).Hudguns}/{gunName}";
        var info = _models.Get(modelPath);
        if (info == null || info.Parts.Count == 0) return null;

        var container = new Node3D
        {
            Name = $"Rig_{gunName}",
            Visible = false,
            ProcessMode = ProcessModeEnum.Disabled,
        };
        AddChild(container);

        var rig = new WeaponRig(gunName, modelPath, info, container);
        for (int i = 0; i < info.Parts.Count; i++)
        {
            var partInfo = info.Parts[i];
            if (partInfo.GlbPath == null && partInfo.GlbOsPath == null)
            {
                GD.PushWarning($"[hudgun] model '{modelPath}' part {i} has no converted glTF");
                container.Free();
                return null;
            }

            var part = new ModelInstance
            {
                Name = $"Part{i}",
                GlbPath = partInfo.GlbPath,
                GlbOsPath = partInfo.GlbOsPath,
                KeepSkeleton = true,
                SkinMaterial = ModelSkin.Build(partInfo, _textures, _skinCache!, _team),
            };

            Node3D? parent = container;
            if (partInfo.LinkParent >= 0)
            {
                if (partInfo.LinkParent >= rig.Parts.Count || string.IsNullOrEmpty(partInfo.LinkTag) ||
                    !info.Parts[partInfo.LinkParent].Tags.TryGetValue(partInfo.LinkTag, out string? bone) ||
                    FindAnchor(rig.Parts[partInfo.LinkParent], bone) is not { } anchor)
                {
                    GD.PushWarning($"[hudgun] cannot resolve link '{partInfo.LinkTag}' for '{modelPath}' part {i}");
                    container.Free();
                    return null;
                }
                parent = anchor;
                // Part 1 glTF has its own <MD5_Root> (-90 deg X rotation). Because it is
                // parented beneath Part 0's already-transformed bone, invert that redundant
                // rotation so the weapon aligns forward with the hand.
                part.Basis = new Basis(Vector3.Right, Mathf.Pi / 2f);
            }

            parent.AddChild(part);
            if (!part.ModelLoaded) part.Reload();
            rig.Parts.Add(part);
            StartIdle(part, partInfo);
        }

        rig.Loaded = rig.Parts.Count == info.Parts.Count &&
                     rig.Parts.TrueForAll(p => p.ModelLoaded);

        _weaponBank[gunName] = rig;
        return rig;
    }

    private void ActivateGun(string gunName)
    {
        var rig = GetOrCreateRig(gunName);
        if (rig == null)
        {
            GD.PushWarning($"[hudgun] linked model '{Playermodels.Get(_modelIndex).Hudguns}/{gunName}' is missing from the model catalogue");
            if (_activeRig != null)
            {
                _activeRig.Container.Visible = false;
                _activeRig.Container.ProcessMode = ProcessModeEnum.Disabled;
                _activeRig = null;
            }
            _info = null;
            ModelPath = $"{Playermodels.Get(_modelIndex).Hudguns}/{gunName}";
            return;
        }

        if (_activeRig != null && _activeRig != rig)
        {
            _activeRig.Container.Visible = false;
            _activeRig.Container.ProcessMode = ProcessModeEnum.Disabled;
        }

        rig.Container.Visible = true;
        rig.Container.ProcessMode = ProcessModeEnum.Inherit;
        _activeRig = rig;
        _info = rig.Info;
        ModelPath = rig.ModelPath;

        ApplyTransform();

        for (int i = 0; i < rig.Parts.Count; i++)
        {
            var part = rig.Parts[i];
            var partInfo = (rig.Info != null && i < rig.Info.Parts.Count) ? rig.Info.Parts[i] : null;
            if (partInfo != null) StartIdle(part, partInfo);
        }
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
        if (_activeRig == null) return;
        var parts = _activeRig.Parts;
        var info = _activeRig.Info;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var partInfo = (info != null && i < info.Parts.Count) ? info.Parts[i] : null;
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
