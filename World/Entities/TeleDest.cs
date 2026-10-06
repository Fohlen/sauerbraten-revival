using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// <c>ET_TELEDEST</c>: the destination of a <see cref="Teleport"/>. Attributes:
/// <c>attr1</c> = facing yaw, <c>attr2</c> = tag (matches <see cref="Teleport.Tag"/>),
/// <c>attr3</c> &gt; 0 = keep the entity's speed along the new yaw.
/// </summary>
[GlobalClass]
public partial class TeleDest : Node3D
{
    [Export] public int Tag { get; set; }
    [Export] public float Yaw { get; set; }
    [Export] public bool Pushed { get; set; }
}
