using Godot;

namespace SauerbratenRevival.World.Models;

/// <summary>
/// A player's third-person view weapon (render.cpp <c>vweps[]</c>, e.g. <c>vwep/rifle</c>).
/// The engine attaches it to the body's <c>tag_weapon</c> bone in <c>renderplayer</c>
/// (<c>modelattach("tag_weapon", …)</c>); here <see cref="PlayerModel"/> parents it under
/// the imported model's <c>Weapon</c> attachment point (a <see cref="BoneAttachment3D"/> when
/// that name is a skin joint, otherwise the glTF importer's plain <c>Weapon</c> node).
///
/// The model is rigid — it has no <c>.md5anim</c> clips of its own; it follows the
/// body's <c>hold N</c>/<c>attack N</c> animation through the attachment point. Representation
/// only for now; the weapon-selection/hold/attack state machine lands with Combat.
/// </summary>
[GlobalClass]
public partial class PlayerViewWeapon : ModelInstance
{
    /// <summary>The engine model name (e.g. "vwep/rifle").</summary>
    [Export] public string? ModelPath { get; set; }

    private Vector3 _positionOffset;

    /// <summary>
    /// Additional anchor-space position offset applied to the internal <c>Model</c> node.
    /// Used alongside <see cref="ModelInstance.ModelRotationDegrees"/> to align the weapon
    /// grip with the player character's hand bone.
    /// </summary>
    [Export]
    public Vector3 PositionOffset
    {
        get => _positionOffset;
        set
        {
            _positionOffset = value;
            if (GetNodeOrNull<Node3D>("Model") is { } holder)
                holder.Position = ModelOffset();
        }
    }

    /// <inheritdoc/>
    protected override Vector3 ModelOffset() => base.ModelOffset() + _positionOffset;

    /// <summary>
    /// Anchor-space position offsets for imported third-person view weapon GLBs.
    /// Can be customized per-weapon if needed; defaults to zero.
    /// </summary>
    public static Vector3 GetBaseOffset(string? modelPath) => Vector3.Zero;

    /// <summary>
    /// Model-space rotation corrections for imported third-person view weapon GLBs.
    /// In Sauerbraten r6889, unskinned vwep meshes had no animations and were drawn
    /// directly from pre-evaluated bind vertices without joint transforms.
    /// The converted MD5 GLBs retain their Root bone orientations:
    /// - <c>vwep/rifle</c> has its roll axis rotated (requiring -90° X rotation to stand upright).
    /// </summary>
    public static Vector3 GetBaseRotationDegrees(string? modelPath) => modelPath switch
    {
        "vwep/rifle" or "rifle" => new Vector3(-90f, 0f, 0f),
        _ => Vector3.Zero,
    };
}
