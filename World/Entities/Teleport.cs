using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// <c>ET_TELEPORT</c>: at runtime this moves an entity to the matching
/// <see cref="TeleDest"/>. Attributes: <c>attr1</c> = destination tag,
/// <c>attr2</c> = model (&gt;0 a mapmodel, 0 the built-in teleporter, &lt;0
/// invisible), <c>attr3</c> = can_teleport hook, <c>attr4</c> = sound.
///
/// Behaviour is not ported yet — this only represents the entity.
/// </summary>
[GlobalClass]
public partial class Teleport : Node3D
{
    [Export] public int Tag { get; set; }
    [Export] public int ModelIndex { get; set; }
    [Export] public int Hook { get; set; }
    [Export] public int Sound { get; set; }

    /// <summary>Resolved model path (null when the teleporter is invisible).</summary>
    [Export] public string? ModelPath { get; set; }

    /// <summary>First <see cref="TeleDest"/> whose tag matches, if any.</summary>
    public TeleDest? Destination { get; set; }
}
