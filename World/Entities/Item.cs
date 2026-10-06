using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// An item pickup (<c>ET_*</c> from `entmdlname`). The engine spins these in the
/// game; for now this is a static representation holding the type and its
/// converted model.
/// </summary>
[Tool]
[GlobalClass]
public partial class Item : ModelInstance
{
    [Export] public int ItemType { get; set; }
    [Export] public string ItemName { get; set; } = "";
}
