using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Spatial;

/// <summary>
/// Unified world spatial index supporting:
/// <list type="bullet">
///   <item><description><b>Static init:</b> Map models and static collision volumes.</description></item>
///   <item><description><b>Dynamic entities:</b> Players, monsters, grenades/bouncers, projectiles.</description></item>
///   <item><description><b>Generational handles:</b> Validates slot versions to prevent stale references.</description></item>
///   <item><description><b>O(1) incremental dynamic updates:</b> Fast-path in-place updates when within the same cell.</description></item>
///   <item><description><b>Explicit query overflow:</b> <see cref="SpatialQueryResult"/> returns counts and truncation status.</description></item>
///   <item><description><b>Narrow-phase map model queries:</b> <see cref="CollideMapModels"/> matching Sauerbraten's mmcollide.</description></item>
/// </list>
/// </summary>
public sealed class WorldSpatialIndex
{
    public const int DefaultCellShift = 6; // 64 Sauerbraten units per cell

    private struct GridCell
    {
        public SpatialCategory StaticMask;
        public SpatialCategory DynamicMask;
        public float StaticMinZ;
        public float StaticMaxZ;
        public int[]? StaticIds;
        public int StaticCount;
        public int[]? DynamicIds;
        public int DynamicCount;
    }

    private int _worldSize;
    private readonly int _cellShift;
    private int _gridDim;
    private GridCell[] _cells;

    private SpatialEntry[] _entries;
    private uint[] _visitStamps;
    private int _entryCount;
    private int _freeHead = -1;
    private uint _queryStamp;

    // Dense tracking list for dynamic entity batch synchronization
    private int[] _trackedDynamicIds = new int[32];
    private int _trackedDynamicCount;

    /// <summary>Number of active static entries currently registered.</summary>
    public int StaticCount { get; private set; }

    /// <summary>Number of active dynamic entries currently registered.</summary>
    public int DynamicCount { get; private set; }

    /// <summary>Total allocated entries (static + dynamic).</summary>
    public int Count => StaticCount + DynamicCount;

    /// <summary>World edge length in Sauerbraten units covered by this index.</summary>
    public int WorldSize => _worldSize;

    /// <summary>Grid cell edge length in Sauerbraten units (<c>1 &lt;&lt; CellShift</c>).</summary>
    public int CellSize => 1 << _cellShift;

    /// <summary>Bit shift used to convert world coordinates into grid cell coordinates.</summary>
    public int CellShift => _cellShift;

    /// <summary>Number of cells along each X and Y axis.</summary>
    public int GridDim => _gridDim;

    /// <summary>Diagnostic counter: number of dynamic updates that stayed within the same cell bounds (O(1) fast path).</summary>
    public long InPlaceUpdates { get; private set; }

    /// <summary>Diagnostic counter: number of dynamic updates that crossed a grid cell boundary.</summary>
    public long BoundaryCrossings { get; private set; }

    public WorldSpatialIndex(int worldSize = 1024, int cellShift = DefaultCellShift, int initialCapacity = 128)
    {
        _cellShift = Math.Clamp(cellShift, 4, 10);
        _worldSize = Math.Max(1 << _cellShift, worldSize);
        _gridDim = Math.Max(1, _worldSize >> _cellShift);
        _cells = new GridCell[_gridDim * _gridDim];
        ResetCellZBounds();

        int cap = Math.Max(16, initialCapacity);
        _entries = new SpatialEntry[cap];
        _visitStamps = new uint[cap];
    }

