using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// A pre-processed light source ready for spatial indexing and probe raycasting (docs/adr/0002-radiance-hints.md §4.2).
/// All coordinates are in Sauerbraten space.
/// </summary>
public sealed class DirectLightSource
{
    public Vector3 Position { get; }
    public float Radius { get; }
    public Color Color { get; }
    public bool IsSpotlight { get; }
    public Vector3 SpotDirection { get; }
    public float SpotCosAngle { get; }

    public DirectLightSource(
        Vector3 position,
        float radius,
        Color color,
        bool isSpotlight = false,
        Vector3 spotDirection = default,
        float spotCosAngle = 0f)
    {
        Position = position;
        Radius = radius;
        Color = color;
        IsSpotlight = isSpotlight;
        SpotDirection = spotDirection;
        SpotCosAngle = spotCosAngle;
    }

    /// <summary>
    /// Extract all point lights and attached spotlights from an OGZ map.
    /// </summary>
    public static List<DirectLightSource> CollectFromMap(OgzMap map) => CollectFromEntities(map.Entities);

    /// <summary>
    /// Extract all point lights and attached spotlights from an entity list.
    /// </summary>
    public static List<DirectLightSource> CollectFromEntities(IReadOnlyList<MapEntity> entities)
    {
        var result = new List<DirectLightSource>();
        var spotlightForLight = SpotlightAttachments.ByLightIndex(entities);

        foreach (var (i, e) in entities.OfType(EntityType.Light))
        {
            Vector3 position = e.Position;
            Color color = new Color(
                Mathf.Clamp(e.Attr2 / 255.0f, 0.0f, 1.0f),
                Mathf.Clamp(e.Attr3 / 255.0f, 0.0f, 1.0f),
                Mathf.Clamp(e.Attr4 / 255.0f, 0.0f, 1.0f));
            if (color.R + color.G + color.B <= 0f)
                color = Colors.White;

            float radius = e.Attr1 > 0 ? e.Attr1 : LightBuilder.InfiniteRange;

            if (spotlightForLight.TryGetValue(i, out var spot))
            {
                Vector3 direction = ((Vector3)spot.Position - position).Normalized();
                int angleDeg = Mathf.Clamp(spot.Attr1, 1, 89);
                float cosAngle = Mathf.Cos(Mathf.DegToRad(angleDeg));
                result.Add(new DirectLightSource(position, radius, color, true, direction, cosAngle));
            }
            else
            {
                result.Add(new DirectLightSource(position, radius, color));
            }
        }

        return result;
    }
}

/// <summary>
/// Coarse 3D grid spatial index for direct entity lights (docs/adr/0002-radiance-hints.md §4.2, §7).
/// Accelerates probe light queries by pruning lights outside cell influence.
/// </summary>
public sealed class LightSpatialIndex
{
    private readonly Vector3 _origin;
    private readonly float _cellSize;
    private readonly int _dimX;
    private readonly int _dimY;
    private readonly int _dimZ;
    private readonly List<DirectLightSource>[] _cells;
    private readonly List<DirectLightSource> _infiniteLights = new();

    public LightSpatialIndex(Aabb boundsSauer, IReadOnlyList<DirectLightSource> lights, float targetCellSize = 128f)
    {
        _origin = boundsSauer.Position;
        _cellSize = Mathf.Max(16f, targetCellSize);
        _dimX = Mathf.Max(1, Mathf.CeilToInt(boundsSauer.Size.X / _cellSize));
        _dimY = Mathf.Max(1, Mathf.CeilToInt(boundsSauer.Size.Y / _cellSize));
        _dimZ = Mathf.Max(1, Mathf.CeilToInt(boundsSauer.Size.Z / _cellSize));

        _cells = new List<DirectLightSource>[_dimX * _dimY * _dimZ];

        foreach (var light in lights)
        {
            if (light.Radius >= LightBuilder.InfiniteRange)
            {
                _infiniteLights.Add(light);
                continue;
            }

            // AABB of the light sphere
            Vector3 min = light.Position - new Vector3(light.Radius, light.Radius, light.Radius);
            Vector3 max = light.Position + new Vector3(light.Radius, light.Radius, light.Radius);

            Vector3 gridMax = _origin + new Vector3(_dimX * _cellSize, _dimY * _cellSize, _dimZ * _cellSize);
            if (max.X < _origin.X || min.X > gridMax.X ||
                max.Y < _origin.Y || min.Y > gridMax.Y ||
                max.Z < _origin.Z || min.Z > gridMax.Z)
            {
                continue;
            }

            int minX = Mathf.Clamp(Mathf.FloorToInt((min.X - _origin.X) / _cellSize), 0, _dimX - 1);
            int maxX = Mathf.Clamp(Mathf.FloorToInt((max.X - _origin.X) / _cellSize), 0, _dimX - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt((min.Y - _origin.Y) / _cellSize), 0, _dimY - 1);
            int maxY = Mathf.Clamp(Mathf.FloorToInt((max.Y - _origin.Y) / _cellSize), 0, _dimY - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt((min.Z - _origin.Z) / _cellSize), 0, _dimZ - 1);
            int maxZ = Mathf.Clamp(Mathf.FloorToInt((max.Z - _origin.Z) / _cellSize), 0, _dimZ - 1);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        int idx = x + _dimX * (y + _dimY * z);
                        var list = _cells[idx];
                        if (list == null)
                        {
                            list = new List<DirectLightSource>();
                            _cells[idx] = list;
                        }
                        list.Add(light);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Query candidate lights affecting a given position in Sauerbraten space.
    /// </summary>
    public void QueryLights(Vector3 positionSauer, List<DirectLightSource> results)
    {
        results.Clear();
        results.AddRange(_infiniteLights);

        int x = Mathf.FloorToInt((positionSauer.X - _origin.X) / _cellSize);
        int y = Mathf.FloorToInt((positionSauer.Y - _origin.Y) / _cellSize);
        int z = Mathf.FloorToInt((positionSauer.Z - _origin.Z) / _cellSize);

        if (x >= 0 && x < _dimX && y >= 0 && y < _dimY && z >= 0 && z < _dimZ)
        {
            int idx = x + _dimX * (y + _dimY * z);
            var list = _cells[idx];
            if (list != null)
            {
                results.AddRange(list);
            }
        }
    }
}
