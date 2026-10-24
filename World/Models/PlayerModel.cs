using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Models;

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
/// (docs/adr/0004-character-animation.md).
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

    /// <summary>The attached third-person view weapon, if any (see <see cref="Configure"/>).</summary>
    public PlayerViewWeapon? ViewWeapon { get; private set; }

    /// <summary>Engine model name of the attached view weapon (e.g. "vwep/rifle").</summary>
    public string? ViewWeaponPath { get; private set; }

    private Vector3 _viewWeaponRotationDegrees = Vector3.Zero;

    /// <summary>
    /// Extra rotation applied to the attached view weapon. The weapon is rigid and
    /// follows the body's <c>Weapon</c> bone, so this is only for aligning the
    /// imported glb's up-axis with the body; tune against the reference render.
    /// Exported so it can be nudged from the remote inspector (it re-attaches live).
    /// </summary>
    [Export]
    public Vector3 ViewWeaponRotationDegrees
    {
        get => _viewWeaponRotationDegrees;
        set
        {
            _viewWeaponRotationDegrees = value;
            if (ViewWeapon != null && IsInstanceValid(ViewWeapon)) AttachViewWeapon();
        }
    }

    /// <summary>True once the baked glb supplied an <see cref="AnimationPlayer"/> with clips.</summary>
    public bool Animated => _player != null && _player.GetAnimationList().Length > 0;

    private AnimationPlayer? _player;
    private readonly Dictionary<string, float> _animSpeeds = new();

    // View-weapon attachment state, retained so a later Reload can re-attach.
    private IModelCatalog? _models;
    private ITextureSource? _textures;
    private Dictionary<string, Material>? _skinCache;
    private string? _viewWeaponName;
    private string _weaponBone = "Weapon";

    /// <summary>
    /// Point this node at a player model's variant for <paramref name="team"/> and load
    /// it, using the world's converted-model catalogue and texture source. Safe to call
    /// before or after the node enters the tree.
    /// </summary>
    /// <param name="viewWeapon">
    /// Optional engine model name of the third-person view weapon to attach
    /// (render.cpp <c>vweps[]</c>, e.g. <c>vwep/rifle</c>), or null to attach none.
    /// </param>
    public void Configure(
        IModelCatalog? models,
        ITextureSource? textures,
        int modelIndex,
        int team,
        Dictionary<string, Material>? skinCache = null,
        string? viewWeapon = null)
    {
        ModelIndex = modelIndex;
        Team = team;
        _models = models;
        _textures = textures;
        _skinCache = skinCache;
        _viewWeaponName = viewWeapon;
        var info = models?.Get(Playermodels.Get(modelIndex).Body(team));

        ModelPath = Playermodels.Get(modelIndex).Body(team);
        // The body's md5tag maps the engine attachment point onto the baked glb's
        // bone name (mrfixit: tag_weapon -> Weapon).
        _weaponBone = info?.Tags.GetValueOrDefault("tag_weapon") ?? "Weapon";
        GlbPath = info?.GlbPath;
        GlbOsPath = info?.GlbOsPath;
        ModelScale = info?.Scale ?? 1.0f;
        // The engine renders the body at the feet (render.cpp renderclient
        // `o = d->feetpos()`) with no player mdltrans, so the holder translation is
        // just the catalog's mdltrans (usually zero).
        ModelTranslation = info?.Translation ?? Vector3.Zero;
        // The baked glb carries assimp's glTF root matrix (Y-up→Z-up); the full
        // node hierarchy is kept, so this correction stands the body upright in the
        // Z-up octree world (verified against the flattened reference). Facing is the
        // player yaw (engine renders the body at yaw+90).
        ModelRotationDegrees = new Vector3(0f, -90f, 0f);
        KeepSkeleton = true;
        // Engine playback speeds (anims.cfg fps) for the speed scale. The manifest
        // carries them when present; otherwise SpeedScaleFor falls back to the
        // built-in md5 player table below.
        _animSpeeds.Clear();
        if (info != null)
            foreach (var (animName, fps) in info.AnimSpeeds) _animSpeeds[animName] = fps;
        SkinMaterial = ModelSkin.Build(info, textures, skinCache ?? new Dictionary<string, Material>());

        if (IsInsideTree()) Reload();
    }

    /// <summary>
    /// Attach, swap or clear the third-person view weapon at runtime without reloading
    /// the body (engine <c>vweps[]</c>, render.cpp). <paramref name="viewWeapon"/> is an
    /// engine model name such as <c>vwep/rifle</c>; null/empty detaches the current one.
    /// Safe to call before the body has finished loading (the attach is retried on load).
    /// </summary>
    public void SetViewWeapon(string? viewWeapon)
    {
        _viewWeaponName = string.IsNullOrEmpty(viewWeapon) ? null : viewWeapon;
        AttachViewWeapon();
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
        if (_player != null && _player.HasAnimation("idle")) Play("idle");

        // The view weapon is independent of the body's AnimationPlayer.
        AttachViewWeapon();
    }

    /// <summary>
    /// Parent a rigid view weapon under the body skeleton's <c>Weapon</c> bone
    /// (render.cpp <c>modelattach("tag_weapon", vweps[gun])</c>). The weapon mesh is
    /// in the same imported model space as the body, so it only inherits the body
    /// holder's transform; its own <c>mdlscale</c>/<c>mdltrans</c> are divided by the
    /// body scale because the body scale is applied at the shared holder (the engine
    /// scales each model independently). Safe to call repeatedly — the previous weapon
    /// is discarded first.
    /// </summary>
    private void AttachViewWeapon()
    {
        if (ViewWeapon != null && IsInstanceValid(ViewWeapon)) ViewWeapon.Free();
        ViewWeapon = null;
        ViewWeaponPath = null;

        if (string.IsNullOrEmpty(_viewWeaponName)) return;
        var info = _models?.Get(_viewWeaponName!);
        if (info?.GlbPath == null && info?.GlbOsPath == null) return;

        // The attachment point is the body's tag_weapon bone. Godot's glTF importer
        // keeps skin joints on the Skeleton3D (resolve with a BoneAttachment3D) but
        // imports a tag bone that has no skin weights — like `Weapon` — as a plain
        // Node3D under the parent joint, so fall back to a node of that name.
        Node3D? anchor = null;
        if (Skeleton != null && Skeleton.FindBone(_weaponBone) >= 0)
        {
            var attachment = new BoneAttachment3D { Name = "WeaponBone" };
            Skeleton.AddChild(attachment);
            attachment.BoneName = _weaponBone;
            anchor = attachment;
        }
        else if (FindChild(_weaponBone, recursive: true, owned: false) is Node3D node)
        {
            anchor = node;
        }
        if (anchor == null) return;

        float bodyScale = Mathf.IsZeroApprox(ModelScale) ? 1.0f : ModelScale;
        var weapon = new PlayerViewWeapon
        {
            Name = "ViewWeapon",
            ModelPath = _viewWeaponName,
            GlbPath = info.GlbPath,
            GlbOsPath = info.GlbOsPath,
            ModelScale = info.Scale / bodyScale,
            // ModelInstance scales the translation by ModelScale (the engine applies
            // mdltrans after scale); the parent body already scales by bodyScale, and the
            // weapon's ModelScale compensates for that, so use the catalog translation as-is.
            ModelTranslation = info.Translation,
            ModelRotationDegrees = ViewWeaponRotationDegrees,
            KeepSkeleton = true,
            SkinMaterial = ModelSkin.Build(info, _textures, _skinCache ?? new Dictionary<string, Material>()),
        };
        anchor.AddChild(weapon);
        ViewWeapon = weapon;
        ViewWeaponPath = _viewWeaponName;
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
    public void Play(string name, bool loop = true, bool restart = false)
    {
        if (_player == null || !_player.HasAnimation(name)) return;
        if (!restart && CurrentAnim == name && _player.IsPlaying()) return;
        var anim = _player.GetAnimation(name);
        anim.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        _player.SpeedScale = SpeedScaleFor(name);
        if (restart && CurrentAnim == name)
            _player.Seek(0, update: true);
        _player.Play(name);
        CurrentAnim = name;
    }

    /// <summary>
    /// Playback speed so the clip advances at the engine's <c>anims.cfg</c> fps
    /// (animmodel: <c>info.speed = 1000/fps</c> ms/frame; 0/absent = engine default,
    /// 100 ms/frame = 10 fps). The baked clip spans the <c>.md5anim</c>'s authored
    /// 24 fps times (Godot's import may resample the keys but preserves the clip
    /// duration), so scale by engine-fps / authored-fps.
    /// </summary>
    private float SpeedScaleFor(string name)
    {
        float engineFps = _animSpeeds.TryGetValue(name, out var s) && s > 0f
            ? s
            : EngineAnimFps.GetValueOrDefault(name, DefaultAnimFps);
        return engineFps / AuthoredAnimFps;
    }

    /// <summary>
    /// Fallback per-clip engine playback fps for the md5 player models that share
    /// <c>packages/models/mrfixit/anims.cfg</c> (mrfixit and snoutx10k; e.g. inky and
    /// captaincannon have their own, different tables). The engine's md5anim loader
    /// keeps the last definition of a clip, so these are the final values. Clips
    /// absent here (idle, jump, edit, sink, the holds, unlisted attacks/deaths) use
    /// <see cref="DefaultAnimFps"/>. Per-model speeds from the converted manifest
    /// (<see cref="ModelInfo.AnimSpeeds"/>) take precedence when present.
    /// </summary>
    private static readonly Dictionary<string, float> EngineAnimFps = new()
    {
        ["forward"] = 125f,
        ["backward"] = 160f,
        ["left"] = 125f,
        ["right"] = 125f,
        ["swim"] = 20f,
        ["attack 2"] = 25f,   // shotgun
        ["attack 3"] = 110f,  // minigun
        ["attack 4"] = 20f,   // rocket launcher
        ["attack 5"] = 20f,   // rifle
        ["attack 6"] = 25f,   // grenade launcher
        ["win"] = 17.5f,
        ["lose"] = 17.5f,
        ["dying"] = 29f,
        ["pain"] = 66f,
        ["taunt"] = 40f,
    };

    /// <summary>Engine default when a clip specifies no fps (100 ms/frame).</summary>
    private const float DefaultAnimFps = 10f;

    /// <summary>
    /// Authored frame rate of the source <c>.md5anim</c> clips (their header is
    /// <c>frameRate 24</c>). Do <b>not</b> derive this from the imported
    /// <see cref="AnimationPlayer"/>: Godot's scene importer resamples animations to
    /// its own rate (30 fps by default, see <c>animation/fps</c> in the <c>.import</c>),
    /// which changes the key count but not the clip duration.
    /// </summary>
    private const float AuthoredAnimFps = 24f;
}
