using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// Builds Godot lights from a map's entity list: <c>ET_LIGHT</c> becomes an
/// <see cref="OmniLight3D"/>, or a <see cref="SpotLight3D"/> when a nearby
/// <c>ET_SPOTLIGHT</c> attaches to it (world.cpp <c>attachentity</c>).
///
/// Engine attribute layout (lightmap.cpp): attr1 = radius, attr2/3/4 = r/g/b
/// (0..255). A spotlight's attr1 is the cone's angular radius in degrees.
/// Positions/directions are in Sauerbraten coordinates; parent the result under
/// the Z-up → Y-up world node.
/// </summary>
public static class LightBuilder
{
    /// <summary>Used when the light has no attenuation radius (attr1 == 0).</summary>
    public const float InfiniteRange = 4096.0f;

    /// <summary>Direct-child name of the lights node the map node owns/updates.</summary>
    public const string LightsNodeName = "Lights";

    public static Node3D Build(OgzMap map) => Build(map.Entities);

    public static Node3D Build(IReadOnlyList<MapEntity> entities)
    {
        var root = new Node3D { Name = LightsNodeName };
        Populate(root, entities);
        return root;
    }

    /// <summary>
    /// Materializes entity lights under the direct-child <c>Lights</c> node on
    /// <paramref name="host"/> (in Sauerbraten Z-up local space). Rebuilds the lights
    /// whenever the entity list changes; idempotent.
    /// </summary>
    public static Node3D Apply(Node3D host, IReadOnlyList<MapEntity> entities)
    {
        var root = host.GetNodeOrNull<Node3D>(LightsNodeName);
        if (root == null)
        {
            root = new Node3D { Name = LightsNodeName };
            host.AddChild(root);
        }

        foreach (Node child in root.GetChildren())
        {
            root.RemoveChild(child);
            child.QueueFree();
        }

        Populate(root, entities);
        return root;
    }

    private static void Populate(Node3D root, IReadOnlyList<MapEntity> entities)
    {
        var spotlightForLight = SpotlightAttachments.ByLightIndex(entities);

        foreach (var (i, e) in entities.OfType(EntityType.Light))
        {
            Vector3 position = e.Position;
            Color color = LightColor(e);
            float range = e.Attr1 > 0 ? e.Attr1 : InfiniteRange;

            if (spotlightForLight.TryGetValue(i, out var spot))
            {
                var spotlight = new SpotLight3D
                {
                    Name = $"SpotLight_{i}",
                    Position = position,
                    LightColor = color,
                    LightEnergy = 1.0f,
                    SpotRange = range,
                    SpotAngle = Mathf.Clamp(spot.Attr1, 1, 89),
                    ShadowEnabled = false,
                };
                Vector3 direction = ((Vector3)spot.Position - position).Normalized();
                spotlight.Basis = Basis.LookingAt(direction, UpVector(direction)).ScaledLocal(new Vector3(-1, 1, 1));
                root.AddChild(spotlight);
            }
            else
            {
                root.AddChild(new OmniLight3D
                {
                    Name = $"Light_{i}",
                    Position = position,
                    LightColor = color,
                    LightEnergy = 1.0f,
                    OmniRange = range,
                    ShadowEnabled = false,
                });
            }
        }
    }

    private static Color LightColor(MapEntity e)
    {
        var color = new Color(
            Mathf.Clamp(e.Attr2 / 255.0f, 0.0f, 1.0f),
            Mathf.Clamp(e.Attr3 / 255.0f, 0.0f, 1.0f),
            Mathf.Clamp(e.Attr4 / 255.0f, 0.0f, 1.0f));
        return color.R + color.G + color.B <= 0.0f ? Colors.White : color;
    }

    // Sauerbraten is Z-up, so prefer +Z as the up hint unless the direction is
    // nearly vertical (then +Y is safer for Basis.LookingAt).
    private static Vector3 UpVector(Vector3 direction) =>
        Mathf.Abs(direction.Z) > 0.9f ? new Vector3(0, 1, 0) : new Vector3(0, 0, 1);
}
