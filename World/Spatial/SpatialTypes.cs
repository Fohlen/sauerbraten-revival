using System;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Spatial;

/// <summary>
/// Bitmask categories for objects registered in <see cref="WorldSpatialIndex"/>.
/// Stored inline on both entries and grid cells so queries can prune entire cells
/// with a single bitwise AND before inspecting individual entries.
/// </summary>
[Flags]
public enum SpatialCategory : uint
{
    None = 0,

    /// <summary>Static collidable map models (<c>ET_MAPMODEL</c>).</summary>
    MapModel = 1u << 0,

    /// <summary>Dynamic or destructible world geometry / brush volumes.</summary>
    Geometry = 1u << 1,

    /// <summary>Alive player or bot entities (<c>ENT_PLAYER</c>).</summary>
    Player = 1u << 2,

    /// <summary>AI monsters (<c>ENT_AI</c>).</summary>
    Monster = 1u << 3,

    /// <summary>Physics bounce bodies: grenades, debris, gibs (<c>ENT_BOUNCE</c>).</summary>
    Bouncer = 1u << 4,

    /// <summary>Linear flying projectiles such as rockets.</summary>
    Projectile = 1u << 5,

    /// <summary>CTF / Protect / Hold flag interaction volumes (at spawn or dropped).</summary>
    FlagZone = 1u << 6,

    /// <summary>CTF / Protect / Hold base capture and influence volumes.</summary>
    BaseZone = 1u << 7,

    /// <summary>Item pickup trigger volumes (<c>ET_SHELLS</c>..<c>ET_QUAD</c>).</summary>
    ItemZone = 1u << 8,

    /// <summary>Map triggers (teleporters, jump pads, racing checkpoints, custom mode objectives).</summary>
    TriggerZone = 1u << 9,

    /// <summary>All living character entities (players + monsters).</summary>
    Actors = Player | Monster,

    /// <summary>All dynamically simulated physics bodies.</summary>
    DynamicBodies = Player | Monster | Bouncer | Projectile,

    /// <summary>All gameplay zones and objective triggers.</summary>
    Zones = FlagZone | BaseZone | ItemZone | TriggerZone,

    /// <summary>All categories.</summary>
    All = ~0u,
}

/// <summary>
/// Narrow-phase collision or containment shape kind for a <see cref="SpatialEntry"/>.
/// </summary>
public enum SpatialShapeKind : byte
{
    /// <summary>Axis-aligned 3D bounding box [<c>Min</c>, <c>Max</c>].</summary>
    Aabb = 0,

    /// <summary>3D sphere centered at <c>Origin + CenterOffset</c> with <c>Extents.X</c> radius.</summary>
    Sphere = 1,

    /// <summary>Upright Z-axis cylinder centered at <c>Origin + CenterOffset</c> with XY radius <c>Extents.X</c> and half-height <c>Extents.Z</c>.</summary>
    Cylinder = 2,

    /// <summary>Upright elliptical cylinder / capsule with XRadius <c>Extents.X</c>, YRadius <c>Extents.Y</c>, half-height <c>Extents.Z</c>, rotated by <c>Yaw</c>.</summary>
    Ellipse = 3,

    /// <summary>Yaw-oriented 3D bounding box centered at <c>Origin + RotateZ(CenterOffset, Yaw)</c> with half-extents <c>Extents</c>.</summary>
    Obb = 4,
}

/// <summary>
/// Determines how a <see cref="SpatialZone"/> tests whether a dynamic entity overlaps it.
/// </summary>
public enum SpatialZoneTriggerMode : byte
{
    /// <summary>
    /// Tests the entity's feet position <c>(Origin.X, Origin.Y, Min.Z)</c> against the zone shape.
    /// Matches Sauerbraten's <c>entities.cpp</c> (<c>checkitems</c>) and <c>ctf.h</c> (<c>checkitems</c> / <c>insidebase</c>).
    /// </summary>
    FeetPos = 0,

    /// <summary>Tests the entity's <c>Origin</c> (eye/center position) against the zone shape.</summary>
    Origin = 1,

    /// <summary>Tests the entity's full 3D bounding volume [<c>Min</c>, <c>Max</c>] against the zone shape.</summary>
    BodyBounds = 2,

    /// <summary>Alias for <see cref="FeetPos"/>.</summary>
    FeetPoint = FeetPos,

    /// <summary>Alias for <see cref="Origin"/>.</summary>
    OriginPoint = Origin,
}

/// <summary>
/// Transition kind emitted when a dynamic entity interacts with a <see cref="SpatialZone"/> during broad-pass evaluation.
/// </summary>
public enum ZoneOverlapKind : byte
{
    Entered = 0,
    Stayed = 1,
    Exited = 2,
}

