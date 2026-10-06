using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>Entity facing helpers (physics.cpp <c>vecfromyawpitch</c>). Sauerbraten is Z-up.</summary>
public static class EntityFacing
{
    /// <summary>physics.cpp vecfromyawpitch(yaw, 0, 1, 0): horizontal facing.</summary>
    public static Vector3 Forward(float yawDegrees)
    {
        float yaw = Mathf.DegToRad(yawDegrees);
        return new Vector3(-Mathf.Sin(yaw), Mathf.Cos(yaw), 0.0f);
    }

    /// <summary>Orient a node along an entity yaw (a no-op for a zero/degenerate facing).</summary>
    public static void ApplyYaw(Node3D node, float yawDegrees)
    {
        Vector3 forward = Forward(yawDegrees);
        if (forward.LengthSquared() > 1e-6f)
            node.Basis = Basis.LookingAt(forward, new Vector3(0, 0, 1));
    }
}
