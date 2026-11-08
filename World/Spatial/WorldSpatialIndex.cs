using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Spatial;

/// <summary>
/// Unified world spatial index supporting:
/// <list type="bullet">
///   <item><description><b>Static init:</b> Map models, CTF bases, item pickup zones, teleporters, jump pads.</description></item>
///   <item><description><b>Dynamic entities:</b> Players, monsters, grenades/bouncers, rockets, dropped/movable flags.</description></item>
///   <item><description><b>Once-per-frame broad-pass updates (<see cref="StepBroadphase"/>):</b> Batch-synchronizes tracked dynamic
///   entities using O(1) incremental cell-bound checks and evaluates zone overlaps.</description></item>
///   <item><description><b>Event-based zone logic:</b> Emits <see cref="ZoneEntered"/>, <see cref="ZoneStayed"/>, and
///   <see cref="ZoneExited"/> transitions (both globally and per <see cref="SpatialZone"/>) when dynamic entities cross zone boundaries.</description></item>
///   <item><description><b>Polling-based queries:</b> Zero-allocation <see cref="QueryZoneOccupants"/>, <see cref="QueryPointZones"/>,
///   <see cref="QueryEntityZones"/>, <see cref="IsEntityInZone"/>, <see cref="QueryAabb(Vector3, Vector3, SpatialCategory, Span{int})"/>,
///   <see cref="QueryRadius"/>, and <see cref="QuerySegment"/>.</description></item>
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

    private readonly record struct OverlapPair(int ZoneId, int EntityId);

    private int _worldSize;
    private readonly int _cellShift;
    private int _gridDim;
    private GridCell[] _cells;

    private SpatialEntry[] _entries;
    private uint[] _visitStamps;
    private int _entryCount;
    private int _freeHead = -1;
    private uint _queryStamp;

    // Dense tracking lists for broad-pass updates & zone evaluation
    private int[] _trackedDynamicIds = new int[32];
    private int _trackedDynamicCount;
    private int[] _registeredZoneIds = new int[32];
    private int _registeredZoneCount;

    // Double-buffered active overlap pairs for Entered / Stayed / Exited transitions
    private OverlapPair[] _prevOverlaps = new OverlapPair[64];
    private int _prevOverlapCount;
    private OverlapPair[] _currOverlaps = new OverlapPair[64];
    private int _currOverlapCount;

    /// <summary>Fired when a dynamic entity enters an active <see cref="SpatialZone"/> during <see cref="StepBroadphase"/>.</summary>
    public event Action<ZoneOverlapEvent>? ZoneEntered;

    /// <summary>Fired each <see cref="StepBroadphase"/> while a dynamic entity remains inside an active <see cref="SpatialZone"/>.</summary>
    public event Action<ZoneOverlapEvent>? ZoneStayed;

    /// <summary>Fired when a dynamic entity leaves an active <see cref="SpatialZone"/> (or when either becomes inactive/removed).</summary>
    public event Action<ZoneOverlapEvent>? ZoneExited;

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

    /// <summary>Clears all static and dynamic entries and resets overlap tracking state.</summary>
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
        Array.Clear(_entries, 0, _entryCount);
        Array.Clear(_visitStamps, 0, _entryCount);
        _entryCount = 0;
        _freeHead = -1;
        _queryStamp = 0;
        _trackedDynamicCount = 0;
        _registeredZoneCount = 0;
        _prevOverlapCount = 0;
        _currOverlapCount = 0;
        StaticCount = 0;
        DynamicCount = 0;
        InPlaceUpdates = 0;
        BoundaryCrossings = 0;
    }

    /// <summary>Returns a read-only reference to the entry for <paramref name="handle"/>.</summary>
    public ref readonly SpatialEntry GetEntry(SpatialHandle handle) => ref _entries[handle.Id];

    /// <summary>Returns a read-only reference to the entry at raw <paramref name="id"/>.</summary>
    public ref readonly SpatialEntry GetEntryById(int id) => ref _entries[id];

    /// <summary>Checks whether <paramref name="handle"/> refers to a currently allocated entry.</summary>
    public bool IsValid(SpatialHandle handle) =>
        handle.Id >= 0 && handle.Id < _entryCount && _entries[handle.Id].Allocated;

    // ---- Registration & Lifecycle -------------------------------------------

    /// <summary>
    /// Registers a static entry in the spatial index (e.g. map model, base zone, item spawn, trigger).
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
    /// Registers a dynamic entry in the spatial index (e.g. player, monster, bouncer, rocket, dropped flag).
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
    /// Registers a <see cref="SpatialZone"/> in the spatial index (static by default, or dynamic for movable zones like dropped flags).
    /// Registered zones automatically participate in <see cref="StepBroadphase"/> overlap detection and polling queries.
    /// </summary>
    public SpatialHandle RegisterZone(SpatialZone zone, bool isStatic = true)
    {
        zone.ComputeBounds(out Vector3 min, out Vector3 max, out float rejectRadius);
        var handle = RegisterInternal(
            isStatic,
            zone.Category,
            zone.Shape,
            zone.Origin,
            min,
            max,
            zone.CenterOffset,
            zone.Extents,
            zone.Yaw,
            rejectRadius,
            trackedPhysEnt: null,
            payload: zone,
            userId: zone.ZoneId,
            team: zone.Team,
            active: zone.Active);
        zone.Handle = handle;
        AppendId(ref _registeredZoneIds, ref _registeredZoneCount, handle.Id);
        return handle;
    }

    /// <summary>
    /// Registers a <see cref="PhysEnt"/> (player, monster, or bouncer) as a tracked dynamic entry.
    /// When <see cref="StepBroadphase"/> runs, its bounds and alive state are automatically synchronized from <paramref name="ent"/>.
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
        entry = new SpatialEntry
        {
            Id = id,
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

        return new SpatialHandle(id);
    }

    // ---- Dynamic Updates & Broad-Pass Execution -----------------------------

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
        if (entry.Payload is SpatialZone zone)
        {
            zone.Origin = origin;
            if (yaw.HasValue) zone.Yaw = yaw.Value;
        }

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
    /// Translates an entry (or movable <see cref="SpatialZone"/>, such as a dropped flag) to <paramref name="newOrigin"/>,
    /// shifting its AABB by <c>newOrigin - oldOrigin</c>.
    /// </summary>
    public void UpdatePosition(SpatialHandle handle, Vector3 newOrigin, float? yaw = null)
    {
        if (!IsValid(handle)) return;
        ref var entry = ref _entries[handle.Id];
        if (entry.Payload is SpatialZone zone)
        {
            zone.Origin = newOrigin;
            if (yaw.HasValue) zone.Yaw = yaw.Value;
            zone.ComputeBounds(out Vector3 zMin, out Vector3 zMax, out float reject);
            entry.RejectRadius = reject;
            UpdateBounds(handle, newOrigin, zMin, zMax, yaw);
            return;
        }
        Vector3 delta = newOrigin - entry.Origin;
        UpdateBounds(handle, newOrigin, entry.Min + delta, entry.Max + delta, yaw);
    }

    /// <summary>
    /// Synchronizes a registered <see cref="PhysEnt"/> handle with its current <c>O</c>, <c>Radius</c>, <c>EyeHeight</c>, <c>AboveEye</c>, <c>Yaw</c>, and <c>State</c>.
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
        if (entry.Payload is SpatialZone zone)
        {
            if (userId.HasValue) zone.ZoneId = userId.Value;
            if (team.HasValue) zone.Team = team.Value;
        }
    }

    /// <summary>
    /// Enables or disables an entry in O(1) without unlinking it from its grid cells.
    /// Useful for item despawn/respawn timers, carried flags, or dead players.
    /// </summary>
    public void SetActive(SpatialHandle handle, bool active)
    {
        if (!IsValid(handle)) return;
        ref var entry = ref _entries[handle.Id];
        entry.Active = active;
        if (entry.Payload is SpatialZone zone)
        {
            zone.Active = active;
        }
    }

    /// <summary>
    /// Executes a once-per-frame / once-per-tick broad-pass update:
    /// <list type="number">
    ///   <item><description>Synchronizes all tracked dynamic <see cref="PhysEnt"/> entries (using the O(1) in-place fast path when cell bounds are unchanged).</description></item>
    ///   <item><description>Evaluates all active <see cref="SpatialZone"/> overlaps against dynamic entities and dispatches
    ///   <see cref="ZoneEntered"/>, <see cref="ZoneStayed"/>, and <see cref="ZoneExited"/> events.</description></item>
    /// </list>
    /// Zero heap allocations.
    /// </summary>
    public void StepBroadphase(bool evaluateZones = true)
    {
        // 1. Sync all tracked dynamic PhysEnts
        for (int i = 0; i < _trackedDynamicCount; i++)
        {
            int id = _trackedDynamicIds[i];
            if (_entries[id].Allocated && _entries[id].TrackedPhysEnt != null)
            {
                UpdatePhysEnt(new SpatialHandle(id), _entries[id].TrackedPhysEnt);
            }
        }

        if (!evaluateZones || (_registeredZoneCount == 0 && _prevOverlapCount == 0))
            return;

        // 2. Collect current zone overlaps
        _currOverlapCount = 0;
        for (int z = 0; z < _registeredZoneCount; z++)
        {
            int zoneEntryId = _registeredZoneIds[z];
            ref var zEntry = ref _entries[zoneEntryId];
            if (!zEntry.Allocated || !zEntry.Active || zEntry.Payload is not SpatialZone zone || !zone.Active)
                continue;

            SpatialCategory targetMask = zone.TargetMask;
            if (targetMask == SpatialCategory.None || DynamicCount == 0) continue;

            bool multiCell = zEntry.CellMinX != zEntry.CellMaxX || zEntry.CellMinY != zEntry.CellMaxY;
            uint stamp = multiCell ? NextQueryStamp() : 0;

            for (int y = zEntry.CellMinY; y <= zEntry.CellMaxY; y++)
            {
                int rowOffset = y * _gridDim;
                for (int x = zEntry.CellMinX; x <= zEntry.CellMaxX; x++)
                {
                    ref var cell = ref _cells[rowOffset + x];
                    if ((cell.DynamicMask & targetMask) == 0) continue;

                    int dCount = cell.DynamicCount;
                    int[]? dIds = cell.DynamicIds;
                    for (int i = 0; i < dCount; i++)
                    {
                        int entId = dIds![i];
                        if (entId == zoneEntryId) continue;
                        if (multiCell)
                        {
                            if (_visitStamps[entId] == stamp) continue;
                            _visitStamps[entId] = stamp;
                        }

                        ref var dEntry = ref _entries[entId];
                        if (!dEntry.Active || (dEntry.Category & targetMask) == 0) continue;

                        if (zone.OverlapsEntry(in dEntry))
                        {
                            AppendOverlap(ref _currOverlaps, ref _currOverlapCount, new OverlapPair(zoneEntryId, entId));
                        }
                    }
                }
            }
        }

        // 3. Emit Exited events for previous overlaps that are no longer present
        for (int p = 0; p < _prevOverlapCount; p++)
        {
            OverlapPair prev = _prevOverlaps[p];
            if (!ContainsOverlap(_currOverlaps, _currOverlapCount, prev))
            {
                EmitZoneEvent(ZoneOverlapKind.Exited, prev.ZoneId, prev.EntityId);
            }
        }

        // 4. Emit Entered and Stayed events for current overlaps
        for (int c = 0; c < _currOverlapCount; c++)
        {
            OverlapPair curr = _currOverlaps[c];
            bool wasPresent = ContainsOverlap(_prevOverlaps, _prevOverlapCount, curr);
            EmitZoneEvent(wasPresent ? ZoneOverlapKind.Stayed : ZoneOverlapKind.Entered, curr.ZoneId, curr.EntityId);
        }

        // 5. Swap buffers
        (_prevOverlaps, _currOverlaps) = (_currOverlaps, _prevOverlaps);
        _prevOverlapCount = _currOverlapCount;
    }

    private void EmitZoneEvent(ZoneOverlapKind kind, int zoneEntryId, int entityEntryId)
    {
        if (zoneEntryId < 0 || zoneEntryId >= _entryCount || entityEntryId < 0 || entityEntryId >= _entryCount)
            return;

        ref var zEntry = ref _entries[zoneEntryId];
        if (zEntry.Payload is not SpatialZone zone) return;

        ref var dEntry = ref _entries[entityEntryId];
        var evt = new ZoneOverlapEvent
        {
            Kind = kind,
            Zone = zone,
            ZoneHandle = new SpatialHandle(zoneEntryId),
            EntityHandle = new SpatialHandle(entityEntryId),
            EntityCategory = dEntry.Category,
            EntityUserId = dEntry.UserId,
            EntityTeam = dEntry.Team,
            EntityOrigin = dEntry.Origin,
            EntityFeetPos = dEntry.FeetPos,
            EntityPayload = dEntry.Payload,
        };

        zone.RaiseOverlapEvent(in evt);
        switch (kind)
        {
            case ZoneOverlapKind.Entered:
                ZoneEntered?.Invoke(evt);
                break;
            case ZoneOverlapKind.Stayed:
                ZoneStayed?.Invoke(evt);
                break;
            case ZoneOverlapKind.Exited:
                ZoneExited?.Invoke(evt);
                break;
        }
    }

    /// <summary>
    /// Removes an entry from the spatial index and recycles its handle slot.
    /// If the removed entry had active zone overlaps, emits <see cref="ZoneExited"/> transitions cleanly.
    /// </summary>
    public bool Remove(SpatialHandle handle)
    {
        if (!IsValid(handle)) return false;
        int id = handle.Id;
        ref var entry = ref _entries[id];

        // Emit Exited for any active overlaps involving this entry before clearing it
        for (int i = _prevOverlapCount - 1; i >= 0; i--)
        {
            OverlapPair pair = _prevOverlaps[i];
            if (pair.ZoneId == id || pair.EntityId == id)
            {
                EmitZoneEvent(ZoneOverlapKind.Exited, pair.ZoneId, pair.EntityId);
                for (int j = i; j < _prevOverlapCount - 1; j++)
                    _prevOverlaps[j] = _prevOverlaps[j + 1];
                _prevOverlapCount--;
            }
        }

        UnlinkEntryFromCells(ref entry);

        if (entry.TrackedPhysEnt != null)
        {
            RemoveIdPreserveOrder(_trackedDynamicIds, ref _trackedDynamicCount, id);
        }
        if (entry.Payload is SpatialZone)
        {
            RemoveIdPreserveOrder(_registeredZoneIds, ref _registeredZoneCount, id);
        }

        if (entry.IsStatic) StaticCount--;
        else DynamicCount--;

        if (entry.Payload is MapModelCollider mm) mm.SpatialHandle = SpatialHandle.Invalid;
        else if (entry.Payload is SpatialZone z) z.Handle = SpatialHandle.Invalid;

        entry.Allocated = false;
        entry.Active = false;
        entry.TrackedPhysEnt = null;
        entry.Payload = null;
        entry.UserId = _freeHead;
        _freeHead = id;
        return true;
    }

    // ---- Polling Queries (Zones, AABB, Radius, Segment) ---------------------

    /// <summary>
    /// Polls all active dynamic entities matching <paramref name="entityMask"/> that currently overlap
    /// the specified <paramref name="zoneHandle"/>. Writes matching entity entry IDs into <paramref name="results"/>.
    /// Zero heap allocations.
    /// </summary>
    public int QueryZoneOccupants(SpatialHandle zoneHandle, SpatialCategory entityMask, Span<int> results)
    {
        if (results.IsEmpty || !IsValid(zoneHandle) || entityMask == SpatialCategory.None || DynamicCount == 0)
            return 0;

        ref var zEntry = ref _entries[zoneHandle.Id];
        if (!zEntry.Active || zEntry.Payload is not SpatialZone zone || !zone.Active)
            return 0;

        bool multiCell = zEntry.CellMinX != zEntry.CellMaxX || zEntry.CellMinY != zEntry.CellMaxY;
        uint stamp = multiCell ? NextQueryStamp() : 0;
        int found = 0;

        for (int y = zEntry.CellMinY; y <= zEntry.CellMaxY; y++)
        {
            int rowOffset = y * _gridDim;
            for (int x = zEntry.CellMinX; x <= zEntry.CellMaxX; x++)
            {
                ref var cell = ref _cells[rowOffset + x];
                if ((cell.DynamicMask & entityMask) == 0) continue;

                int dCount = cell.DynamicCount;
                int[]? dIds = cell.DynamicIds;
                for (int i = 0; i < dCount; i++)
                {
                    int entId = dIds![i];
                    if (entId == zoneHandle.Id) continue;
                    if (multiCell)
                    {
                        if (_visitStamps[entId] == stamp) continue;
                        _visitStamps[entId] = stamp;
                    }

                    ref var dEntry = ref _entries[entId];
                    if (!dEntry.Active || (dEntry.Category & entityMask) == 0) continue;

                    if (zone.OverlapsEntry(in dEntry))
                    {
                        results[found++] = entId;
                        if (found >= results.Length) return found;
                    }
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Polls whether a specific registered entity (<paramref name="entityHandle"/>) currently overlaps <paramref name="zoneHandle"/>.
    /// </summary>
    public bool IsEntityInZone(SpatialHandle zoneHandle, SpatialHandle entityHandle)
    {
        if (!IsValid(zoneHandle) || !IsValid(entityHandle)) return false;
        ref var zEntry = ref _entries[zoneHandle.Id];
        ref var dEntry = ref _entries[entityHandle.Id];
        if (!zEntry.Active || !dEntry.Active || zEntry.Payload is not SpatialZone zone || !zone.Active)
            return false;
        return zone.OverlapsEntry(in dEntry);
    }

    /// <summary>
    /// Polls all active <see cref="SpatialZone"/> entries matching <paramref name="zoneMask"/> that currently
    /// overlap the specified dynamic entity (<paramref name="entityHandle"/>).
    /// </summary>
    public int QueryEntityZones(SpatialHandle entityHandle, SpatialCategory zoneMask, Span<int> results)
    {
        if (results.IsEmpty || !IsValid(entityHandle) || zoneMask == SpatialCategory.None)
            return 0;

        ref var dEntry = ref _entries[entityHandle.Id];
        if (!dEntry.Active) return 0;

        bool multiCell = dEntry.CellMinX != dEntry.CellMaxX || dEntry.CellMinY != dEntry.CellMaxY;
        uint stamp = multiCell ? NextQueryStamp() : 0;
        int found = 0;

        for (int y = dEntry.CellMinY; y <= dEntry.CellMaxY; y++)
        {
            int rowOffset = y * _gridDim;
            for (int x = dEntry.CellMinX; x <= dEntry.CellMaxX; x++)
            {
                ref var cell = ref _cells[rowOffset + x];
                if (((cell.StaticMask | cell.DynamicMask) & zoneMask) == 0) continue;

                if ((cell.StaticMask & zoneMask) != 0)
                {
                    int sCount = cell.StaticCount;
                    int[]? sIds = cell.StaticIds;
                    for (int i = 0; i < sCount; i++)
                    {
                        int zId = sIds![i];
                        if (multiCell)
                        {
                            if (_visitStamps[zId] == stamp) continue;
                            _visitStamps[zId] = stamp;
                        }
                        ref var zEntry = ref _entries[zId];
                        if (!zEntry.Active || (zEntry.Category & zoneMask) == 0) continue;
                        if (zEntry.Payload is SpatialZone zone && zone.OverlapsEntry(in dEntry))
                        {
                            results[found++] = zId;
                            if (found >= results.Length) return found;
                        }
                    }
                }

                if ((cell.DynamicMask & zoneMask) != 0)
                {
                    int dCount = cell.DynamicCount;
                    int[]? dIds = cell.DynamicIds;
                    for (int i = 0; i < dCount; i++)
                    {
                        int zId = dIds![i];
                        if (zId == entityHandle.Id) continue;
                        if (multiCell)
                        {
                            if (_visitStamps[zId] == stamp) continue;
                            _visitStamps[zId] = stamp;
                        }
                        ref var zEntry = ref _entries[zId];
                        if (!zEntry.Active || (zEntry.Category & zoneMask) == 0) continue;
                        if (zEntry.Payload is SpatialZone zone && zone.OverlapsEntry(in dEntry))
                        {
                            results[found++] = zId;
                            if (found >= results.Length) return found;
                        }
                    }
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Broad-phase 3D AABB overlap query. Writes matching entry IDs into <paramref name="results"/>
    /// and returns the number of matches written (up to <c>results.Length</c>).
    /// Zero heap allocations.
    /// </summary>
    public int QueryAabb(Vector3 min, Vector3 max, SpatialCategory mask, Span<int> results)
    {
        if (results.IsEmpty || mask == SpatialCategory.None || Count == 0) return 0;

        ComputeCellSpan(min, max, out short startX, out short startY, out short endX, out short endY);
        bool multiCell = startX != endX || startY != endY;
        uint stamp = multiCell ? NextQueryStamp() : 0;

        bool checkStatic = StaticCount > 0;
        bool checkDynamic = DynamicCount > 0;

        int found = 0;
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

                        results[found++] = id;
                        if (found >= results.Length) return found;
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

                        results[found++] = id;
                        if (found >= results.Length) return found;
                    }
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Convenience overload of <see cref="QueryAabb(Vector3, Vector3, SpatialCategory, Span{int})"/> appending to a <see cref="List{T}"/>.
    /// </summary>
    public void QueryAabb(Vector3 min, Vector3 max, SpatialCategory mask, List<int> results)
    {
        results.Clear();
        Span<int> buffer = stackalloc int[256];
        int count = QueryAabb(min, max, mask, buffer);
        for (int i = 0; i < count; i++) results.Add(buffer[i]);
    }

    /// <summary>
    /// Queries all active entries within <paramref name="radius"/> of <paramref name="center"/> (3D sphere overlap against entry AABB).
    /// Useful for explosion blast radius queries (<c>WeaponExplosion.ResolveExplosion</c>) and <c>OverlapsDynent</c>.
    /// </summary>
    public int QueryRadius(Vector3 center, float radius, SpatialCategory mask, Span<int> results)
    {
        if (results.IsEmpty || radius < 0f || mask == SpatialCategory.None || Count == 0) return 0;

        Vector3 rVec = new(radius, radius, radius);
        Vector3 min = center - rVec;
        Vector3 max = center + rVec;

        Span<int> candidates = stackalloc int[256];
        int candCount = QueryAabb(min, max, mask, candidates);
        float rSq = radius * radius;
        int found = 0;

        for (int i = 0; i < candCount; i++)
        {
            int id = candidates[i];
            ref var e = ref _entries[id];

            // Closest point on entry AABB to sphere center
            float cx = Math.Clamp(center.X, e.Min.X, e.Max.X) - center.X;
            float cy = Math.Clamp(center.Y, e.Min.Y, e.Max.Y) - center.Y;
            float cz = Math.Clamp(center.Z, e.Min.Z, e.Max.Z) - center.Z;
            if (cx * cx + cy * cy + cz * cz <= rSq)
            {
                results[found++] = id;
                if (found >= results.Length) return found;
            }
        }
        return found;
    }

    /// <summary>
    /// Queries all active <see cref="SpatialZone"/> entries matching <paramref name="zoneMask"/> whose narrow-phase
    /// volume contains <paramref name="point"/> (e.g. player feet position for CTF flags, bases, items, teleporters, jump pads).
    /// </summary>
    public int QueryPointZones(Vector3 point, SpatialCategory zoneMask, Span<int> results)
    {
        if (results.IsEmpty || zoneMask == SpatialCategory.None || Count == 0) return 0;

        int cellX = Math.Clamp((int)MathF.Floor(point.X) >> _cellShift, 0, _gridDim - 1);
        int cellY = Math.Clamp((int)MathF.Floor(point.Y) >> _cellShift, 0, _gridDim - 1);
        ref var cell = ref _cells[cellY * _gridDim + cellX];

        if (((cell.StaticMask | cell.DynamicMask) & zoneMask) == 0) return 0;

        int found = 0;

        if ((cell.StaticMask & zoneMask) != 0 && point.Z >= cell.StaticMinZ && point.Z <= cell.StaticMaxZ)
        {
            int sCount = cell.StaticCount;
            int[]? sIds = cell.StaticIds;
            for (int i = 0; i < sCount; i++)
            {
                int id = sIds![i];
                ref var e = ref _entries[id];
                if (!e.Active || (e.Category & zoneMask) == 0) continue;
                if (point.Z < e.Min.Z || point.Z > e.Max.Z ||
                    point.X < e.Min.X || point.X > e.Max.X ||
                    point.Y < e.Min.Y || point.Y > e.Max.Y) continue;

                if (e.Payload is SpatialZone zone ? zone.ContainsPoint(point) : ContainsPointInline(ref e, point))
                {
                    results[found++] = id;
                    if (found >= results.Length) return found;
                }
            }
        }

        if ((cell.DynamicMask & zoneMask) != 0)
        {
            int dCount = cell.DynamicCount;
            int[]? dIds = cell.DynamicIds;
            for (int i = 0; i < dCount; i++)
            {
                int id = dIds![i];
                ref var e = ref _entries[id];
                if (!e.Active || (e.Category & zoneMask) == 0) continue;
                if (point.Z < e.Min.Z || point.Z > e.Max.Z ||
                    point.X < e.Min.X || point.X > e.Max.X ||
                    point.Y < e.Min.Y || point.Y > e.Max.Y) continue;

                if (e.Payload is SpatialZone zone ? zone.ContainsPoint(point) : ContainsPointInline(ref e, point))
                {
                    results[found++] = id;
                    if (found >= results.Length) return found;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Queries candidate entries along a swept 3D line segment [<paramref name="from"/>, <paramref name="to"/>]
    /// expanded by <paramref name="radius"/>. Used by flying rockets (<c>RocketProjectile.Advance</c>) and hitscan rays.
    /// </summary>
    public int QuerySegment(Vector3 from, Vector3 to, float radius, SpatialCategory mask, Span<int> results)
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

    private static bool ContainsPointInline(ref SpatialEntry e, Vector3 point)
    {
        Vector3 center = e.Origin + e.CenterOffset;
        return e.Shape switch
        {
            SpatialShapeKind.Sphere => point.DistanceSquaredTo(center) < e.Extents.X * e.Extents.X,
            SpatialShapeKind.Cylinder =>
                (center.X - point.X) * (center.X - point.X) + (center.Y - point.Y) * (center.Y - point.Y) <= e.Extents.X * e.Extents.X &&
                MathF.Abs(center.Z - point.Z) <= e.Extents.Z,
            _ => true,
        };
    }

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

    private static void AppendId([System.Diagnostics.CodeAnalysis.NotNull] ref int[]? array, ref int count, int id)
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

    private static void AppendOverlap(ref OverlapPair[] array, ref int count, OverlapPair pair)
    {
        if (count == array.Length)
        {
            Array.Resize(ref array, array.Length * 2);
        }
        array[count++] = pair;
    }

    private static bool ContainsOverlap(OverlapPair[] array, int count, OverlapPair target)
    {
        for (int i = 0; i < count; i++)
        {
            if (array[i] == target) return true;
        }
        return false;
    }
}
