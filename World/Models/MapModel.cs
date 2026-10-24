using Godot;

namespace SauerbratenRevival.World.Models;

/// <summary>
/// A map model (<c>ET_MAPMODEL</c>). Position and yaw come from the entity;
/// <see cref="ModelPath"/> is the engine's registered model name, resolved through
/// the metadata catalogue. The converted glTF is loaded by <see cref="ModelInstance"/>.
/// </summary>
[Tool]
[GlobalClass]
public partial class MapModel : ModelInstance
{
    [Export] public string ModelPath { get; set; } = "";

    /// <summary>attr1: facing yaw in degrees (Sauerbraten convention).</summary>
    [Export] public float Yaw { get; set; }
}
