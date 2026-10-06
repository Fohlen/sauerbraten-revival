using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// <c>ET_JUMPPAD</c>: launches an entity. <see cref="Push"/> is the initial
/// velocity: fpsgame/entities.cpp computes
/// <c>((sbyte)attr3*10, (sbyte)attr2*10, attr1*12.5)</c>. <c>attr4</c> is the sound.
/// Behaviour is not ported yet — this only represents the entity.
/// </summary>
[GlobalClass]
public partial class JumpPad : Node3D
{
    [Export] public Vector3 Push { get; set; }
    [Export] public int Sound { get; set; }
}
