using Godot;

namespace SauerbratenRevival.World.Entities;

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
}