/// <summary>
/// Event payload describing an interaction between a <see cref="SpatialZone"/> and a dynamic entity.
/// </summary>
public readonly struct ZoneOverlapEvent
{
    public ZoneOverlapKind Kind { get; init; }
    public SpatialZone Zone { get; init; }
    public SpatialHandle ZoneHandle { get; init; }
    public SpatialHandle EntityHandle { get; init; }
    public SpatialCategory EntityCategory { get; init; }
    public int EntityUserId { get; init; }
    public int EntityTeam { get; init; }
    public Vector3 EntityOrigin { get; init; }
    public Vector3 EntityFeetPos { get; init; }
    public object? EntityPayload { get; init; }
}

/// <summary>
/// Lightweight, generational handle referencing an entry in <see cref="WorldSpatialIndex"/>.
/// Storing a monotonic generation counter prevents stale handles from referring to newly
/// allocated entries when slot indices are recycled.
/// </summary>
public readonly record struct SpatialHandle(int Id, uint Generation)
{
    public static readonly SpatialHandle Invalid = new(-1, 0);
    public bool IsValid => Id >= 0 && Generation > 0;
}

/// <summary>
/// Explicit result for buffer-backed spatial queries.
/// Informs the caller of both the number of items written to the buffer and whether
/// the results were truncated due to insufficient buffer capacity.
/// </summary>
public readonly record struct SpatialQueryResult(int Count, int TotalMatches)
{
    /// <summary>
    /// True if the number of matching entries exceeded the destination buffer length.
    /// When true, <see cref="TotalMatches"/> indicates the required capacity to hold all matches.
    /// </summary>
    public bool IsTruncated => TotalMatches > Count;
}

/// <summary>
/// Contiguous value-type record stored in <see cref="WorldSpatialIndex"/>.
/// Holds the 3D world-space AABB and narrow-phase shape parameters inline so broad-phase
/// and Z-interval pruning happen in L1/L2 cache without dereferencing managed heap objects.
/// </summary>
public struct SpatialEntry
{
    /// <summary>Dense index in the spatial index entry table.</summary>
    public int Id;

    /// <summary>Monotonic generation counter for handle validation.</summary>
    public uint Generation;

    /// <summary>Category bitmask for fast query filtering.</summary>
    public SpatialCategory Category;

    /// <summary>Narrow-phase shape type.</summary>
    public SpatialShapeKind Shape;

    /// <summary>True if registered in the static layer; false if registered in the dynamic layer.</summary>
    public bool IsStatic;

    /// <summary>True while this entry is active and participates in queries (e.g. spawned item, alive player).</summary>
    public bool Active;

    /// <summary>True if this entry is currently allocated (false when on the free list).</summary>
    public bool Allocated;

    /// <summary>Optional user/entity ID (e.g. player ID, entity index) for zero-dereference filtering.</summary>
    public int UserId;

    /// <summary>Optional team affiliation (0 = neutral, 1 = good/blue, 2 = evil/red).</summary>
    public int Team;

    /// <summary>World-space reference origin in Sauerbraten Z-up coordinates.</summary>
    public Vector3 Origin;

    /// <summary>Tight world-space 3D AABB minimum corner.</summary>
    public Vector3 Min;

    /// <summary>Tight world-space 3D AABB maximum corner.</summary>
    public Vector3 Max;

    /// <summary>Local center offset relative to <see cref="Origin"/> (used by map models).</summary>
    public Vector3 CenterOffset;

    /// <summary>Shape half-extents or radii (X = radius/xr, Y = yr, Z = half-height/zr).</summary>
    public Vector3 Extents;

    /// <summary>Yaw rotation in degrees around Z (Sauerbraten convention).</summary>
    public float Yaw;

    /// <summary>2D XY bounding reject radius around <see cref="Origin"/> (<c>geom.h vec::reject</c>).</summary>
    public float RejectRadius;

    /// <summary>Cached quantized grid cell bounds currently occupied by this entry.</summary>
    public short CellMinX, CellMinY, CellMaxX, CellMaxY;

    /// <summary>Optional bound <see cref="PhysEnt"/> for synchronization.</summary>
    public PhysEnt? TrackedPhysEnt;

    /// <summary>Optional domain object reference (<c>MapModelCollider</c>, <c>PhysEnt</c>, etc.).</summary>
    public object? Payload;

    /// <summary>Strongly typed handle for this entry.</summary>
    public readonly SpatialHandle Handle => new(Id, Generation);

    /// <summary>Feet position <c>(Origin.X, Origin.Y, Min.Z)</c> matching <c>PhysEnt.FeetPos()</c>.</summary>
    public readonly Vector3 FeetPos => new(Origin.X, Origin.Y, Min.Z);
}
