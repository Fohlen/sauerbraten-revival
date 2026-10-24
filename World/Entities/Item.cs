using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// An item pickup (<c>ET_*</c> from `entmdlname`). The engine spins and bobs these
/// in game (fpsgame/entities.cpp renderentities); this node holds the item type, spawn state,
/// respawn countdown, base position, and pickup radius.
/// </summary>
[Tool]
[GlobalClass]
public partial class Item : Models.ModelInstance
{
    [Export] public int ItemType { get; set; }
    [Export] public int ItemIndex { get; set; }
    [Export] public string ItemName { get; set; } = "";
    [Export] public Vector3 BasePosition { get; set; }
    [Export] public bool Spawned { get; set; } = true;
    [Export] public int SpawnTime { get; set; }
    [Export] public float PickupRadius { get; set; } = 12.0f;

    public override void _Ready()
    {
        base._Ready();
        if (BasePosition == Vector3.Zero && Position != Vector3.Zero)
            BasePosition = Position;
    }

    /// <summary>
    /// Update visual bobbing and spinning matching <c>fpsgame/entities.cpp:142</c>
    /// (<c>p.z += 1 + sinf(lastmillis/100.0 + e.o.x + e.o.y)/20; yaw = lastmillis / 10.0</c>).
    /// </summary>
    public void UpdateVisual(int lastMillis)
    {
        Visible = Spawned;
        if (!Spawned)
        {
            Position = BasePosition;
            return;
        }

        float bob = 1.0f + Mathf.Sin((lastMillis / 100.0f) + BasePosition.X + BasePosition.Y) / 20.0f;
        Position = new Vector3(BasePosition.X, BasePosition.Y, BasePosition.Z + bob);
        float yaw = (lastMillis / 10.0f) % 360.0f;
        EntityFacing.ApplyYaw(this, yaw);
    }
}
