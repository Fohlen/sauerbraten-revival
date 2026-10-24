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
    /// <summary>world.cpp <c>attachradius</c> default.</summary>
    public const float AttachRadius = 100.0f;

    /// <summary>Used when the light has no attenuation radius (attr1 == 0).</summary>
    public const float InfiniteRange = 4096.0f;

    public static Node3D Build(OgzMap map) => Build(map.Entities);

    public static Node3D Build(IReadOnlyList<IEntityData> entities)
    {
        var root = new Node3D { Name = "Lights" };
        var spotlightForLight = AttachSpotlights(entities);

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
                spotlight.Basis = Basis.LookingAt(direction, UpVector(direction));
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

        return root;
    }

    /// <summary>
    /// world.cpp attachentity for ET_SPOTLIGHT: each spotlight attaches to the
    /// closest not-yet-attached light within <see cref="AttachRadius"/>.
    /// </summary>
    private static Dictionary<int, IEntityData> AttachSpotlights(IReadOnlyList<IEntityData> entities)
    {
        var result = new Dictionary<int, IEntityData>();
        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i].Type != EntityType.SpotLight) continue;

            int closest = -1;
            float closestDist = float.MaxValue;
            for (int j = 0; j < entities.Count; j++)
            {
                if (entities[j].Type != EntityType.Light || result.ContainsKey(j)) continue;
                float dist = entities[i].Position.DistanceTo(entities[j].Position);
                if (dist < closestDist) { closest = j; closestDist = dist; }
            }

            if (closest >= 0 && closestDist <= AttachRadius) result[closest] = entities[i];
        }
        return result;
    }

    private static Color LightColor(IEntityData e)
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