    private void ResetCellZBounds()
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i].StaticMinZ = float.MaxValue;
            _cells[i].StaticMaxZ = float.MinValue;
        }
    }

    /// <summary>
    /// Ensures the spatial grid matches <paramref name="worldSize"/>, rebuilding cell buckets if the world was resized.
    /// </summary>
    public void EnsureWorldSize(int worldSize)
    {
        int clamped = Math.Max(1 << _cellShift, worldSize);
        if (clamped == _worldSize) return;

        _worldSize = clamped;
        _gridDim = Math.Max(1, _worldSize >> _cellShift);
        _cells = new GridCell[_gridDim * _gridDim];
        ResetCellZBounds();

        for (int i = 0; i < _entryCount; i++)
        {
            ref var entry = ref _entries[i];
            if (!entry.Allocated) continue;
            ComputeCellSpan(entry.Min, entry.Max, out entry.CellMinX, out entry.CellMinY, out entry.CellMaxX, out entry.CellMaxY);
            LinkEntryToCells(ref entry);
        }
    }

    /// <summary>Clears all static and dynamic entries, resets cell data and free list.</summary>
    public void Clear()
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i].StaticMask = SpatialCategory.None;
            _cells[i].DynamicMask = SpatialCategory.None;
            _cells[i].StaticMinZ = float.MaxValue;
            _cells[i].StaticMaxZ = float.MinValue;
            _cells[i].StaticCount = 0;
            _cells[i].DynamicCount = 0;
        }
        for (int i = 0; i < _entryCount; i++)
        {
            _entries[i].Allocated = false;
            _entries[i].Active = false;
            _entries[i].Generation++;
            if (_entries[i].Generation == 0) _entries[i].Generation = 1;
            _entries[i].TrackedPhysEnt = null;
            _entries[i].Payload = null;
        }
        Array.Clear(_visitStamps, 0, _entryCount);
        _entryCount = 0;
        _freeHead = -1;
        _queryStamp = 0;
        _trackedDynamicCount = 0;
        StaticCount = 0;
        DynamicCount = 0;
        InPlaceUpdates = 0;
        BoundaryCrossings = 0;
    }

    /// <summary>Returns a read-only reference to the entry for <paramref name="handle"/>.</summary>
    public ref readonly SpatialEntry GetEntry(SpatialHandle handle)
    {
        if (!IsValid(handle))
            throw new ArgumentException("Invalid or stale spatial handle.", nameof(handle));
        return ref _entries[handle.Id];
    }

    /// <summary>Returns a read-only reference to the entry at raw <paramref name="id"/> (unsafe/internal).</summary>
    public ref readonly SpatialEntry GetEntryById(int id) => ref _entries[id];

    /// <summary>Checks whether <paramref name="handle"/> refers to a currently allocated entry matching its generation.</summary>
    public bool IsValid(SpatialHandle handle) =>
        handle.Id >= 0 &&
        handle.Id < _entryCount &&
        _entries[handle.Id].Allocated &&
        _entries[handle.Id].Generation == handle.Generation;

    // ---- Registration & Lifecycle -------------------------------------------

    /// <summary>
    /// Registers a static entry in the spatial index (e.g. map model, static collider).
    /// </summary>
    public SpatialHandle RegisterStatic(
        SpatialCategory category,
        SpatialShapeKind shape,
        Vector3 origin,
        Vector3 min,
        Vector3 max,
        Vector3 centerOffset = default,
        Vector3 extents = default,
        float yaw = 0f,
        float rejectRadius = 0f,
        object? payload = null,
        int userId = -1,
        int team = 0,
        bool active = true)
    {
        return RegisterInternal(
            isStatic: true,
            category,
            shape,
            origin,
            min,
            max,
            centerOffset,
            extents,
            yaw,
            rejectRadius,
            trackedPhysEnt: null,
            payload,
            userId,
            team,
            active);
    }

    /// <summary>
    /// Registers a dynamic entry in the spatial index (e.g. player, monster, bouncer, projectile).
    /// </summary>
    public SpatialHandle RegisterDynamic(
        SpatialCategory category,
        SpatialShapeKind shape,
        Vector3 origin,
        Vector3 min,
        Vector3 max,
        Vector3 centerOffset = default,
        Vector3 extents = default,
        float yaw = 0f,
        float rejectRadius = 0f,
        object? payload = null,
        int userId = -1,
        int team = 0,
        bool active = true)
    {
        return RegisterInternal(
            isStatic: false,
            category,
            shape,
            origin,
            min,
            max,
            centerOffset,
            extents,
            yaw,
            rejectRadius,
            trackedPhysEnt: null,
            payload,
            userId,
            team,
            active);
    }

    /// <summary>
    /// Registers a static <see cref="MapModelCollider"/> in the spatial index.
    /// Computes the exact 3D Z-interval and 2D XY reject bounds matching <c>physics.cpp mmcollide</c>.
    /// </summary>
    public SpatialHandle RegisterMapModel(MapModelCollider collider, int entityId = -1)
    {
        float reject = collider.RejectRadius > 0f
            ? collider.RejectRadius
            : (collider.Center.Abs() + collider.Radius).Length();
        collider.RejectRadius = reject;

        float centerZ = collider.O.Z + collider.Center.Z;
        Vector3 min = new(collider.O.X - reject, collider.O.Y - reject, centerZ - collider.Radius.Z);
        Vector3 max = new(collider.O.X + reject, collider.O.Y + reject, centerZ + collider.Radius.Z);

        SpatialShapeKind shape = collider.EllipseCollide ? SpatialShapeKind.Ellipse : SpatialShapeKind.Obb;
        var handle = RegisterStatic(
            SpatialCategory.MapModel,
            shape,
            collider.O,
            min,
            max,
            collider.Center,
            collider.Radius,
            collider.Yaw,
            reject,
            payload: collider,
            userId: entityId,
            active: collider.Collide);
        collider.SpatialHandle = handle;
        return handle;
    }

    /// <summary>
    /// Registers a <see cref="PhysEnt"/> (player, monster, or bouncer) as a tracked dynamic entry.
    /// When <see cref="StepBroadphase"/> or <see cref="SyncDynamicEntities"/> runs, its bounds are updated.
    /// </summary>
    public SpatialHandle RegisterPhysEnt(
        PhysEnt ent,
        SpatialCategory category = SpatialCategory.Player,
        object? payload = null,
        int userId = -1,
        int team = 0)
    {
        Vector3 min = new(ent.O.X - ent.Radius, ent.O.Y - ent.Radius, ent.O.Z - ent.EyeHeight);
        Vector3 max = new(ent.O.X + ent.Radius, ent.O.Y + ent.Radius, ent.O.Z + ent.AboveEye);
        Vector3 extents = new(ent.XRadius, ent.YRadius, (ent.EyeHeight + ent.AboveEye) * 0.5f);
        Vector3 centerOffset = new(0f, 0f, (ent.AboveEye - ent.EyeHeight) * 0.5f);
        bool active = ent.State == PhysEnt.CsAlive;

        var handle = RegisterInternal(
            isStatic: false,
            category,
            SpatialShapeKind.Ellipse,
            ent.O,
            min,
            max,
            centerOffset,
            extents,
            ent.Yaw,
            ent.Radius,
            trackedPhysEnt: ent,
            payload: payload ?? ent,
            userId: userId,
            team: team,
            active: active);

        AppendId(ref _trackedDynamicIds, ref _trackedDynamicCount, handle.Id);
        return handle;
    }

    private SpatialHandle RegisterInternal(
        bool isStatic,
        SpatialCategory category,
        SpatialShapeKind shape,
        Vector3 origin,
        Vector3 min,
        Vector3 max,
        Vector3 centerOffset,
        Vector3 extents,
        float yaw,
        float rejectRadius,
        PhysEnt? trackedPhysEnt,
        object? payload,
        int userId,
        int team,
        bool active)
    {
        int id = AllocateSlot();
        ComputeCellSpan(min, max, out short cMinX, out short cMinY, out short cMaxX, out short cMaxY);

        ref var entry = ref _entries[id];
        uint gen = entry.Generation;
        if (gen == 0) gen = 1;

        entry = new SpatialEntry
        {
            Id = id,
            Generation = gen,
            Category = category,
            Shape = shape,
            IsStatic = isStatic,
            Active = active,
            Allocated = true,
            UserId = userId,
            Team = team,
            Origin = origin,
            Min = min,
            Max = max,
            CenterOffset = centerOffset,
            Extents = extents,
            Yaw = yaw,
            RejectRadius = rejectRadius > 0f ? rejectRadius : MathF.Max(max.X - origin.X, max.Y - origin.Y),
            CellMinX = cMinX,
            CellMinY = cMinY,
            CellMaxX = cMaxX,
            CellMaxY = cMaxY,
            TrackedPhysEnt = trackedPhysEnt,
            Payload = payload,
        };
        _visitStamps[id] = 0;

        LinkEntryToCells(ref entry);

        if (isStatic) StaticCount++;
        else DynamicCount++;

        return new SpatialHandle(id, gen);
    }

    // ---- Dynamic Updates & Lifecycle ----------------------------------------

    /// <summary>
    /// Updates an entry's world-space origin and 3D AABB.
    /// If the quantized grid cell span has not changed, updates the AABB in-place in O(1)
    /// without modifying any grid cell lists.
    /// </summary>
    public void UpdateBounds(SpatialHandle handle, Vector3 origin, Vector3 min, Vector3 max, float? yaw = null)
    {
        if (!IsValid(handle)) return;
        ref var entry = ref _entries[handle.Id];

        entry.Origin = origin;
        entry.Min = min;
        entry.Max = max;
        if (yaw.HasValue) entry.Yaw = yaw.Value;

        ComputeCellSpan(min, max, out short newMinX, out short newMinY, out short newMaxX, out short newMaxY);
        if (newMinX == entry.CellMinX &&
            newMinY == entry.CellMinY &&
            newMaxX == entry.CellMaxX &&
            newMaxY == entry.CellMaxY)
        {
            if (entry.IsStatic)
            {
                ExpandStaticZBounds(ref entry);
            }
            InPlaceUpdates++;
            return;
        }

        BoundaryCrossings++;
        UnlinkEntryFromCells(ref entry);
        entry.CellMinX = newMinX;
        entry.CellMinY = newMinY;
        entry.CellMaxX = newMaxX;
        entry.CellMaxY = newMaxY;
        LinkEntryToCells(ref entry);
    }

    /// <summary>
    /// Translates an entry to <paramref name="newOrigin"/>, shifting its AABB by <c>newOrigin - oldOrigin</c>.
    /// </summary>
    public void UpdatePosition(SpatialHandle handle, Vector3 newOrigin, float? yaw = null)
    {
        if (!IsValid(handle)) return;
        ref var entry = ref _entries[handle.Id];
        Vector3 delta = newOrigin - entry.Origin;
        UpdateBounds(handle, newOrigin, entry.Min + delta, entry.Max + delta, yaw);
    }

    /// <summary>
    /// Synchronizes a registered <see cref="PhysEnt"/> handle with its current coordinates.
    /// </summary>
    public void UpdatePhysEnt(SpatialHandle handle, PhysEnt? entOverride = null)
    {
        if (!IsValid(handle)) return;
        ref var entry = ref _entries[handle.Id];
        PhysEnt? ent = entOverride ?? entry.TrackedPhysEnt;
        if (ent == null) return;

        entry.Active = ent.State == PhysEnt.CsAlive;
        entry.RejectRadius = ent.Radius;
        entry.Extents = new Vector3(ent.XRadius, ent.YRadius, (ent.EyeHeight + ent.AboveEye) * 0.5f);
        Vector3 min = new(ent.O.X - ent.Radius, ent.O.Y - ent.Radius, ent.O.Z - ent.EyeHeight);
        Vector3 max = new(ent.O.X + ent.Radius, ent.O.Y + ent.Radius, ent.O.Z + ent.AboveEye);
        UpdateBounds(handle, ent.O, min, max, ent.Yaw);
    }

    /// <summary>
    /// Updates the team or user ID metadata on an existing entry in O(1).
    /// </summary>
    public void SetMetadata(SpatialHandle handle, int? userId = null, int? team = null)
    {
        if (!IsValid(handle)) return;
        ref var entry = ref _entries[handle.Id];
        if (userId.HasValue) entry.UserId = userId.Value;
        if (team.HasValue) entry.Team = team.Value;
    }

    /// <summary>
    /// Enables or disables an entry in O(1) without unlinking it from its grid cells.
    /// Useful for active/inactive collision toggles, item respawns, or player state changes.
    /// </summary>
    public void SetActive(SpatialHandle handle, bool active)
    {
        if (!IsValid(handle)) return;
        _entries[handle.Id].Active = active;
    }

    /// <summary>
    /// Batch synchronizes all tracked dynamic entities with their current bounds.
    /// </summary>
    public void SyncDynamicEntities()
    {
        for (int i = 0; i < _trackedDynamicCount; i++)
        {
            int id = _trackedDynamicIds[i];
            ref var e = ref _entries[id];
            if (e.Allocated && e.TrackedPhysEnt != null)
            {
                UpdatePhysEnt(new SpatialHandle(id, e.Generation), e.TrackedPhysEnt);
            }
        }
    }

    /// <summary>Alias for <see cref="SyncDynamicEntities"/>.</summary>
    public void StepBroadphase(bool evaluateZones = false) => SyncDynamicEntities();

    /// <summary>
    /// Removes an entry from the spatial index and invalidates its handle.
    /// Increments the slot's generation counter to guarantee stale handles cannot match recycled slots.
    /// </summary>
    public bool Remove(SpatialHandle handle)
    {
        if (!IsValid(handle)) return false;
        int id = handle.Id;
        ref var entry = ref _entries[id];

        UnlinkEntryFromCells(ref entry);

        if (entry.TrackedPhysEnt != null)
        {
            RemoveIdPreserveOrder(_trackedDynamicIds, ref _trackedDynamicCount, id);
        }

        if (entry.IsStatic) StaticCount--;
        else DynamicCount--;

        if (entry.Payload is MapModelCollider mm) mm.SpatialHandle = SpatialHandle.Invalid;

        entry.Allocated = false;
        entry.Active = false;
        entry.TrackedPhysEnt = null;
        entry.Payload = null;

        // Bump generation to immediately invalidate existing handles
        entry.Generation++;
        if (entry.Generation == 0) entry.Generation = 1;

        entry.UserId = _freeHead;
        _freeHead = id;
        return true;
    }

    // ---- Spatial Queries (AABB, Radius, Segment, Map Models) ----------------

    /// <summary>
    /// Broad-phase 3D AABB overlap query.
    /// Writes matching entry IDs into <paramref name="results"/> up to its length,
    /// and returns a <see cref="SpatialQueryResult"/> indicating both the written count
    /// and the total number of matches found (allowing callers to detect truncation).
    /// Zero heap allocations.
    /// </summary>
    public SpatialQueryResult QueryAabb(Vector3 min, Vector3 max, SpatialCategory mask, Span<int> results)
    {
        if (mask == SpatialCategory.None || Count == 0)
            return new SpatialQueryResult(0, 0);

        ComputeCellSpan(min, max, out short startX, out short startY, out short endX, out short endY);
        bool multiCell = startX != endX || startY != endY;
        uint stamp = multiCell ? NextQueryStamp() : 0;

        bool checkStatic = StaticCount > 0;
        bool checkDynamic = DynamicCount > 0;

        int written = 0;
        int totalMatches = 0;

        for (int y = startY; y <= endY; y++)
        {
            int rowOffset = y * _gridDim;
            for (int x = startX; x <= endX; x++)
            {
                ref var cell = ref _cells[rowOffset + x];
                if (((cell.StaticMask | cell.DynamicMask) & mask) == 0) continue;

                if (checkStatic && (cell.StaticMask & mask) != 0 && max.Z >= cell.StaticMinZ && min.Z <= cell.StaticMaxZ)
                {
                    int sCount = cell.StaticCount;
                    int[]? sIds = cell.StaticIds;
                    for (int i = 0; i < sCount; i++)
                    {
                        int id = sIds![i];
                        if (multiCell)
                        {
                            if (_visitStamps[id] == stamp) continue;
                            _visitStamps[id] = stamp;
                        }
                        ref var e = ref _entries[id];
                        if (!e.Active || (e.Category & mask) == 0) continue;
                        if (e.Max.Z < min.Z || e.Min.Z > max.Z ||
                            e.Max.X < min.X || e.Min.X > max.X ||
                            e.Max.Y < min.Y || e.Min.Y > max.Y) continue;

                        totalMatches++;
                        if (written < results.Length)
                        {
                            results[written++] = id;
                        }
                    }
                }

                if (checkDynamic && (cell.DynamicMask & mask) != 0)
                {
                    int dCount = cell.DynamicCount;
                    int[]? dIds = cell.DynamicIds;
                    for (int i = 0; i < dCount; i++)
                    {
                        int id = dIds![i];
                        if (multiCell)
                        {
                            if (_visitStamps[id] == stamp) continue;
                            _visitStamps[id] = stamp;
                        }
                        ref var e = ref _entries[id];
                        if (!e.Active || (e.Category & mask) == 0) continue;
                        if (e.Max.Z < min.Z || e.Min.Z > max.Z ||
                            e.Max.X < min.X || e.Min.X > max.X ||
                            e.Max.Y < min.Y || e.Min.Y > max.Y) continue;

                        totalMatches++;
                        if (written < results.Length)
                        {
                            results[written++] = id;
                        }
                    }
                }
            }
        }

        return new SpatialQueryResult(written, totalMatches);
    }

    /// <summary>
    /// Convenience overload of <see cref="QueryAabb(Vector3, Vector3, SpatialCategory, Span{int})"/> appending all matches to a <see cref="List{T}"/>.
    /// Unbounded; dynamically resizes without truncation.
    /// </summary>
    public int QueryAabb(Vector3 min, Vector3 max, SpatialCategory mask, List<int> results)
    {
        results.Clear();
        if (mask == SpatialCategory.None || Count == 0) return 0;

        ComputeCellSpan(min, max, out short startX, out short startY, out short endX, out short endY);
        bool multiCell = startX != endX || startY != endY;
        uint stamp = multiCell ? NextQueryStamp() : 0;

        bool checkStatic = StaticCount > 0;
        bool checkDynamic = DynamicCount > 0;

        for (int y = startY; y <= endY; y++)
        {
            int rowOffset = y * _gridDim;
            for (int x = startX; x <= endX; x++)
            {
                ref var cell = ref _cells[rowOffset + x];
                if (((cell.StaticMask | cell.DynamicMask) & mask) == 0) continue;

                if (checkStatic && (cell.StaticMask & mask) != 0 && max.Z >= cell.StaticMinZ && min.Z <= cell.StaticMaxZ)
                {
                    int sCount = cell.StaticCount;
                    int[]? sIds = cell.StaticIds;
                    for (int i = 0; i < sCount; i++)
                    {
                        int id = sIds![i];
                        if (multiCell)
                        {
                            if (_visitStamps[id] == stamp) continue;
                            _visitStamps[id] = stamp;
                        }
                        ref var e = ref _entries[id];
                        if (!e.Active || (e.Category & mask) == 0) continue;
                        if (e.Max.Z < min.Z || e.Min.Z > max.Z ||
                            e.Max.X < min.X || e.Min.X > max.X ||
                            e.Max.Y < min.Y || e.Min.Y > max.Y) continue;

                        results.Add(id);
                    }
                }

                if (checkDynamic && (cell.DynamicMask & mask) != 0)
                {
                    int dCount = cell.DynamicCount;
                    int[]? dIds = cell.DynamicIds;
                    for (int i = 0; i < dCount; i++)
                    {
                        int id = dIds![i];
                        if (multiCell)
                        {
                            if (_visitStamps[id] == stamp) continue;
                            _visitStamps[id] = stamp;
                        }
                        ref var e = ref _entries[id];
                        if (!e.Active || (e.Category & mask) == 0) continue;
                        if (e.Max.Z < min.Z || e.Min.Z > max.Z ||
                            e.Max.X < min.X || e.Min.X > max.X ||
                            e.Max.Y < min.Y || e.Min.Y > max.Y) continue;

                        results.Add(id);
                    }
                }
            }
        }

        return results.Count;
    }

    /// <summary>
    /// Queries active entries within <paramref name="radius"/> of <paramref name="center"/> (3D sphere overlap against entry AABB).
    /// Directly traverses candidate cells without intermediate stack buffers, writing up to <paramref name="results"/>.Length
    /// and returning total matches to make overflow explicit.
    /// </summary>
    public SpatialQueryResult QueryRadius(Vector3 center, float radius, SpatialCategory mask, Span<int> results)
    {
        if (radius < 0f || mask == SpatialCategory.None || Count == 0)
            return new SpatialQueryResult(0, 0);

        Vector3 rVec = new(radius, radius, radius);
        Vector3 min = center - rVec;
        Vector3 max = center + rVec;

        ComputeCellSpan(min, max, out short startX, out short startY, out short endX, out short endY);
        bool multiCell = startX != endX || startY != endY;
        uint stamp = multiCell ? NextQueryStamp() : 0;

        float rSq = radius * radius;
        bool checkStatic = StaticCount > 0;
        bool checkDynamic = DynamicCount > 0;

        int written = 0;
        int totalMatches = 0;

        for (int y = startY; y <= endY; y++)
        {
            int rowOffset = y * _gridDim;
            for (int x = startX; x <= endX; x++)
            {
                ref var cell = ref _cells[rowOffset + x];
                if (((cell.StaticMask | cell.DynamicMask) & mask) == 0) continue;

                if (checkStatic && (cell.StaticMask & mask) != 0 && max.Z >= cell.StaticMinZ && min.Z <= cell.StaticMaxZ)
                {
                    int sCount = cell.StaticCount;
                    int[]? sIds = cell.StaticIds;
                    for (int i = 0; i < sCount; i++)
                    {
                        int id = sIds![i];
                        if (multiCell)
                        {
                            if (_visitStamps[id] == stamp) continue;
                            _visitStamps[id] = stamp;
                        }
                        ref var e = ref _entries[id];
                        if (!e.Active || (e.Category & mask) == 0) continue;
                        if (e.Max.Z < min.Z || e.Min.Z > max.Z ||
                            e.Max.X < min.X || e.Min.X > max.X ||
                            e.Max.Y < min.Y || e.Min.Y > max.Y) continue;

                        float cx = Math.Clamp(center.X, e.Min.X, e.Max.X) - center.X;
                        float cy = Math.Clamp(center.Y, e.Min.Y, e.Max.Y) - center.Y;
                        float cz = Math.Clamp(center.Z, e.Min.Z, e.Max.Z) - center.Z;
                        if (cx * cx + cy * cy + cz * cz <= rSq)
                        {
                            totalMatches++;
                            if (written < results.Length)
                            {
                                results[written++] = id;
                            }
                        }
                    }
                }

                if (checkDynamic && (cell.DynamicMask & mask) != 0)
                {
                    int dCount = cell.DynamicCount;
                    int[]? dIds = cell.DynamicIds;
                    for (int i = 0; i < dCount; i++)
                    {
                        int id = dIds![i];
                        if (multiCell)
                        {
                            if (_visitStamps[id] == stamp) continue;
                            _visitStamps[id] = stamp;
                        }
                        ref var e = ref _entries[id];
                        if (!e.Active || (e.Category & mask) == 0) continue;
                        if (e.Max.Z < min.Z || e.Min.Z > max.Z ||
                            e.Max.X < min.X || e.Min.X > max.X ||
                            e.Max.Y < min.Y || e.Min.Y > max.Y) continue;

                        float cx = Math.Clamp(center.X, e.Min.X, e.Max.X) - center.X;
                        float cy = Math.Clamp(center.Y, e.Min.Y, e.Max.Y) - center.Y;
                        float cz = Math.Clamp(center.Z, e.Min.Z, e.Max.Z) - center.Z;
                        if (cx * cx + cy * cy + cz * cz <= rSq)
                        {
                            totalMatches++;
                            if (written < results.Length)
                            {
                                results[written++] = id;
                            }
                        }
                    }
                }
            }
        }

        return new SpatialQueryResult(written, totalMatches);
    }

    /// <summary>
    /// Convenience overload of <see cref="QueryRadius(Vector3, float, SpatialCategory, Span{int})"/> appending all matches to a <see cref="List{T}"/>.
    /// Unbounded; dynamically resizes without truncation.
    /// </summary>
    public int QueryRadius(Vector3 center, float radius, SpatialCategory mask, List<int> results)
    {
        results.Clear();
        if (radius < 0f || mask == SpatialCategory.None || Count == 0) return 0;

        Vector3 rVec = new(radius, radius, radius);
        Vector3 min = center - rVec;
        Vector3 max = center + rVec;

        ComputeCellSpan(min, max, out short startX, out short startY, out short endX, out short endY);
        bool multiCell = startX != endX || startY != endY;
        uint stamp = multiCell ? NextQueryStamp() : 0;

        float rSq = radius * radius;
        bool checkStatic = StaticCount > 0;
        bool checkDynamic = DynamicCount > 0;

        for (int y = startY; y <= endY; y++)
        {
            int rowOffset = y * _gridDim;
            for (int x = startX; x <= endX; x++)
            {
                ref var cell = ref _cells[rowOffset + x];
                if (((cell.StaticMask | cell.DynamicMask) & mask) == 0) continue;

                if (checkStatic && (cell.StaticMask & mask) != 0 && max.Z >= cell.StaticMinZ && min.Z <= cell.StaticMaxZ)
                {
                    int sCount = cell.StaticCount;
                    int[]? sIds = cell.StaticIds;
                    for (int i = 0; i < sCount; i++)
                    {
                        int id = sIds![i];
                        if (multiCell)
                        {
                            if (_visitStamps[id] == stamp) continue;
                            _visitStamps[id] = stamp;
                        }
                        ref var e = ref _entries[id];
                        if (!e.Active || (e.Category & mask) == 0) continue;
                        if (e.Max.Z < min.Z || e.Min.Z > max.Z ||
                            e.Max.X < min.X || e.Min.X > max.X ||
                            e.Max.Y < min.Y || e.Min.Y > max.Y) continue;

                        float cx = Math.Clamp(center.X, e.Min.X, e.Max.X) - center.X;
                        float cy = Math.Clamp(center.Y, e.Min.Y, e.Max.Y) - center.Y;
                        float cz = Math.Clamp(center.Z, e.Min.Z, e.Max.Z) - center.Z;
                        if (cx * cx + cy * cy + cz * cz <= rSq)
                        {
                            results.Add(id);
                        }
                    }
                }

                if (checkDynamic && (cell.DynamicMask & mask) != 0)
                {
                    int dCount = cell.DynamicCount;
                    int[]? dIds = cell.DynamicIds;
                    for (int i = 0; i < dCount; i++)
                    {
                        int id = dIds![i];
                        if (multiCell)
                        {
                            if (_visitStamps[id] == stamp) continue;
                            _visitStamps[id] = stamp;
                        }
                        ref var e = ref _entries[id];
                        if (!e.Active || (e.Category & mask) == 0) continue;
                        if (e.Max.Z < min.Z || e.Min.Z > max.Z ||
                            e.Max.X < min.X || e.Min.X > max.X ||
                            e.Max.Y < min.Y || e.Min.Y > max.Y) continue;

                        float cx = Math.Clamp(center.X, e.Min.X, e.Max.X) - center.X;
                        float cy = Math.Clamp(center.Y, e.Min.Y, e.Max.Y) - center.Y;
                        float cz = Math.Clamp(center.Z, e.Min.Z, e.Max.Z) - center.Z;
                        if (cx * cx + cy * cy + cz * cz <= rSq)
                        {
                            results.Add(id);
                        }
                    }
                }
            }
        }

        return results.Count;
    }

    /// <summary>
    /// Queries candidate entries along a swept 3D line segment [<paramref name="from"/>, <paramref name="to"/>]
    /// expanded by <paramref name="radius"/>.
    /// </summary>
    public SpatialQueryResult QuerySegment(Vector3 from, Vector3 to, float radius, SpatialCategory mask, Span<int> results)
    {
        Vector3 rVec = new(radius, radius, radius);
        Vector3 min = new Vector3(
            MathF.Min(from.X, to.X),
            MathF.Min(from.Y, to.Y),
            MathF.Min(from.Z, to.Z)) - rVec;
        Vector3 max = new Vector3(
            MathF.Max(from.X, to.X),
            MathF.Max(from.Y, to.Y),
            MathF.Max(from.Z, to.Z)) + rVec;

        return QueryAabb(min, max, mask, results);
    }

    /// <summary>
    /// Queries candidate entries along a swept 3D line segment into an unbounded <see cref="List{T}"/>.
    /// </summary>
    public int QuerySegment(Vector3 from, Vector3 to, float radius, SpatialCategory mask, List<int> results)
    {
        Vector3 rVec = new(radius, radius, radius);
        Vector3 min = new Vector3(
            MathF.Min(from.X, to.X),
            MathF.Min(from.Y, to.Y),
            MathF.Min(from.Z, to.Z)) - rVec;
        Vector3 max = new Vector3(
            MathF.Max(from.X, to.X),
            MathF.Max(from.Y, to.Y),
            MathF.Max(from.Z, to.Z)) + rVec;

        return QueryAabb(min, max, mask, results);
    }

    /// <summary>
    /// Executes narrow-phase map model collision (<c>physics.cpp:738 mmcollide</c>) for <paramref name="d"/>
    /// moving along <paramref name="dir"/> using the spatial index's static <see cref="SpatialCategory.MapModel"/> layer.
    /// Zero allocations on the hot path.
    /// </summary>
    public bool CollideMapModels(OctaPhysics phys, PhysEnt d, Vector3 dir)
    {
        if (StaticCount == 0) return false;

        int maxCell = _gridDim - 1;
        int startX = Math.Clamp((int)MathF.Floor(d.O.X - d.Radius) >> _cellShift, 0, maxCell);
        int endX = Math.Clamp((int)MathF.Floor(d.O.X + d.Radius) >> _cellShift, 0, maxCell);
        int startY = Math.Clamp((int)MathF.Floor(d.O.Y - d.Radius) >> _cellShift, 0, maxCell);
        int endY = Math.Clamp((int)MathF.Floor(d.O.Y + d.Radius) >> _cellShift, 0, maxCell);

        bool multiCell = startX != endX || startY != endY;
        uint stamp = multiCell ? NextQueryStamp() : 0;

        float dTop = d.O.Z + d.AboveEye;
        float dBottom = d.O.Z - d.EyeHeight;

        for (int y = startY; y <= endY; y++)
        {
            int rowOffset = y * _gridDim;
            for (int x = startX; x <= endX; x++)
            {
                ref var cell = ref _cells[rowOffset + x];
                if ((cell.StaticMask & SpatialCategory.MapModel) == 0) continue;
                if (dTop <= cell.StaticMinZ || dBottom >= cell.StaticMaxZ) continue;

                int sCount = cell.StaticCount;
                int[]? sIds = cell.StaticIds;
                for (int i = 0; i < sCount; i++)
                {
                    int id = sIds![i];
                    if (multiCell)
                    {
                        if (_visitStamps[id] == stamp) continue;
                        _visitStamps[id] = stamp;
                    }

                    ref var e = ref _entries[id];
                    if (!e.Active || (e.Category & SpatialCategory.MapModel) == 0) continue;

                    // Strict vertical & 2D XY reject parity with physics.cpp:750
                    if (e.Min.Z >= dTop || dBottom >= e.Max.Z) continue;
                    if (Reject2D(d.O, e.Origin, d.Radius + e.RejectRadius)) continue;

                    switch (d.CollideType)
                    {
                        case Phys.CollideEllipse:
                        case Phys.CollideEllipsePrecise:
                            if (e.Shape == SpatialShapeKind.Ellipse)
                            {
                                if (phys.EllipseCollide(d, dir, e.Origin, e.CenterOffset, e.Yaw, e.Extents.X, e.Extents.Y, e.Extents.Z, e.Extents.Z))
                                    return true;
                            }
                            else
                            {
                                if (phys.EllipseBoxCollide(d, dir, e.Origin, e.CenterOffset, e.Yaw, e.Extents.X, e.Extents.Y, e.Extents.Z, e.Extents.Z))
                                    return true;
                            }
                            break;
                    }
                }
            }
        }
        return false;
    }

    // ---- Internal Helpers ---------------------------------------------------

    private static bool Reject2D(Vector3 v, Vector3 o, float r) =>
        v.X > o.X + r || v.X < o.X - r || v.Y > o.Y + r || v.Y < o.Y - r;

    private uint NextQueryStamp()
    {
        uint stamp = ++_queryStamp;
        if (stamp == 0)
        {
            Array.Clear(_visitStamps, 0, _entryCount);
            stamp = ++_queryStamp;
        }
        return stamp;
    }

    private int AllocateSlot()
    {
        if (_freeHead >= 0)
        {
            int id = _freeHead;
            _freeHead = _entries[id].UserId;
            return id;
        }

        if (_entryCount == _entries.Length)
        {
            int newCap = _entries.Length * 2;
            Array.Resize(ref _entries, newCap);
            Array.Resize(ref _visitStamps, newCap);
        }
        return _entryCount++;
    }

    private void ComputeCellSpan(Vector3 min, Vector3 max, out short cMinX, out short cMinY, out short cMaxX, out short cMaxY)
    {
        int maxCell = _gridDim - 1;
        cMinX = (short)Math.Clamp((int)MathF.Floor(min.X) >> _cellShift, 0, maxCell);
        cMaxX = (short)Math.Clamp((int)MathF.Floor(max.X) >> _cellShift, 0, maxCell);
        cMinY = (short)Math.Clamp((int)MathF.Floor(min.Y) >> _cellShift, 0, maxCell);
        cMaxY = (short)Math.Clamp((int)MathF.Floor(max.Y) >> _cellShift, 0, maxCell);
    }

    private void LinkEntryToCells(ref SpatialEntry entry)
    {
        for (int y = entry.CellMinY; y <= entry.CellMaxY; y++)
        {
            int rowOffset = y * _gridDim;
            for (int x = entry.CellMinX; x <= entry.CellMaxX; x++)
            {
                ref var cell = ref _cells[rowOffset + x];
                if (entry.IsStatic)
                {
                    cell.StaticMask |= entry.Category;
                    if (entry.Min.Z < cell.StaticMinZ) cell.StaticMinZ = entry.Min.Z;
                    if (entry.Max.Z > cell.StaticMaxZ) cell.StaticMaxZ = entry.Max.Z;
                    AppendId(ref cell.StaticIds, ref cell.StaticCount, entry.Id);
                }
                else
                {
                    cell.DynamicMask |= entry.Category;
                    AppendId(ref cell.DynamicIds, ref cell.DynamicCount, entry.Id);
                }
            }
        }
    }

    private void ExpandStaticZBounds(ref SpatialEntry entry)
    {
        for (int y = entry.CellMinY; y <= entry.CellMaxY; y++)
        {
            int rowOffset = y * _gridDim;
            for (int x = entry.CellMinX; x <= entry.CellMaxX; x++)
            {
                ref var cell = ref _cells[rowOffset + x];
                if (entry.Min.Z < cell.StaticMinZ) cell.StaticMinZ = entry.Min.Z;
                if (entry.Max.Z > cell.StaticMaxZ) cell.StaticMaxZ = entry.Max.Z;
            }
        }
    }

    private void UnlinkEntryFromCells(ref SpatialEntry entry)
    {
        for (int y = entry.CellMinY; y <= entry.CellMaxY; y++)
        {
            int rowOffset = y * _gridDim;
            for (int x = entry.CellMinX; x <= entry.CellMaxX; x++)
            {
                ref var cell = ref _cells[rowOffset + x];
                if (entry.IsStatic)
                {
                    RemoveIdPreserveOrder(cell.StaticIds, ref cell.StaticCount, entry.Id);
                    RecomputeStaticCellMetadata(ref cell);
                }
                else
                {
                    RemoveIdPreserveOrder(cell.DynamicIds, ref cell.DynamicCount, entry.Id);
                    RecomputeDynamicCellMetadata(ref cell);
                }
            }
        }
    }

    private void RecomputeStaticCellMetadata(ref GridCell cell)
    {
        SpatialCategory mask = SpatialCategory.None;
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;
        int count = cell.StaticCount;
        int[]? ids = cell.StaticIds;
        for (int i = 0; i < count; i++)
        {
            ref var e = ref _entries[ids![i]];
            mask |= e.Category;
            if (e.Min.Z < minZ) minZ = e.Min.Z;
            if (e.Max.Z > maxZ) maxZ = e.Max.Z;
        }
        cell.StaticMask = mask;
        cell.StaticMinZ = minZ;
        cell.StaticMaxZ = maxZ;
    }

    private void RecomputeDynamicCellMetadata(ref GridCell cell)
    {
        SpatialCategory mask = SpatialCategory.None;
        int count = cell.DynamicCount;
        int[]? ids = cell.DynamicIds;
        for (int i = 0; i < count; i++)
        {
            mask |= _entries[ids![i]].Category;
        }
        cell.DynamicMask = mask;
    }

    private static void AppendId([NotNull] ref int[]? array, ref int count, int id)
    {
        if (array == null)
        {
            array = new int[4];
        }
        else if (count == array.Length)
        {
            Array.Resize(ref array, array.Length * 2);
        }
        array[count++] = id;
    }

    private static void RemoveIdPreserveOrder(int[]? array, ref int count, int id)
    {
        if (array == null || count == 0) return;
        for (int i = 0; i < count; i++)
        {
            if (array[i] == id)
            {
                for (int j = i; j < count - 1; j++)
                {
                    array[j] = array[j + 1];
                }
                count--;
                return;
            }
        }
    }
}
