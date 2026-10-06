using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// A player spawn point (<c>ET_PLAYERSTART</c>): team and facing come straight
/// from the map entity. Positioned in Sauerbraten coordinates; meant to live
/// under the Z-up → Y-up world node. The visual (blue marker + facing arrow) is
/// added by <see cref="PlayerStartBuilder"/> via <see cref="EntityMarker"/>.
/// </summary>
[GlobalClass]
public partial class SpawnPoint : Marker3D
{
    /// <summary>attr2: 0 for non-team modes, 1 or 2 for CTF teams.</summary>
    [Export] public int Team { get; set; }

    /// <summary>attr1: facing yaw in degrees (Sauerbraten convention).</summary>
    [Export] public float Yaw { get; set; }
}
