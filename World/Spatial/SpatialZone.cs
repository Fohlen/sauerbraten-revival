using System;
using Godot;

namespace SauerbratenRevival.World.Spatial;

/// <summary>
/// A geometric volume ("zone") registered in <see cref="WorldSpatialIndex"/> that can represent:
/// <list type="bullet">
///   <item><description>CTF / Protect / Hold base zones (cylinder: radius 64, height ±24)</description></item>
///   <item><description>CTF flag pickup / return / score touch zones (sphere: radius 16)</description></item>
///   <item><description>Item pickup zones (sphere: radius 12)</description></item>
///   <item><description>Teleporter (radius 16) and jump pad (radius 12) triggers</description></item>
///   <item><description>Custom mode objective volumes (checkpoints, hills, rugby goals)</description></item>
/// </list>
/// Supports both <b>event-based</b> notifications (<see cref="Entered"/>, <see cref="Stayed"/>, <see cref="Exited"/>)
/// driven by <see cref="WorldSpatialIndex.StepBroadphase"/> and <b>polling-based</b> queries
/// (<c>WorldSpatialIndex.QueryZoneOccupants</c>, <c>WorldSpatialIndex.QueryPointZones</c>).
/// All coordinates are in Sauerbraten Z-up world space.
/// </summary>
public sealed class SpatialZone
{
    /// <summary>Handle assigned when registered in a <see cref="WorldSpatialIndex"/>.</summary>
    public SpatialHandle Handle { get; internal set; } = SpatialHandle.Invalid;

    /// <summary>Zone category bitmask (<see cref="SpatialCategory.BaseZone"/>, <see cref="SpatialCategory.FlagZone"/>, etc.).</summary>
    public SpatialCategory Category { get; set; }

    /// <summary>Mask of dynamic entity categories that can trigger this zone during broad-pass updates (defaults to <see cref="SpatialCategory.Player"/>).</summary>
    public SpatialCategory TargetMask { get; set; } = SpatialCategory.Player;

    /// <summary>Geometric shape of the zone.</summary>
    public SpatialShapeKind Shape { get; set; }

    /// <summary>How candidate entities are tested against this zone (defaults to <see cref="SpatialZoneTriggerMode.FeetPos"/>).</summary>
    public SpatialZoneTriggerMode TriggerMode { get; set; } = SpatialZoneTriggerMode.FeetPos;

    /// <summary>World-space origin of the zone.</summary>
    public Vector3 Origin { get; set; }

    /// <summary>Local center offset from <see cref="Origin"/>.</summary>
    public Vector3 CenterOffset { get; set; }

    /// <summary>
    /// Shape dimensions:
    /// <list type="bullet">
    ///   <item><description><see cref="SpatialShapeKind.Sphere"/>: <c>X</c> = radius</description></item>
    ///   <item><description><see cref="SpatialShapeKind.Cylinder"/>: <c>X</c> = XY radius, <c>Z</c> = half-height</description></item>
    ///   <item><description><see cref="SpatialShapeKind.Aabb"/> / <see cref="SpatialShapeKind.Obb"/>: half-extents <c>(X, Y, Z)</c></description></item>
    /// </list>
    /// </summary>
    public Vector3 Extents { get; set; }

    /// <summary>Yaw angle in degrees around Z (for <see cref="SpatialShapeKind.Obb"/>).</summary>
    public float Yaw { get; set; }

    /// <summary>Logical identifier (e.g. flag ID, item index, trigger tag).</summary>
    public int ZoneId { get; set; } = -1;

    /// <summary>Team owner (0 = neutral, 1 = good/blue, 2 = evil/red).</summary>
    public int Team { get; set; }

    /// <summary>Whether the zone is currently active (e.g. item is spawned, flag is on ground/at base).</summary>
    public bool Active { get; set; } = true;

    /// <summary>Optional owner object (e.g. <c>FlagState</c>, <c>Item</c>, <c>Teleport</c>, <c>JumpPad</c>).</summary>
    public object? Owner { get; set; }

    /// <summary>Fired when a matching dynamic entity enters this zone during <c>WorldSpatialIndex.StepBroadphase</c>.</summary>
    public event Action<ZoneOverlapEvent>? Entered;

    /// <summary>Fired each broad-pass tick while a matching dynamic entity remains inside this zone.</summary>
    public event Action<ZoneOverlapEvent>? Stayed;

    /// <summary>Fired when a previously overlapping dynamic entity leaves this zone (or becomes inactive/removed).</summary>
    public event Action<ZoneOverlapEvent>? Exited;

    internal void RaiseOverlapEvent(in ZoneOverlapEvent evt)
    {
        switch (evt.Kind)
        {
            case ZoneOverlapKind.Entered:
                Entered?.Invoke(evt);
                break;
            case ZoneOverlapKind.Stayed:
                Stayed?.Invoke(evt);
                break;
            case ZoneOverlapKind.Exited:
                Exited?.Invoke(evt);
                break;
        }
    }

