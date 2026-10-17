using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// <c>ET_TELEPORT</c>: at runtime this moves an entity to the matching
/// <see cref="TeleDest"/>. Attributes: <c>attr1</c> = destination tag,
/// <c>attr2</c> = model (&gt;0 a mapmodel, 0 the built-in teleporter, &lt;0
/// invisible), <c>attr3</c> = can_teleport hook, <c>attr4</c> = sound.
/// </summary>
[Tool]
[GlobalClass]
public partial class Teleport : ModelInstance
{
    [Export] public int Tag { get; set; }
    [Export] public int ModelIndex { get; set; }
    [Export] public int Hook { get; set; }
    [Export] public int Sound { get; set; }

    /// <summary>Resolved model path (null when the teleporter is invisible).</summary>
    [Export] public string? ModelPath { get; set; }

    [Export] public Vector3 BasePosition { get; set; }

    /// <summary>First <see cref="TeleDest"/> whose tag matches, if any.</summary>
    public TeleDest? Destination { get; set; }

    public override void _Ready()
    {
        base._Ready();
        if (BasePosition == Vector3.Zero && Position != Vector3.Zero)
            BasePosition = Position;
    }

    /// <summary>
    /// Update visual bobbing and spinning matching <c>fpsgame/entities.cpp:142-148</c>
    /// (<c>p.z += 1 + sinf(lastmillis/100.0 + e.o.x + e.o.y)/20; yaw = lastmillis / 10.0</c>).
    /// </summary>
    public void UpdateVisual(int lastMillis)
    {
        if (ModelIndex < 0) return;

        float bob = 1.0f + Mathf.Sin((lastMillis / 100.0f) + BasePosition.X + BasePosition.Y) / 20.0f;
        Position = new Vector3(BasePosition.X, BasePosition.Y, BasePosition.Z + bob);
        float yaw = (lastMillis / 10.0f) % 360.0f;
        EntityFacing.ApplyYaw(this, yaw);
    }
}

