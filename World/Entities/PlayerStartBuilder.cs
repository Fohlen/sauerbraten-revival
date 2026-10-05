using Godot;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Builds <see cref="SpawnPoint"/> nodes from a map's <c>ET_PLAYERSTART</c>
/// entities. attr1 is the facing yaw (physics.cpp vecfromyawpitch: forward =
/// (-sin yaw, cos yaw, 0)), attr2 the team.
/// </summary>
public static class PlayerStartBuilder
{
    public static Node3D Build(OgzMap map)
    {
        var root = new Node3D { Name = "PlayerStarts" };
        for (int i = 0; i < map.Entities.Count; i++)
        {
            var e = map.Entities[i];
            if (e.Type != EntityType.PlayerStart) continue;

            var spawn = new SpawnPoint
            {
                Name = $"PlayerStart_{i}",
                Position = e.Position,
                Team = e.Attr2,
                Yaw = e.Attr1,
            };

            Vector3 forward = Forward(e.Attr1);
            if (forward.LengthSquared() > 1e-6f)
                spawn.Basis = Basis.LookingAt(forward, new Vector3(0, 0, 1)); // Z is up in Sauer space

            root.AddChild(spawn);
        }
        return root;
    }

    /// <summary>physics.cpp vecfromyawpitch(yaw, 0, 1, 0): horizontal facing.</summary>
    public static Vector3 Forward(float yawDegrees)
    {
        float yaw = Mathf.DegToRad(yawDegrees);
        return new Vector3(-Mathf.Sin(yaw), Mathf.Cos(yaw), 0.0f);
    }
}
