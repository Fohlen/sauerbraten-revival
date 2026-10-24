using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Builds <see cref="SpawnPoint"/> nodes from a map's <c>ET_PLAYERSTART</c>
/// entities. attr1 is the facing yaw (physics.cpp vecfromyawpitch: forward =
/// (-sin yaw, cos yaw, 0)), attr2 the team.
/// </summary>
public static class PlayerStartBuilder
{
    public static Node3D Build(OgzMap map, ITextureSource? textures = null) => Build(map.Entities, textures);

    public static Node3D Build<T>(IReadOnlyList<T> entities, ITextureSource? textures = null) where T : IEntityData
    {
        var root = new Node3D { Name = "PlayerStarts" };
        foreach (var (i, e) in entities.OfType(EntityType.PlayerStart))
        {
            var spawn = new SpawnPoint
            {
                Name = $"PlayerStart_{i}",
                Position = e.Position,
                Team = e.Attr2,
                Yaw = e.Attr1,
            };
            EntityFacing.ApplyYaw(spawn, e.Attr1);
            root.AddChild(spawn);
            EntityMarker.Add(spawn, textures, arrow: true);
        }
        return root;
    }
}
