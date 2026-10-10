using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// The local player's visible body model — one of the five skeletal MD5 models in
/// <see cref="Playermodels"/> (render.cpp <c>playermodels</c>). It is the third-person
/// counterpart to the first-person camera: in first person <see cref="MapPlayer"/>
/// hides it, in third person the detached camera looks at it.
///
/// The glTF is loaded <b>with its skeleton</b> (see <see cref="ModelInstance.KeepSkeleton"/>),
/// and the original <c>.md5anim</c> clips are parsed by <see cref="Md5Anim"/> into a
/// Godot <see cref="AnimationPlayer"/>. The clips are named per the engine's
/// <c>anims.cfg</c> table ("idle", "forward", "hold 1", "attack 1", …).
/// </summary>
[GlobalClass]
public partial class PlayerModel : ModelInstance
{
    /// <summary>Engine model name actually loaded (e.g. "mrfixit/blue"). Diagnostics only.</summary>
    [Export] public string? ModelPath { get; set; }

    /// <summary>Selected table index (engine <c>playermodel</c>, 0..4).</summary>
    [Export] public int ModelIndex { get; set; }

    /// <summary>Team skin (0 = FFA, 1 = blue, 2 = red). Diagnostics only.</summary>
    [Export] public int Team { get; set; }

    /// <summary>Animation currently playing, if any.</summary>
    public string? CurrentAnim { get; private set; }

    /// <summary>True once the AnimationPlayer has at least one clip.</summary>
    public bool Animated => _player != null && _player.GetAnimationList().Length > 0;

    private AnimationPlayer? _player;

    /// <summary>
    /// Point this node at a player model's variant for <paramref name="team"/> and load
    /// it, using the world's converted-model catalogue and texture source. Safe to call
    /// before or after the node enters the tree.
    /// </summary>
    public void Configure(
        ModelCatalog? models,
        ITextureSource? textures,
        int modelIndex,
        int team,
        Dictionary<string, Material>? skinCache = null)
    {
        ModelIndex = modelIndex;
        Team = team;
        var info = models?.Get(Playermodels.Get(modelIndex).Body(team));

        ModelPath = Playermodels.Get(modelIndex).Body(team);
        GlbPath = info?.GlbPath;
        GlbOsPath = info?.GlbOsPath;
        ModelScale = info?.Scale ?? 1.0f;
        // The catalog's mdltrans, plus a fixed nudge that plants the feet on the floor.
        // Assimp's MD5 export puts the model origin at mid-height, so the body needs to
        // be lifted by half its height: +10 in the model's local frame (before the -90
        // about X below). The engine has no such offset — it relies on the MD5 loader's
        // bind-pose handling, which assimp does not reproduce.
        ModelTranslation = (info?.Translation ?? Vector3.Zero) + new Vector3(0f, 0f, 10f);
        // Assimp exports the MD5 body through a glTF root node carrying its own
        // Y-up→Z-up matrix. The flattened load path (CopyMeshes) drops that matrix;
        // the skeleton-preserving path keeps the full hierarchy, so the extra rotation
        // must be cancelled here — (0,-90,0) restores the exact flattened orientation
        // (the -90 about X below then stands the body in the Z-up octree world).
        ModelRotationDegrees = new Vector3(0f, -90f, 0f);
        // Keep the glTF hierarchy so the Skeleton3D survives; drives the animation.
        KeepSkeleton = true;
        SkinMaterial = ModelSkin.Build(info, textures, skinCache ?? new Dictionary<string, Material>());

        if (IsInsideTree()) Reload();
    }

    public override void _Ready()
    {
        base._Ready();
        if (ModelLoaded) BuildAnimations();
    }

    public override void Reload()
    {
        base.Reload();
        if (ModelLoaded) BuildAnimations();
    }

    /// <summary>
    /// Build the <see cref="AnimationPlayer"/> from the model's <c>.md5anim</c> clips
    /// (recorded in the manifest) and start the idle clip. No-op for models without a
    /// skeleton or with no clips available.
    /// </summary>
    private void BuildAnimations()
    {
        _player?.QueueFree();
        _player = null;
        CurrentAnim = null;

        if (Skeleton == null || _animClips.Count == 0) return;

        var skeleton = Skeleton;
        var bind = Md5MeshBind.Parse(_meshOsPath);
        if (bind == null) { GD.PushWarning($"[player-model] bind pose not found: {_meshOsPath}"); return; }

        var byName = new Dictionary<string, int>();
        for (int i = 0; i < skeleton.GetBoneCount(); i++) byName[skeleton.GetBoneName(i)] = i;
        // Godot bone tracks use "<skeleton path>:<bone name>", so resolve by name.
        System.Func<string, string?> boneName = n => byName.ContainsKey(n) ? n : null;
        System.Func<string, string?> boneParentName = n =>
        {
            if (!byName.TryGetValue(n, out var i)) return null;
            int p = skeleton.GetBoneParent(i);
            return p >= 0 ? skeleton.GetBoneName(p) : null;
        };
        System.Func<string, Transform3D?> boneRest = n =>
            byName.TryGetValue(n, out var i) ? skeleton.GetBoneRest(i) : null;

        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        AddChild(player);
        // Animation tracks resolve relative to the player's RootNode (default ".." =
        // this model). Compute the skeleton path from there, not from the player node.
        var root = player.GetNode(player.RootNode);
        NodePath skeletonPath = root.GetPathTo(skeleton);
        // One default library so clips play by bare name (Play("idle")), matching the
        // engine's anims.cfg names.
        var lib = new AnimationLibrary();
        foreach (var (name, animPath) in _animClips)
        {
            var md5 = Md5Anim.Parse(animPath, bind.Names);
            if (md5 == null) continue;
            lib.AddAnimation(name, md5.BuildAnimation(bind, skeletonPath, boneName, boneParentName, boneRest));
        }
        player.AddAnimationLibrary("", lib);
        _player = player;
        if (player.HasAnimation("idle")) Play("idle");
    }

    /// <summary>Play a named clip (engine <c>anims.cfg</c> name, e.g. "forward", "attack 3").</summary>
    public void Play(string name, bool loop = true)
    {
        if (_player == null || !_player.HasAnimation(name)) return;
        if (CurrentAnim == name) return;
        var anim = _player.GetAnimation(name);
        anim.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        _player.Play(name);
        CurrentAnim = name;
    }

    /// <summary>Root node path the animation tracks target (the AnimationPlayer's parent).</summary>

    // ---- configuration captured in Configure, consumed by BuildAnimations ----

    private readonly List<(string Name, string Path)> _animClips = new();
    private string? _meshOsPath;

    /// <summary>Supply the local .md5mesh/.md5anim paths (resolved by the viewer from the reference dump).</summary>
    public void SetAnimSources(string meshOsPath, IEnumerable<KeyValuePair<string, string>> anims, string referenceRoot)
    {
        _meshOsPath = meshOsPath;
        _animClips.Clear();
        foreach (var kv in anims)
            _animClips.Add((kv.Key, System.IO.Path.Combine(referenceRoot, "packages", kv.Value)));
    }
}
