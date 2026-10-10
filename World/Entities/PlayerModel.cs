using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// The local player's visible body model — one of the five skeletal MD5 models in
/// <see cref="Playermodels"/> (render.cpp <c>playermodels</c>). It is the third-person
/// counterpart to the first-person camera: in first person <see cref="MapPlayer"/>
/// hides it, in third person the detached camera looks at it.
///
/// The glTF is loaded <b>with its skeleton</b> (see <see cref="ModelInstance.KeepSkeleton"/>).
/// The model's <c>.md5anim</c> clips are baked into the glb at import time
/// (<c>tools/model-import</c>), so Godot's own importer produces an
/// <see cref="AnimationPlayer"/> whose clips are named per the engine's <c>anims.cfg</c>
/// table ("idle", "forward", "hold 1", "attack 1", …). We do <b>not</b> map engine
/// poses onto Godot's skeleton ourselves — that deforms the mesh; see
/// docs/character-animation.md.
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

    /// <summary>True once the baked glb supplied an <see cref="AnimationPlayer"/> with clips.</summary>
    public bool Animated => _player != null && _player.GetAnimationList().Length > 0;

    private AnimationPlayer? _player;
    private readonly Dictionary<string, float> _animSpeeds = new();

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
        // The baked glb carries assimp's glTF root matrix (Y-up→Z-up); the full
        // node hierarchy is kept, so this correction stands the body upright in the
        // Z-up octree world (verified against the flattened reference). Facing is the
        // player yaw (engine renders the body at yaw+90).
        ModelRotationDegrees = new Vector3(0f, -90f, 0f);
        KeepSkeleton = true;
        // Engine playback speeds (anims.cfg fps) for the speed scale.
        _animSpeeds.Clear();
        if (info != null)
            foreach (var (animName, fps) in info.AnimSpeeds) _animSpeeds[animName] = fps;
        SkinMaterial = ModelSkin.Build(info, textures, skinCache ?? new Dictionary<string, Material>());

        if (IsInsideTree()) Reload();
    }

    public override void _Ready()
    {
        base._Ready();
        if (ModelLoaded) BindAnimations();
    }

    public override void Reload()
    {
        base.Reload();
        if (ModelLoaded) BindAnimations();
    }

    /// <summary>
    /// Grab the baked <see cref="AnimationPlayer"/> from the imported glb and start
    /// the idle clip. No-op for models whose glb has no baked clips.
    /// </summary>
    private void BindAnimations()
    {
        _player = null;
        CurrentAnim = null;

        // The AnimationPlayer sits somewhere in the imported glTF subtree (below the
        // "Model" holder this node owns).
        _player = FindAnimationPlayer(this);
        if (_player == null) return;
        if (_player.HasAnimation("idle")) Play("idle");
    }

    private static AnimationPlayer? FindAnimationPlayer(Node? n)
    {
        if (n == null) return null;
        if (n is AnimationPlayer ap) return ap;
        foreach (var c in n.GetChildren())
            if (FindAnimationPlayer(c) is { } found) return found;
        return null;
    }

    /// <summary>Play a named clip (engine <c>anims.cfg</c> name, e.g. "forward", "attack 3").</summary>
    public void Play(string name, bool loop = true)
    {
        if (_player == null || !_player.HasAnimation(name)) return;
        if (CurrentAnim == name && _player.IsPlaying()) return;
        var anim = _player.GetAnimation(name);
        anim.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        _player.SpeedScale = SpeedScaleFor(name);
        _player.Play(name);
        CurrentAnim = name;
    }

    /// <summary>
    /// Playback speed so the clip advances at the engine's <c>anims.cfg</c> fps
    /// (animmodel: <c>info.speed = 1000/fps</c> ms/frame; 0/absent = engine default,
    /// 100 ms/frame = 10 fps). Godot's baked clip advances at the <c>.md5anim</c>'s
    /// authored frameRate, so scale by engine-fps / authored-fps.
    /// </summary>
    private float SpeedScaleFor(string name)
    {
        float engineFps = _animSpeeds.TryGetValue(name, out var s) && s > 0f ? s : DefaultAnimFps;
        return engineFps / AuthoredFps(name);
    }

    /// <summary>Frames per second baked into the clip (from its key count and length).</summary>
    private float AuthoredFps(string name)
    {
        if (_player == null || !_player.HasAnimation(name)) return 1f;
        var anim = _player.GetAnimation(name);
        int keys = anim.GetTrackCount() > 0 ? anim.TrackGetKeyCount(0) : 0;
        return keys >= 2 && anim.Length > 0.0 ? (float)((keys - 1) / anim.Length) : 1f;
    }

    /// <summary>Engine default when a clip specifies no fps (100 ms/frame).</summary>
    private const float DefaultAnimFps = 10f;
}