    /// <summary>Computes the tight 3D world-space AABB and 2D XY reject radius for this zone.</summary>
    public void ComputeBounds(out Vector3 min, out Vector3 max, out float rejectRadius)
    {
        Vector3 center = Origin + CenterOffset;
        switch (Shape)
        {
            case SpatialShapeKind.Sphere:
                {
                    float r = MathF.Abs(Extents.X);
                    Vector3 rv = new(r, r, r);
                    min = center - rv;
                    max = center + rv;
                    rejectRadius = new Vector2(CenterOffset.X, CenterOffset.Y).Length() + r;
                    break;
                }
            case SpatialShapeKind.Cylinder:
                {
                    float r = MathF.Abs(Extents.X);
                    float hz = MathF.Abs(Extents.Z);
                    Vector3 rv = new(r, r, hz);
                    min = center - rv;
                    max = center + rv;
                    rejectRadius = new Vector2(CenterOffset.X, CenterOffset.Y).Length() + r;
                    break;
                }
            case SpatialShapeKind.Obb:
                {
                    Vector3 ext = Extents.Abs();
                    float rad = Yaw * (MathF.PI / 180f);
                    float cos = MathF.Abs(MathF.Cos(rad));
                    float sin = MathF.Abs(MathF.Sin(rad));
                    float rx = ext.X * cos + ext.Y * sin;
                    float ry = ext.Y * cos + ext.X * sin;
                    float rz = ext.Z;
                    Vector3 rotCenter = Origin + RotateAroundZ(CenterOffset, rad);
                    min = rotCenter - new Vector3(rx, ry, rz);
                    max = rotCenter + new Vector3(rx, ry, rz);
                    rejectRadius = new Vector2(CenterOffset.X, CenterOffset.Y).Length() + new Vector2(ext.X, ext.Y).Length();
                    break;
                }
            case SpatialShapeKind.Aabb:
            default:
                {
                    Vector3 ext = Extents.Abs();
                    min = center - ext;
                    max = center + ext;
                    rejectRadius = new Vector2(MathF.Abs(CenterOffset.X) + ext.X, MathF.Abs(CenterOffset.Y) + ext.Y).Length();
                    break;
                }
        }
    }

    /// <summary>
    /// Tests whether <paramref name="point"/> (in Sauerbraten Z-up world coordinates) lies inside this zone.
    /// Uses strict radius inequality for spheres (<c>dist &lt; R</c>, matching <c>checkitems</c>) and
    /// inclusive cylindrical bounds (<c>dx² + dy² &lt;= R² &amp;&amp; |dz| &lt;= H</c>, matching <c>insidebase</c>).
    /// </summary>
    public bool ContainsPoint(Vector3 point)
    {
        Vector3 center = Origin + CenterOffset;
        switch (Shape)
        {
            case SpatialShapeKind.Sphere:
                {
                    float r = MathF.Abs(Extents.X);
                    return point.DistanceSquaredTo(center) < r * r;
                }
            case SpatialShapeKind.Cylinder:
                {
                    float dx = center.X - point.X;
                    float dy = center.Y - point.Y;
                    float dz = center.Z - point.Z;
                    float r = MathF.Abs(Extents.X);
                    float hz = MathF.Abs(Extents.Z);
                    return dx * dx + dy * dy <= r * r && MathF.Abs(dz) <= hz;
                }
            case SpatialShapeKind.Obb:
                {
                    float rad = -Yaw * (MathF.PI / 180f);
                    Vector3 local = RotateAroundZ(point - Origin, rad) - CenterOffset;
                    return MathF.Abs(local.X) <= MathF.Abs(Extents.X) &&
                           MathF.Abs(local.Y) <= MathF.Abs(Extents.Y) &&
                           MathF.Abs(local.Z) <= MathF.Abs(Extents.Z);
                }
            case SpatialShapeKind.Aabb:
            default:
                {
                    Vector3 d = (point - center).Abs();
                    return d.X <= MathF.Abs(Extents.X) &&
                           d.Y <= MathF.Abs(Extents.Y) &&
                           d.Z <= MathF.Abs(Extents.Z);
                }
        }
    }

    /// <summary>
    /// Tests whether a <see cref="SpatialEntry"/> overlaps this zone according to <see cref="TriggerMode"/>.
    /// </summary>
    public bool OverlapsEntry(in SpatialEntry entry)
    {
        return TriggerMode switch
        {
            SpatialZoneTriggerMode.FeetPos => ContainsPoint(entry.FeetPos),
            SpatialZoneTriggerMode.Origin => ContainsPoint(entry.Origin),
            SpatialZoneTriggerMode.BodyBounds => OverlapsBounds(entry.Min, entry.Max),
            _ => ContainsPoint(entry.FeetPos),
        };
    }

