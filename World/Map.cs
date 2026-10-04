using Godot;

namespace SauerbratenRevival.World;

/// <summary>
/// Godot-facing world node. Owns an <see cref="Octa"/> and will host rendering,
/// editing and collision as those land (OctaRender / OctaEdit / OctaCollision).
/// </summary>
[GlobalClass]
public partial class Map : Node3D
{
    [Export] public int WorldScale { get; set; } = 10;

    public Octa World { get; private set; } = null!;

    public override void _Ready()
    {
        World = new Octa(WorldScale);
        GD.Print($"[Map] worldscale={WorldScale} worldsize={World.WorldSize} allocnodes={World.AllocNodes}");
    }
}
