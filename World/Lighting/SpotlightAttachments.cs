using System.Collections.Generic;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// world.cpp <c>attachentity</c> for <c>ET_SPOTLIGHT</c>: each spotlight attaches to
/// the closest not-yet-attached <c>ET_LIGHT</c> within <see cref="AttachRadius"/>.
///
/// Shared by the scene builder (<see cref="LightBuilder"/>) and the radiance bake's
/// direct-light extraction (<see cref="DirectLightSource"/>), which must resolve
/// attachments identically or a baked probe would not match the real-time light.
/// </summary>
public static class SpotlightAttachments
{
    /// <summary>world.cpp <c>attachradius</c> default.</summary>
    public const float AttachRadius = 100.0f;

    /// <summary>
    /// Resolve the attachments for an entity list, keyed by the attached
    /// <c>ET_LIGHT</c>'s entity index (the value is the spotlight entity).
    /// </summary>
    public static Dictionary<int, MapEntity> ByLightIndex(IReadOnlyList<MapEntity> entities)
    {
        var result = new Dictionary<int, MapEntity>();
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
}