    /// <summary>
    /// Tests whether a 3D AABB [<paramref name="bMin"/>, <paramref name="bMax"/>] overlaps this zone's shape.
    /// </summary>
    public bool OverlapsBounds(Vector3 bMin, Vector3 bMax)
    {
        Vector3 center = Origin + CenterOffset;
        switch (Shape)
        {
            case SpatialShapeKind.Sphere:
                {
                    float cx = Math.Clamp(center.X, bMin.X, bMax.X) - center.X;
                    float cy = Math.Clamp(center.Y, bMin.Y, bMax.Y) - center.Y;
                    float cz = Math.Clamp(center.Z, bMin.Z, bMax.Z) - center.Z;
                    float r = MathF.Abs(Extents.X);
                    return cx * cx + cy * cy + cz * cz < r * r;
                }
            case SpatialShapeKind.Cylinder:
                {
                    float hz = MathF.Abs(Extents.Z);
                    if (bMax.Z < center.Z - hz || bMin.Z > center.Z + hz) return false;
                    float cx = Math.Clamp(center.X, bMin.X, bMax.X) - center.X;
                    float cy = Math.Clamp(center.Y, bMin.Y, bMax.Y) - center.Y;
                    float r = MathF.Abs(Extents.X);
                    return cx * cx + cy * cy <= r * r;
                }
            default:
                {
                    ComputeBounds(out Vector3 zMin, out Vector3 zMax, out _);
                    return bMax.X >= zMin.X && bMin.X <= zMax.X &&
                           bMax.Y >= zMin.Y && bMin.Y <= zMax.Y &&
                           bMax.Z >= zMin.Z && bMin.Z <= zMax.Z;
                }
        }
    }

    /// <summary>Creates a spherical zone (e.g. item pickup radius 12, flag touch radius 16, teleport 16, jumppad 12).</summary>
    public static SpatialZone CreateSphere(
        Vector3 origin,
        float radius,
        SpatialCategory category,
        int zoneId = -1,
        int team = 0,
        object? owner = null,
        SpatialZoneTriggerMode triggerMode = SpatialZoneTriggerMode.FeetPos) => new()
        {
            Category = category,
            Shape = SpatialShapeKind.Sphere,
            TriggerMode = triggerMode,
            Origin = origin,
            Extents = new Vector3(radius, radius, radius),
            ZoneId = zoneId,
            Team = team,
            Owner = owner,
        };

    /// <summary>Creates an upright cylindrical zone (e.g. CTF base zone: radius 64, halfHeight 24).</summary>
    public static SpatialZone CreateCylinder(
        Vector3 origin,
        float radius,
        float halfHeight,
        SpatialCategory category,
        int zoneId = -1,
        int team = 0,
        object? owner = null,
        SpatialZoneTriggerMode triggerMode = SpatialZoneTriggerMode.FeetPos) => new()
        {
            Category = category,
            Shape = SpatialShapeKind.Cylinder,
            TriggerMode = triggerMode,
            Origin = origin,
            Extents = new Vector3(radius, radius, halfHeight),
            ZoneId = zoneId,
            Team = team,
            Owner = owner,
        };

    /// <summary>Creates an axis-aligned box zone from [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static SpatialZone CreateAabb(
        Vector3 min,
        Vector3 max,
        SpatialCategory category,
        int zoneId = -1,
        int team = 0,
        object? owner = null,
        SpatialZoneTriggerMode triggerMode = SpatialZoneTriggerMode.FeetPos)
    {
        Vector3 center = (min + max) * 0.5f;
        Vector3 halfExtents = (max - min) * 0.5f;
        return new SpatialZone
        {
            Category = category,
            Shape = SpatialShapeKind.Aabb,
            TriggerMode = triggerMode,
            Origin = center,
            Extents = halfExtents.Abs(),
            ZoneId = zoneId,
            Team = team,
            Owner = owner,
        };
    }

    /// <summary>Creates a yaw-oriented box zone (e.g. rotated checkpoint gate or rugby goal).</summary>
    public static SpatialZone CreateObb(
        Vector3 origin,
        Vector3 centerOffset,
        Vector3 halfExtents,
        float yawDegrees,
        SpatialCategory category,
        int zoneId = -1,
        int team = 0,
        object? owner = null,
        SpatialZoneTriggerMode triggerMode = SpatialZoneTriggerMode.FeetPos) => new()
        {
            Category = category,
            Shape = SpatialShapeKind.Obb,
            TriggerMode = triggerMode,
            Origin = origin,
            CenterOffset = centerOffset,
            Extents = halfExtents.Abs(),
            Yaw = yawDegrees,
            ZoneId = zoneId,
            Team = team,
            Owner = owner,
        };

    private static Vector3 RotateAroundZ(Vector3 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector3(c * v.X - s * v.Y, s * v.X + c * v.Y, v.Z);
    }
}
