using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Weapons;

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

    /// <summary>Team skin (0 = neutral, 1 = blue, 2 = red). Diagnostics only.</summary>
    [Export] public int Team { get; set; }

    /// <summary>Animation currently playing, if any.</summary>
    public string? CurrentAnim { get; private set; }

    /// <summary>The attached third-person view weapon, if any (see <see cref="Configure"/>).</summary>
    public PlayerViewWeapon? ViewWeapon { get; private set; }

    /// <summary>Engine model name of the attached view weapon (e.g. "vwep/rifle").</summary>
    public string? ViewWeaponPath { get; private set; }

    /// <summary>Number of view weapon models currently cached in the bank.</summary>
    public int CachedViewWeaponCount => _viewWeapons.Count;

    private Vector3 _viewWeaponRotationDegrees = Vector3.Zero;

    /// <summary>
    /// Extra rotation applied to the attached view weapon. The weapon is rigid and
    /// follows the body's <c>Weapon</c> bone, so this is only for aligning the
    /// imported glb's up-axis with the body; tune against the reference render.
    /// Exported so it can be nudged from the remote inspector (it updates live).
    /// </summary>
    [Export]
    public Vector3 ViewWeaponRotationDegrees
    {
        get => _viewWeaponRotationDegrees;
        set
        {
            _viewWeaponRotationDegrees = value;
            foreach (var weapon in _viewWeapons.Values)
            {
                if (GodotObject.IsInstanceValid(weapon))
                    weapon.ModelRotationDegrees = value;
            }
        }
    }

    /// <summary>True once the baked glb supplied an <see cref="AnimationPlayer"/> with clips.</summary>
    public bool Animated => _player != null && _player.GetAnimationList().Length > 0;

    private AnimationPlayer? _player;
    private readonly Dictionary<string, float> _animSpeeds = new();
    private readonly Dictionary<string, PlayerViewWeapon> _viewWeapons = new();

    // View-weapon attachment state, retained so a later Reload can re-attach.
    private IModelCatalog? _models;
    private ITextureSource? _textures;
    private IDictionary<string, Material>? _skinCache;
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
        IDictionary<string, Material>? skinCache = null,
        string? viewWeapon = null)
    {
        ModelIndex = modelIndex;
        Team = team;
        _models = models;
        _textures = textures;
        _skinCache = skinCache ?? ModelCache.SkinCache;
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
        SkinMaterials = ModelSkin.BuildSkins(info, textures, _skinCache, team);
        SkinMaterial = SkinMaterials.Count > 0 ? SkinMaterials[0] : ModelSkin.Build(info, textures, _skinCache, team);

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
        _viewWeapons.Clear();
        ViewWeapon = null;
        ViewWeaponPath = null;
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
        if (!string.IsNullOrEmpty(_viewWeaponName))
        {
            PrebuildViewWeapons();
            AttachViewWeapon();
        }
    }

    private Node3D? GetOrCreateWeaponAnchor()
    {
        if (Skeleton != null && Skeleton.FindBone(_weaponBone) >= 0)
        {
            if (Skeleton.GetNodeOrNull<BoneAttachment3D>("WeaponBone") is { } existing)
                return existing;
            var attachment = new BoneAttachment3D { Name = "WeaponBone", BoneName = _weaponBone };
            Skeleton.AddChild(attachment);
            return attachment;
        }
        return FindChild(_weaponBone, recursive: true, owned: false) as Node3D;
    }

    /// <summary>
    /// Parent a rigid view weapon under the body skeleton's <c>Weapon</c> bone
    /// (render.cpp <c>modelattach("tag_weapon", vweps[gun])</c>). Pre-builds or retains
    /// view weapon nodes in an internal bank, toggling visibility rather than destroying
    /// and rebuilding them on weapon switches (ADR 0011).
    /// </summary>
    private void AttachViewWeapon()
    {
        if (string.IsNullOrEmpty(_viewWeaponName))
        {
            foreach (var w in _viewWeapons.Values)
                if (GodotObject.IsInstanceValid(w)) w.Visible = false;
            ViewWeapon = null;
            ViewWeaponPath = null;
            return;
        }

        var info = _models?.Get(_viewWeaponName!);
        if (info?.GlbPath == null && info?.GlbOsPath == null) return;

        var anchor = GetOrCreateWeaponAnchor();
        if (anchor == null) return;

        if (!_viewWeapons.TryGetValue(_viewWeaponName, out var weapon) || !GodotObject.IsInstanceValid(weapon))
        {
            float bodyScale = Mathf.IsZeroApprox(ModelScale) ? 1.0f : ModelScale;
            var vwepSkins = ModelSkin.BuildSkins(info, _textures, _skinCache ?? ModelCache.SkinCache);
            weapon = new PlayerViewWeapon
            {
                Name = $"ViewWeapon_{_viewWeaponName.Replace('/', '_')}",
                ModelPath = _viewWeaponName,
                GlbPath = info.GlbPath,
                GlbOsPath = info.GlbOsPath,
                ModelScale = info.Scale / bodyScale,
                ModelTranslation = info.Translation,
                ModelRotationDegrees = ViewWeaponRotationDegrees,
                KeepSkeleton = true,
                SkinMaterials = vwepSkins,
                SkinMaterial = vwepSkins.Count > 0 ? vwepSkins[0] : ModelSkin.Build(info, _textures, _skinCache ?? ModelCache.SkinCache),
            };
            anchor.AddChild(weapon);
            _viewWeapons[_viewWeaponName] = weapon;
        }

        foreach (var (name, w) in _viewWeapons)
        {
            if (GodotObject.IsInstanceValid(w))
                w.Visible = (name == _viewWeaponName);
        }

        ViewWeapon = weapon;
        ViewWeaponPath = _viewWeaponName;
    }

    /// <summary>
    /// Pre-builds all 7 standard third-person view weapons (render.cpp:158 vweps[])
    /// under the weapon anchor, keeping them ready for zero-cost switching (ADR 0011).
    /// </summary>
    public void PrebuildViewWeapons()
    {
        if (_models == null) return;
        var anchor = GetOrCreateWeaponAnchor();
        if (anchor == null) return;

        for (int g = Guns.First; g <= Guns.Last; g++)
        {
            string? vwep = WeaponCatalog.Vwep((Gun)g);
            if (string.IsNullOrEmpty(vwep) || _viewWeapons.ContainsKey(vwep)) continue;
            var info = _models.Get(vwep);
            if (info?.GlbPath == null && info?.GlbOsPath == null) continue;

            float bodyScale = Mathf.IsZeroApprox(ModelScale) ? 1.0f : ModelScale;
            var vwepSkins = ModelSkin.BuildSkins(info, _textures, _skinCache ?? ModelCache.SkinCache);
            var weapon = new PlayerViewWeapon
            {
                Name = $"ViewWeapon_{vwep.Replace('/', '_')}",
                ModelPath = vwep,
                GlbPath = info.GlbPath,
                GlbOsPath = info.GlbOsPath,
                ModelScale = info.Scale / bodyScale,
                ModelTranslation = info.Translation,
                ModelRotationDegrees = ViewWeaponRotationDegrees,
                KeepSkeleton = true,
                SkinMaterials = vwepSkins,
                SkinMaterial = vwepSkins.Count > 0 ? vwepSkins[0] : ModelSkin.Build(info, _textures, _skinCache ?? ModelCache.SkinCache),
                Visible = (vwep == _viewWeaponName),
            };
            anchor.AddChild(weapon);
            _viewWeapons[vwep] = weapon;
        }

        if (!string.IsNullOrEmpty(_viewWeaponName) && _viewWeapons.TryGetValue(_viewWeaponName, out var active))
        {
            ViewWeapon = active;
            ViewWeaponPath = _viewWeaponName;
        }
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
