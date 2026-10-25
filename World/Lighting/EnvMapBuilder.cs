using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// Builds <see cref="EnvMapProbe"/> nodes from a map's <c>ET_ENVMAP</c> entities.
///
/// Engine attribute layout (engine/texture.cpp <c>initenvmaps</c>):
///   attr1 = radius (0 → <c>envmapradius</c>, clamped 0..10000),
///   attr2 = cubemap size (4..9, 0 → <c>envmapsize</c>; then <c>min(size, envmapsize)</c>),
///   attr3 = blur (1..2, 0 → no blur).
///
/// In the original each one is rendered to a cubemap once and referenced by the
/// nearest geometry via <c>closestenvmap</c>. Godot's <see cref="ReflectionProbe"/>
/// is the equivalent: a box-shaped probe whose extents come from the radius. This
/// is representation only — the materials that sample the result
/// (<c>SHADER_ENVMAP</c>) are not ported yet (see docs/rendering.md).
/// </summary>
public static class EnvMapBuilder
{
    /// <summary>engine/texture.cpp <c>VARR(envmapradius, 0, 128, 10000)</c>.</summary>
    public const int DefaultRadius = 128;

    /// <summary>engine/texture.cpp <c>VARFP(envmapsize, 4, 7, 10, ...)</c>.</summary>
    public const int DefaultSize = 7;

    /// <summary>Upper bound the engine clamps a custom radius to.</summary>
    public const int MaxRadius = 10000;

    public static Node3D Build(OgzMap map) => Build(map.Entities);

    public static Node3D Build(IReadOnlyList<MapEntity> entities)
    {
        var root = new Node3D { Name = "EnvMaps" };
        foreach (var (i, e) in entities.OfType(EntityType.EnvMap))
        {
            root.AddChild(new EnvMapProbe
            {
                Name = $"EnvMap_{i}",
                Position = e.Position,
                Attr1 = e.Attr1,
                Attr2 = e.Attr2,
                Attr3 = e.Attr3,
                Radius = ResolveRadius(e),
                CubeMapSize = ResolveCubeMapSize(e),
                Blur = ResolveBlur(e),
            });
        }
        return root;
    }

    /// <summary><c>ent.attr1 ? clamp(attr1, 0, 10000) : envmapradius</c>.</summary>
    public static int ResolveRadius(MapEntity e) =>
        e.Attr1 != 0 ? Mathf.Clamp(e.Attr1, 0, MaxRadius) : DefaultRadius;

    /// <summary>
    /// <c>ent.attr2 ? clamp(attr2, 4, 9) : envmapsize</c>. The engine then folds in
    /// the user's <c>envmapsize</c> cap (<c>min(size, envmapsize)</c>); here that cap
    /// is <see cref="DefaultSize"/>, so a custom size above it is pulled back down.
    /// </summary>
    public static int ResolveCubeMapSize(MapEntity e) =>
        e.Attr2 != 0 ? Mathf.Min(Mathf.Clamp(e.Attr2, 4, 9), DefaultSize) : DefaultSize;

    /// <summary><c>ent.attr3 ? clamp(attr3, 1, 2) : 0</c>.</summary>
    public static int ResolveBlur(MapEntity e) => e.Attr3 != 0 ? Mathf.Clamp(e.Attr3, 1, 2) : 0;
}
