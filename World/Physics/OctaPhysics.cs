using Godot;

namespace SauerbratenRevival.World.Physics;

// Octree collision + the move() state machine, ported from src/engine/physics.cpp
// (SVN r6889). Names mirror the engine tokens. This is the layer-2 collision the
// A3 replay differential needs (docs/movement-verification.md).
//
// Scope of this file (A3.1): the player collision type COLLIDE_ELLIPSE against
// solid cubes (fuzzycollidesolid, an AABB test), the full move()/trystepup()/
// trystepdown()/findfloor() state machine, and the velocity model. Deformed cubes
// (fuzzycollideplanes / genclipplanes) are NOT ported yet — see known-bugs.md.

/// <summary>src/engine/physics.cpp constants + ents.h defaults.</summary>
public static class Phys
{
    public const float Gravity = 200.0f;     // physics.cpp:478
    public const float JumpVel = 125.0f;     // :477
    public const float StairHeight = 4.1f;   // :473
    public const float FloorZ = 0.867f;      // :474
    public const float SlopeZ = 0.5f;        // :475
    public const float WallZ = 0.2f;         // :476
    public const int PhysFrameTime = 5;      // :1582
    public const int MoveRes = 10;           // fps.cpp:264

    public const int PhysFloat = 0, PhysFall = 1, PhysSlide = 2, PhysSlope = 3,
                     PhysFloor = 4, PhysStepUp = 5, PhysStepDown = 6, PhysBounce = 7;
    public const int EntPlayer = 0, EntAi = 1, EntInanimate = 2, EntCamera = 3, EntBounce = 4;
    public const int CollideEllipse = 0, CollideObb = 1, CollideEllipsePrecise = 2;

    // iengine.h material flags (MATF_*).
    public const ushort MatAir = 0;
    public const ushort MatWater = 1 << 2, MatLava = 2 << 2, MatGlass = 3 << 2;
    public const ushort MatNoClip = 1 << 5, MatClip = 2 << 5, MatGameClip = 3 << 5;
    public const ushort MatFIndex = 3, MatFVolume = 7 << 2, MatFClip = 7 << 5;
    public const ushort MatAlpha = 4 << 8;
    public const ushort MatDeath = 1 << 8;

    public static bool IsLiquid(ushort m) => (m & MatFVolume) == MatWater || (m & MatFVolume) == MatLava;
    public static bool IsClipped(ushort m) => (m & MatFVolume) == MatGlass;
}

/// <summary>src/shared/ents.h <c>physent</c> — the fields movement/collision touch.</summary>
public sealed class PhysEnt
{
    public Vector3 O;                            // origin (eye height)
    public Vector3 Vel;                          // velocity accumulator
    public Vector3 Falling;                      // gravity accumulator
    public Vector3 Floor;                        // floor normal
    public float Radius = 4.1f;                  // ents.h:91
    public float EyeHeight = 14.0f;
    public float AboveEye = 1.0f;

    /// <summary>ents.h:114 <c>feetpos(offset = 0)</c>: origin offset by <c>offset - eyeheight</c> along Z.</summary>
    public Vector3 FeetPos(float offset = 0.0f) => new(O.X, O.Y, O.Z + offset - EyeHeight);
    public float XRadius = 4.1f;                 // ents.h:92 (ellipse collision pair)
    public float YRadius = 4.1f;
    public float Zmargin = 0.0f;
    public int PhysState = Phys.PhysFall;
    public int Type = Phys.EntPlayer;
    public int CollideType = Phys.CollideEllipse;
    public int Move, Strafe;
    public bool Blocked;
    public int TimeInAir;
    /// <summary>
    /// True when the entity is in <see cref="CsDead"/>. Setting true transitions
    /// <see cref="State"/> to <see cref="CsDead"/> (hit <c>MAT_DEATH</c> / fell out of the world);
    /// setting false while dead restores <see cref="CsAlive"/>.
    /// </summary>
    public bool Dead
    {
        get => State == CsDead;
        set
        {
            if (value) State = CsDead;
            else if (State == CsDead) State = CsAlive;
        }
    }
    public float Yaw;
    public float Pitch;          // ents.h:71 — used by vecfromyawpitch when floating/water/camera
    public float Roll;           // ents.h:71 — strafe roll (camera only)
    public bool Jumping;
    public float MaxSpeed = 100.0f;
    /// <summary>physics.cpp:1587 <c>floatspeed</c> — spectator/edit fly speed (% of maxspeed).</summary>
    public float FloatSpeed = 100.0f;
    /// <summary>True for the local player (<c>pl == player</c>); floatspeed only applies to it.</summary>
    public bool IsLocalPlayer = true;

    /// <summary>ents.h:79 <c>inwater</c> — the material volume the entity is currently in
    /// (0 = MAT_AIR). The velocity damping on entering water keys off this.</summary>
    public int InWater;

    // ents.h:77 interpolation state used by bounce()/moveplayer() (newpos/deltapos).
    /// <summary>nets.h: the authoritative position at the start of the frame's physics steps.</summary>
    public Vector3 NewPos;
    /// <summary>nets.h: the interpolated offset between <see cref="O"/> and <see cref="NewPos"/>.</summary>
    public Vector3 DeltaPos;

    // ents.h:60 CS_* player states (only the two physics cares about are named here).
    public const int CsAlive = 0, CsDead = 1, CsSpawning = 2, CsLagged = 3, CsEditing = 4, CsSpectator = 5;
    /// <summary>ents.h:82 <c>state</c>; <see cref="CsEditing"/>/<see cref="CsSpectator"/> imply floating.</summary>
    public int State = CsAlive;
}

/// <summary>The move()/collide() state machine. One per world (collision is stateless).</summary>
public sealed class OctaPhysics
{
    public Octa Octa => _octa;
    private readonly Octa _octa;
    public OctaPhysics(Octa octa)
    {
        _octa = octa;
        for (int i = 0; i < DynEntCacheSize; i++) _dynEntCache[i] = new DynEntCacheEntry();
    }

    // collision outputs (engine globals collidewall / collideinside)
    public Vector3 CollideWall;
    public int CollideInside;

    /// <summary>engine <c>collideplayer</c>: the dynent the last collide() hit, or null.
    /// Only set when a collide() is called with <c>playercol = true</c>. No dynent
    /// cache is ported yet, so <see cref="CollidePlayer"/> stays null (see <see cref="PlCollide"/>).</summary>
    public PhysEnt? CollidePlayer;

    // ---- octree collision -------------------------------------------------

    /// <summary>physics.cpp:1048 <c>collide</c>.</summary>
    public bool Collide(PhysEnt d, Vector3 dir, float cutoff = 0.0f)
        => Collide(d, dir, cutoff, false, false);

    /// <summary>physics.cpp:1048 <c>collide</c> (full form: <c>playercol</c> +
    /// <c>insideplayercol</c>). Player collision is a seam — see <see cref="PlCollide"/>.</summary>
    public bool Collide(PhysEnt d, Vector3 dir, float cutoff, bool playercol, bool insideplayercol)
    {
        CollideInside = 0;
        CollidePlayer = null;
        CollideWall = Vector3.Zero;
        var bo = new Vector3I((int)(d.O.X - d.Radius), (int)(d.O.Y - d.Radius), (int)(d.O.Z - d.EyeHeight));
        var bs = new Vector3I((int)(d.O.X + d.Radius), (int)(d.O.Y + d.Radius), (int)(d.O.Z + d.AboveEye));
        bs += Vector3I.One; // guard space (physics.cpp:1055)
        bool hit = OctaCollide(d, dir, cutoff, bo, bs, _octa.WorldRoot, Vector3I.Zero, _octa.WorldSize >> 1);
        return hit || (playercol && PlCollide(d, dir, insideplayercol));
    }

    /// <summary>
    /// physics.cpp:676 <c>plcollide</c> — collide with a player/monster/movable.
    /// Iterates the dynent cache (see <see cref="UpdateDynentCache"/>) and tests each
    /// candidate with <see cref="EllipseCollide"/> for the default COLLIDE_ELLIPSE
    /// pair. <c>game::dynentcollide</c> is a seam (null in production).
    /// </summary>
    private bool PlCollide(PhysEnt d, Vector3 dir, bool insideplayercol)
    {
        if (d.Type == Phys.EntCamera || d.State != PhysEnt.CsAlive) return false;
        int lastinside = CollideInside;
        PhysEnt? insideplayer = null;
        LoopDynentCache(d.O, d.Radius, (x, y) =>
        {
            foreach (var o in CheckDynentCache(x, y))
            {
                if (ReferenceEquals(o, d) || Reject(d.O, o.O, d.Radius + o.Radius)) continue;
                if (PlCollide1(d, dir, o))
                {
                    CollidePlayer = o;
                    DynEntCollide?.Invoke(d, o, CollideWall);
                    return true;
                }
                if (CollideInside > lastinside)
                {
                    lastinside = CollideInside;
                    insideplayer = o;
                }
            }
            return false;
        });
        if (CollidePlayer != null) return true;
        if (insideplayer != null && insideplayercol)
        {
            CollidePlayer = insideplayer;
            DynEntCollide?.Invoke(d, insideplayer, Vector3.Zero);
            return true;
        }
        return false;
    }

    /// <summary>physics.cpp:661 dispatch for one pair; only the COLLIDE_ELLIPSE pair is ported.</summary>
    private bool PlCollide1(PhysEnt d, Vector3 dir, PhysEnt o)
    {
        switch (d.CollideType)
        {
            case Phys.CollideEllipse:
            case Phys.CollideEllipsePrecise:
                // Ellipse-vs-OBB and ellipse-vs-ellipse both fall back to the cylinder test
                // in the engine; only COLLIDE_OBB needs the mpr box path (not ported).
                if (o.CollideType == Phys.CollideObb) return false;
                return EllipseCollide(d, dir, o.O, Vector3.Zero, o.Yaw, o.XRadius, o.YRadius, o.AboveEye, o.EyeHeight);
            default:
                return false;
        }
    }

    /// <summary>physics.cpp:535 <c>ellipsecollide</c> (COLLIDE_ELLIPSE player pair, both capsules).</summary>
    public bool EllipseCollide(PhysEnt d, Vector3 dir, Vector3 o, Vector3 center, float yaw, float xr, float yr, float hi, float lo)
    {
        float below = (o.Z + center.Z - lo) - (d.O.Z + d.AboveEye),
              above = (d.O.Z - d.EyeHeight) - (o.Z + center.Z + hi);
        if (below >= 0 || above >= 0) return false;
        Vector3 yo = center;
        yo = RotateAroundZ(yo, yaw * (Mathf.Pi / 180f));
        yo += o;
        float x = yo.X - d.O.X, y = yo.Y - d.O.Y;
        float angle = Mathf.Atan2(y, x), dangle = angle - (d.Yaw + 90) * (Mathf.Pi / 180f), eangle = angle - (yaw + 90) * (Mathf.Pi / 180f);
        float dx = d.XRadius * Mathf.Cos(dangle), dy = d.YRadius * Mathf.Sin(dangle);
        float ex = xr * Mathf.Cos(eangle), ey = yr * Mathf.Sin(eangle);
        float dist = Mathf.Sqrt(x * x + y * y) - Mathf.Sqrt(dx * dx + dy * dy) - Mathf.Sqrt(ex * ex + ey * ey);
        if (dist < 0)
        {
            if (dist > (d.O.Z < yo.Z ? below : above) && (dir == Vector3.Zero || x * dir.X + y * dir.Y > 0))
            {
                CollideWall = new Vector3(-x, -y, 0);
                if (CollideWall != Vector3.Zero) CollideWall = CollideWall.Normalized();
                return true;
            }
            if (d.O.Z < yo.Z)
            {
                if (dir == Vector3.Zero || (dir.Z > 0 && (d.Type >= Phys.EntInanimate || below >= d.Zmargin - (d.EyeHeight + d.AboveEye) / 4.0f)))
                {
                    CollideWall = new Vector3(0, 0, -1);
                    return true;
                }
            }
            else if (dir == Vector3.Zero || (dir.Z < 0 && (d.Type >= Phys.EntInanimate || above >= d.Zmargin - (d.EyeHeight + d.AboveEye) / 3.0f)))
            {
                CollideWall = new Vector3(0, 0, 1);
                return true;
            }
            CollideInside++;
        }
        return false;
    }

    /// <summary>geom.h <c>vec::rotate_around_z</c> (the engine's clockwise-around-Z form).</summary>
    private static Vector3 RotateAroundZ(Vector3 v, float radians)
    {
        float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
        return new Vector3(c * v.X - s * v.Y, s * v.X + c * v.Y, v.Z);
    }

    /// <summary>geom.h <c>vec::reject</c> — <c>|v-o| >= radius</c> means no possible overlap.</summary>
    private static bool Reject(Vector3 v, Vector3 o, float radius) => v.DistanceSquaredTo(o) > radius * radius;

    // ---- dynent cache (physics.cpp:587) ----------------------------------

    /// <summary>engine <c>VARF(dynentsize, 4, 7, 12)</c>.</summary>
    public int DynEntSize = 7;

    private const int DynEntCacheSize = 1024;
    private sealed class DynEntCacheEntry
    {
        public int X = int.MinValue, Y = int.MinValue;
        public uint Frame;
        public readonly System.Collections.Generic.List<PhysEnt> DynEnts = new();
    }
    private readonly DynEntCacheEntry[] _dynEntCache = new DynEntCacheEntry[DynEntCacheSize];
    private uint _dynEntFrame;

    /// <summary>The alive dynents in the world (players, monsters, movables). The game
    /// layer registers them here; the engine iterates <c>game::iterdynents</c>.</summary>
    public readonly System.Collections.Generic.List<PhysEnt> DynEnts = new();

    /// <summary>game::dynentcollide seam — called when a collide() hits a dynent.
    /// Null in production; used by the game/weapon layer later.</summary>
    public event System.Action<PhysEnt, PhysEnt, Vector3>? DynEntCollide;

    /// <summary>physics.cpp:591 <c>cleardynentcache</c>.</summary>
    public void ClearDynentCache()
    {
        _dynEntFrame++;
        if (_dynEntFrame == 0 || _dynEntFrame == 1)
            for (int i = 0; i < DynEntCacheSize; i++) _dynEntCache[i].Frame = 0;
        if (_dynEntFrame == 0) _dynEntFrame = 1;
    }

    private static int DynEntHash(int x, int y)
    {
        int k = x ^ y;
        return (((k << 5) + (k >> 5)) & (DynEntCacheSize - 1));
    }

    /// <summary>physics.cpp:596 <c>checkdynentcache</c> (building the bucket if stale).</summary>
    public System.Collections.Generic.List<PhysEnt> CheckDynentCache(int x, int y)
    {
        var dec = _dynEntCache[DynEntHash(x, y)];
        if (dec.X == x && dec.Y == y && dec.Frame == _dynEntFrame) return dec.DynEnts;
        dec.X = x; dec.Y = y; dec.Frame = _dynEntFrame;
        dec.DynEnts.Clear();
        int dsize = 1 << DynEntSize, dx = x << DynEntSize, dy = y << DynEntSize;
        foreach (var d in DynEnts)
        {
            if (d.State != PhysEnt.CsAlive ||
                d.O.X + d.Radius <= dx || d.O.X - d.Radius >= dx + dsize ||
                d.O.Y + d.Radius <= dy || d.O.Y - d.Radius >= dy + dsize) continue;
            dec.DynEnts.Add(d);
        }
        return dec.DynEnts;
    }

    /// <summary>physics.cpp:616 <c>updatedynentcache</c> — add a dynent to every bucket it overlaps.</summary>
    public void UpdateDynentCache(PhysEnt d)
    {
        LoopDynentCache(d.O, d.Radius, (x, y) =>
        {
            var dec = _dynEntCache[DynEntHash(x, y)];
            if (dec.X != x || dec.Y != y || dec.Frame != _dynEntFrame || dec.DynEnts.Contains(d)) return false;
            dec.DynEnts.Add(d);
            return false;
        });
    }

    /// <summary>physics.cpp:625 <c>overlapsdynent</c> — is any dynent within <paramref name="radius"/> of <paramref name="o"/>?</summary>
    public bool OverlapsDynent(Vector3 o, float radius)
    {
        bool found = false;
        LoopDynentCache(o, radius, (x, y) =>
        {
            foreach (var d in CheckDynentCache(x, y))
                if (o.DistanceTo(d.O) - d.Radius < radius) { found = true; return true; }
            return false;
        });
        return found;
    }

    /// <summary>physics.cpp:617 <c>loopdynentcache</c> — iterate the buckets overlapping a circle.
    /// <paramref name="body"/> returns true to stop early.</summary>
    private void LoopDynentCache(Vector3 o, float radius, System.Func<int, int, bool> body)
    {
        int curx, endx, cury, endy;
        bool stop = false;
        for (curx = Mathf.Max((int)(o.X - radius), 0) >> DynEntSize, endx = Mathf.Min((int)(o.X + radius), _octa.WorldSize - 1) >> DynEntSize; curx <= endx && !stop; curx++)
            for (cury = Mathf.Max((int)(o.Y - radius), 0) >> DynEntSize, endy = Mathf.Min((int)(o.Y + radius), _octa.WorldSize - 1) >> DynEntSize; cury <= endy; cury++)
                if (body(curx, cury)) { stop = true; break; }
    }

    /// <summary>physics.cpp:1014 <c>octacollide</c> (the readable recursive form).</summary>
    private bool OctaCollide(PhysEnt d, Vector3 dir, float cutoff, Vector3I bo, Vector3I bs, Cube[] c, Vector3I cor, int size)
    {
        int possible = OctaBoxOverlap(cor, size, bo, bs);
        for (int i = 0; i < 8; i++)
        {
            if ((possible & (1 << i)) == 0) continue;
            var o = new Vector3I(cor.X + ((i & 1) >> 0) * size, cor.Y + ((i & 2) >> 1) * size, cor.Z + ((i & 4) >> 2) * size);
            Cube[]? children = c[i].Children;
            if (children != null)
            {
                if (OctaCollide(d, dir, cutoff, bo, bs, children, o, size >> 1)) return true;
                continue;
            }
            bool solid = false;
            switch (c[i].Material & Phys.MatFClip)
            {
                case Phys.MatNoClip: continue;
                case Phys.MatGameClip: if (d.Type == Phys.EntAi) solid = true; break;
                case Phys.MatClip:
                    if (Phys.IsClipped(c[i].Material) || d.Type < Phys.EntCamera) solid = true;
                    break;
            }
            if (!solid && c[i].IsEmpty()) continue;
            if (CubeCollide(d, dir, cutoff, c[i], o, size, solid)) return true;
        }
        return false;
    }

    /// <summary>physics.cpp:1006 <c>cubecollide</c> (COLLIDE_ELLIPSE = the fuzzy path).</summary>
    private bool CubeCollide(PhysEnt d, Vector3 dir, float cutoff, Cube c, Vector3I co, int size, bool solid)
    {
        if (d.CollideType == Phys.CollideObb) return false;
        bool entire = c.IsEntirelySolid() || solid;
        bool r = entire ? FuzzyCollideSolid(d, dir, cutoff, c, co, size) : FuzzyCollidePlanes(d, dir, cutoff, c, co, size);
        if (MoveTrace != null) MT($"    cubecollide co={co} size={size} entire={entire} solid={solid} -> {r} wall={CollideWall}");
        return r;
    }

    /// <summary>physics.cpp:777 <c>fuzzycollidesolid</c> (AABB test).</summary>
    private bool FuzzyCollideSolid(PhysEnt d, Vector3 dir, float cutoff, Cube c, Vector3I co, int size)
    {
        float crad = size * 0.5f;
        if (Mathf.Abs(d.O.X - co.X - crad) > d.Radius + crad || Mathf.Abs(d.O.Y - co.Y - crad) > d.Radius + crad ||
            d.O.Z + d.AboveEye < co.Z || d.O.Z - d.EyeHeight > co.Z + size) return false;

        CollideWall = Vector3.Zero;
        float bestdist = -1e10f;
        int visible = c.IsEntirelySolid() ? OctaVisible.CollisionVisibleMask(_octa, c, co, size) : 0xFF;

        bool Side(int side, float dist, float dotval, float margin, Vector3 normal)
        {
            if ((visible & (1 << side)) == 0) return false;
            if (dist > 0) return true;               // not colliding: caller aborts
            if (dist <= bestdist) return false;
            if (dir != Vector3.Zero)
            {
                if (dotval >= -cutoff * dir.Length()) return false;
                if (d.Type < Phys.EntCamera && dotval < 0 && dist < margin) return false;
            }
            CollideWall = normal;
            bestdist = dist;
            return false;
        }

        if (Side(0, co.X - (d.O.X + d.Radius), -dir.X, -d.Radius, new Vector3(-1, 0, 0))) return false;
        if (Side(1, d.O.X - d.Radius - (co.X + size), dir.X, -d.Radius, new Vector3(1, 0, 0))) return false;
        if (Side(2, co.Y - (d.O.Y + d.Radius), -dir.Y, -d.Radius, new Vector3(0, -1, 0))) return false;
        if (Side(3, d.O.Y - d.Radius - (co.Y + size), dir.Y, -d.Radius, new Vector3(0, 1, 0))) return false;
        if (Side(4, co.Z - (d.O.Z + d.AboveEye), -dir.Z, d.Zmargin - (d.EyeHeight + d.AboveEye) / 4.0f, new Vector3(0, 0, -1))) return false;
        if (Side(5, d.O.Z - d.EyeHeight - (co.Z + size), dir.Z, d.Zmargin - (d.EyeHeight + d.AboveEye) / 3.0f, new Vector3(0, 0, 1))) return false;

        if (CollideWall == Vector3.Zero) { CollideInside++; return false; }
        return true;
    }

    /// <summary>physics.cpp:843 <c>fuzzycollideplanes</c> — COLLIDE_ELLIPSE vs a deformed cube.</summary>
    private bool FuzzyCollidePlanes(PhysEnt d, Vector3 dir, float cutoff, Cube c, Vector3I co, int size)
    {
        int mask = OctaVisible.CollisionVisibleMask(_octa, c, co, size);
        var p = OctaClipping.GetClipPlanes(_octa, c, co, size, mask);
        if (Mathf.Abs(d.O.X - p.O.X) > p.R.X + d.Radius || Mathf.Abs(d.O.Y - p.O.Y) > p.R.Y + d.Radius ||
            d.O.Z + d.AboveEye < p.O.Z - p.R.Z || d.O.Z - d.EyeHeight > p.O.Z + p.R.Z) return false;

        CollideWall = Vector3.Zero;
        float bestdist = -1e10f;
        int visible = p.Visible;
        if (MoveTrace != null) MT($"    fcp co={co} size={size} mask={mask} pVis={p.Visible} nplanes={p.Size} o={d.O} dir={dir}");

        bool Side(int side, float dist, float dotval, float margin, Vector3 normal)
        {
            if ((visible & (1 << side)) == 0) return false;
            if (dist > 0) return true;
            if (dist <= bestdist) return false;
            if (dir != Vector3.Zero)
            {
                if (dotval >= -cutoff * dir.Length()) return false;
                if (d.Type < Phys.EntCamera && dotval < 0 && dist < margin) return false;
            }
            CollideWall = normal; bestdist = dist;
            return false;
        }

        if (Side(0, p.O.X - p.R.X - (d.O.X + d.Radius), -dir.X, -d.Radius, new Vector3(-1, 0, 0))) return false;
        if (Side(1, d.O.X - d.Radius - (p.O.X + p.R.X), dir.X, -d.Radius, new Vector3(1, 0, 0))) return false;
        if (Side(2, p.O.Y - p.R.Y - (d.O.Y + d.Radius), -dir.Y, -d.Radius, new Vector3(0, -1, 0))) return false;
        if (Side(3, d.O.Y - d.Radius - (p.O.Y + p.R.Y), dir.Y, -d.Radius, new Vector3(0, 1, 0))) return false;
        if (Side(4, p.O.Z - p.R.Z - (d.O.Z + d.AboveEye), -dir.Z, d.Zmargin - (d.EyeHeight + d.AboveEye) / 4.0f, new Vector3(0, 0, -1))) return false;
        if (Side(5, d.O.Z - d.EyeHeight - (p.O.Z + p.R.Z), dir.Z, d.Zmargin - (d.EyeHeight + d.AboveEye) / 3.0f, new Vector3(0, 0, 1))) return false;

        var entvol = new EntCapsule(d);
        int bestplane = -1;
        for (int i = 0; i < p.Size; i++)
        {
            var w = p.P[i];
            Vector3 pw = entvol.SupportPoint(-w.N);
            float dist = w.Dist(pw);
            if (dist >= 0) return false;
            if (dist <= bestdist) continue;
            bestplane = -1; bestdist = dist;
            if (dir != Vector3.Zero)
            {
                if (w.N.Dot(dir) >= -cutoff * dir.Length()) continue;
                if (d.Type < Phys.EntCamera &&
                    dist < (dir.Z * w.N.Z < 0
                        ? d.Zmargin - (d.EyeHeight + d.AboveEye) / (dir.Z < 0 ? 3.0f : 4.0f)
                        : ((dir.X * w.N.X < 0 || dir.Y * w.N.Y < 0) ? -d.Radius : 0))) continue;
            }
            if (OctaClipping.ClampCollide(p, entvol, w, pw)) continue;
            bestplane = i;
        }
        if (bestplane >= 0) CollideWall = p.P[bestplane].N;
        else if (CollideWall == Vector3.Zero) { CollideInside++; return false; }
        return true;
    }

    /// <summary>
    /// The engine's <c>c.visible</c> collision mask (set by setcubevisibility()):
    /// the faces whose neighbour is not entirely solid. Approximated by test-sampling
    /// the neighbour; enough for solid terrain.
    /// </summary>
    private int CubeVisibleMask(Cube c, Vector3I co, int size)
    {
        int mask = 0;
        for (int orient = 0; orient < 6; orient++)
        {
            int dim = orient >> 1, dir = (orient & 1) == 0 ? -1 : 1;
            int nc = co[dim] + dir * size;
            if (nc < 0 || nc >= _octa.WorldSize) { mask |= 1 << orient; continue; }
            var centre = co;
            centre[dim] = nc + size / 2;
            centre[(dim + 1) % 3] = co[(dim + 1) % 3] + size / 2;
            centre[(dim + 2) % 3] = co[(dim + 2) % 3] + size / 2;
            var nb = _octa.LeafCube(centre);
            if (nb == null || !nb.IsEntirelySolid()) mask |= 1 << orient;
        }
        return mask;
    }

    /// <summary>octa.h:282 <c>octaboxoverlap</c>.</summary>
    private static int OctaBoxOverlap(Vector3I o, int size, Vector3I bbmin, Vector3I bbmax)
    {
        int p = 0xFF;
        var mid = new Vector3I(o.X + size, o.Y + size, o.Z + size);
        if (mid.Z <= bbmin.Z) p &= 0xF0; else if (mid.Z >= bbmax.Z) p &= 0x0F;
        if (mid.Y <= bbmin.Y) p &= 0xCC; else if (mid.Y >= bbmax.Y) p &= 0x33;
        if (mid.X <= bbmin.X) p &= 0xAA; else if (mid.X >= bbmax.X) p &= 0x55;
        return p;
    }

    // ---- move() + step machine -------------------------------------------

    public void RecalcDir(PhysEnt d, Vector3 oldvel, ref Vector3 dir)
    {
        float speed = oldvel.Length();
        if (speed > 1e-6f)
        {
            float step = dir.Length();
            dir = d.Vel + d.Falling;
            dir *= step / speed;
        }
    }

    public void SlideAgainst(PhysEnt d, ref Vector3 dir, Vector3 obstacle, bool foundfloor, bool slidecollide)
    {
        var wall = obstacle;
        if (foundfloor ? wall.Z > 0 : slidecollide)
        {
            wall.Z = 0;
            if (wall != Vector3.Zero) wall = wall.Normalized();
        }
        Vector3 oldvel = d.Vel + d.Falling;
        d.Vel = Project(d.Vel, wall);
        d.Falling = Project(d.Falling, wall);
        RecalcDir(d, oldvel, ref dir);
    }

    public void SwitchFloor(PhysEnt d, ref Vector3 dir, Vector3 floor)
    {
        if (floor.Z >= Phys.FloorZ) d.Falling = Vector3.Zero;
        Vector3 oldvel = d.Vel + d.Falling;
        if (dir.Dot(floor) >= 0)
        {
            if (d.PhysState < Phys.PhysSlide || Mathf.Abs(dir.Dot(d.Floor)) > 0.01f * dir.Length()) return;
            d.Vel = ProjectXy(d.Vel, floor, 0.0f);
        }
        else d.Vel = ProjectXy(d.Vel, floor);
        d.Falling = Project(d.Falling, floor);
        RecalcDir(d, oldvel, ref dir);
    }

    public bool TryStepUp(PhysEnt d, ref Vector3 dir, Vector3 obstacle, float maxstep, Vector3 floor)
    {
        Vector3 old = d.O;
        // geom.h rescale(1): mul(1/mag), NOT div(mag) — the rounding differs.
        Vector3 stairdir = obstacle.Z >= 0 && obstacle.Z < Phys.SlopeZ ? new Vector3(-obstacle.X, -obstacle.Y, 0) : new Vector3(dir.X, dir.Y, 0);
        { float mag = stairdir.Length(); if (mag > 1e-6f) stairdir *= 1.0f / mag; }
        bool cansmooth = true;
        if (MoveTrace != null) MT($"  tsu enter obstacle={obstacle} maxstep={maxstep} floor={floor} stairdir={stairdir} ps={d.PhysState}");
        if (d.PhysState != Phys.PhysStepUp)
        {
            Vector3 checkdir = stairdir * 0.1f;
            checkdir.Z += maxstep + 0.1f;
            d.O += checkdir;
            if (Collide(d, Vector3.Zero))
            {
                d.O = old;
                if (!Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ)) { if (MoveTrace != null) MT("  tsu false (headroom, no floor)"); return false; }
                cansmooth = false;
            }
        }
        if (MoveTrace != null) MT($"  tsu cansmooth={cansmooth}");
        if (cansmooth)
        {
            Vector3 checkdir = stairdir; checkdir.Z += 1; checkdir *= maxstep;
            d.O = old + checkdir;
            int scale = 2;
            if (Collide(d, checkdir))
            {
                if (!Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ)) { d.O = old; return false; }
                d.O += checkdir;
                if (Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ)) scale = 1;
            }
            if (scale != 1)
            {
                d.O = old;
                d.O -= checkdir * new Vector3(2, 2, 1);
                if (!Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ)) scale = 1;
            }
            d.O = old;
            Vector3 smoothdir = new Vector3(dir.X, dir.Y, 0);
            float magxy = smoothdir.Length();
            if (magxy > 1e-9f)
            {
                if (magxy > scale * dir.Z)
                {
                    smoothdir *= 1.0f / magxy; // vec::mul(1/magxy), not div(magxy)
                    smoothdir.Z = 1.0f / scale;
                    smoothdir *= dir.Length() / smoothdir.Length();
                }
                else smoothdir.Z = dir.Z;
                // Reference: d->o.add(smoothdir); d->o.z += maxstep+0.1f; [collide];
                // d->o.z -= maxstep+0.1f. Under -ffast-math the compiler folds the
                // two z adds, so the settled z is exactly old+smoothdir.z (no float
                // round-trip loss). Reproduce that here instead of add/subtracting.
                float zadd = d.O.Z + smoothdir.Z;
                d.O += smoothdir;
                d.O = d.O with { Z = zadd + (maxstep + 0.1f) };
                bool smoothHit = Collide(d, smoothdir);
                if (MoveTrace != null) MT($"  tsu smoothdir={smoothdir} scale={scale} smoothCollide={smoothHit}");
                if (!smoothHit)
                {
                    d.O = d.O with { Z = zadd };
                    if (d.PhysState == Phys.PhysFall || d.Floor != floor)
                    {
                        d.TimeInAir = 0; d.Floor = floor; SwitchFloor(d, ref dir, d.Floor);
                    }
                    d.PhysState = Phys.PhysStepUp;
                    if (MoveTrace != null) MT("  tsu true (smooth)");
                    return true;
                }
            }
        }
        d.O = old;
        d.O = d.O with { Z = d.O.Z + dir.Length() };
        bool upHit = Collide(d, new Vector3(0, 0, 1));
        if (MoveTrace != null) MT($"  tsu plain-up collide={upHit}");
        if (!upHit)
        {
            if (d.PhysState == Phys.PhysFall || d.Floor != floor)
            {
                d.TimeInAir = 0; d.Floor = floor; SwitchFloor(d, ref dir, d.Floor);
            }
            if (cansmooth) d.PhysState = Phys.PhysStepUp;
            if (MoveTrace != null) MT("  tsu true (plain)");
            return true;
        }
        d.O = old;
        if (MoveTrace != null) MT("  tsu false");
        return false;
    }

    public bool TryStepDown(PhysEnt d, ref Vector3 dir, float step, float xy, float z, bool init = false)
    {
        Vector3 stepdir = new(dir.X, dir.Y, 0);
        stepdir.Z = -(float)Mathf.Sqrt(dir.X * dir.X + dir.Y * dir.Y) * z / xy; // magnitude2()
        if (stepdir.Z == 0) return false;
        stepdir = stepdir.Normalized();

        Vector3 old = d.O;
        d.O += stepdir * (Phys.StairHeight / Mathf.Abs(stepdir.Z));
        d.O = d.O with { Z = d.O.Z - Phys.StairHeight };
        d.Zmargin = -Phys.StairHeight;
        if (Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ))
        {
            d.O = old + stepdir * step;
            d.Zmargin = 0;
            if (!Collide(d, new Vector3(0, 0, -1)))
            {
                Vector3 stepfloor = stepdir * -stepdir.Z; // mul(-stepdir.z) scales all components
                stepfloor = stepfloor with { Z = stepfloor.Z + 1 };
                stepfloor = stepfloor.Normalized();
                if (d.PhysState >= Phys.PhysSlope && d.Floor != stepfloor)
                {
                    Vector3 stepped = d.O;
                    d.O = d.O with { Z = d.O.Z - 0.5f };
                    d.Zmargin = -0.5f;
                    if (Collide(d, stepdir) && CollideWall == d.Floor)
                    {
                        d.O = old;
                        if (!init) { d.O += new Vector3(dir.X, dir.Y, 0); if (dir.Z <= 0 || Collide(d, dir)) d.O = d.O with { Z = d.O.Z + dir.Z }; }
                        d.Zmargin = 0; d.PhysState = Phys.PhysStepDown; d.TimeInAir = 0;
                        return true;
                    }
                    d.O = init ? old : stepped; d.Zmargin = 0;
                }
                else if (init) d.O = old;
                SwitchFloor(d, ref dir, stepfloor);
                d.Floor = stepfloor; d.PhysState = Phys.PhysStepDown; d.TimeInAir = 0;
                return true;
            }
        }
        d.O = old; d.Zmargin = 0;
        return false;
    }

    public bool TryStepDown(PhysEnt d, ref Vector3 dir, bool init = false)
    {
        if ((d.Move == 0 && d.Strafe == 0)) return false;
        Vector3 old = d.O;
        d.O = d.O with { Z = d.O.Z - Phys.StairHeight };
        d.Zmargin = -Phys.StairHeight;
        if (!Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ)) { d.O = old; d.Zmargin = 0; return false; }
        d.O = old; d.Zmargin = 0;
        float step = dir.Length();
        if (TryStepDown(d, ref dir, step, 4, 1, init)) return true;
        return false;
    }

    public void Falling(PhysEnt d, ref Vector3 dir, Vector3 floor)
    {
        if (floor.Z > 0.0f && floor.Z < Phys.SlopeZ)
        {
            if (floor.Z >= Phys.WallZ) SwitchFloor(d, ref dir, floor);
            d.TimeInAir = 0; d.PhysState = Phys.PhysSlide; d.Floor = floor;
        }
        else if (d.PhysState < Phys.PhysSlope || dir.Dot(d.Floor) > 0.01f * dir.Length() || (floor.Z != 0.0f && floor.Z != 1.0f) || !TryStepDown(d, ref dir, true))
            d.PhysState = Phys.PhysFall;
    }

    public void Landing(PhysEnt d, ref Vector3 dir, Vector3 floor, bool collided)
    {
        SwitchFloor(d, ref dir, floor);
        d.TimeInAir = 0;
        if ((d.PhysState != Phys.PhysStepUp && d.PhysState != Phys.PhysStepDown) || !collided)
            d.PhysState = floor.Z >= Phys.FloorZ ? Phys.PhysFloor : Phys.PhysSlope;
        d.Floor = floor;
    }

    public bool FindFloor(PhysEnt d, bool collided, Vector3 obstacle, ref bool slide, ref Vector3 floor)
    {
        bool found = false;
        Vector3 moved = d.O;
        d.O = d.O with { Z = d.O.Z - 0.1f };
        if (Collide(d, new Vector3(0, 0, -1), d.PhysState == Phys.PhysSlope || d.PhysState == Phys.PhysStepDown ? Phys.SlopeZ : Phys.FloorZ))
        {
            floor = CollideWall; found = true;
        }
        else if (collided && obstacle.Z >= Phys.SlopeZ) { floor = obstacle; found = true; slide = false; }
        else if (d.PhysState == Phys.PhysStepUp || d.PhysState == Phys.PhysSlide)
        {
            if (Collide(d, new Vector3(0, 0, -1)) && CollideWall.Z > 0.0f)
            {
                floor = CollideWall;
                if (floor.Z >= Phys.SlopeZ) found = true;
            }
        }
        else if (d.PhysState >= Phys.PhysSlope && d.Floor.Z < 1.0f)
        {
            if (Collide(d, -d.Floor, 0.95f) || Collide(d, new Vector3(0, 0, -1)))
            {
                floor = CollideWall;
                if (floor.Z >= Phys.SlopeZ && floor.Z < 1.0f) found = true;
            }
        }
        if (collided && (!found || obstacle.Z > floor.Z))
        {
            floor = obstacle;
            slide = !found && (floor.Z < Phys.WallZ || floor.Z >= Phys.SlopeZ);
        }
        d.O = moved;
        return found;
    }

    /// <summary>Optional diagnostic sink for <see cref="Move"/> (null in production).
    /// Set by test probes to diff the move() decision chain against the reference.</summary>
    public System.Action<string>? MoveTrace { get; set; }
    private void MT(string s) { MoveTrace?.Invoke(s); }

    /// <summary>physics.cpp:1340 <c>move</c>.</summary>
    public bool Move(PhysEnt d, ref Vector3 dir)
    {
        Vector3 old = d.O;
        bool collided = false, slidecollide = false;
        Vector3 obstacle = Vector3.Zero;
        d.O += dir;
        if (MoveTrace != null) MT($"move in o={d.O} dir={dir} ps={d.PhysState} floor={d.Floor}");
        // physics.cpp:1347 — AI/inanimate entities also test a zero-direction collide
        // (a starting-inside-geometry guard); the player does not.
        if (Collide(d, dir) || ((d.Type == Phys.EntAi || d.Type == Phys.EntInanimate) && Collide(d, Vector3.Zero, 0.0f, false, false)))
        {
            obstacle = CollideWall;
            if (MoveTrace != null) MT($"  collide hit obstacle={obstacle} ps={d.PhysState}");
            if (d.Type == Phys.EntPlayer && ((CollideWall.Z >= Phys.SlopeZ && dir.Z < 0) || (CollideWall.Z <= -Phys.SlopeZ && dir.Z > 0)) && (dir.X != 0 || dir.Y != 0) && Collide(d, new Vector3(dir.X, dir.Y, 0)))
            {
                if (CollideWall.Dot(dir) >= 0) slidecollide = true;
                obstacle = CollideWall;
            }
            d.O = old;
            d.O = d.O with { Z = d.O.Z - Phys.StairHeight };
            d.Zmargin = -Phys.StairHeight;
            bool tryup = d.PhysState == Phys.PhysSlope || d.PhysState == Phys.PhysFloor ||
                (Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ) && (d.PhysState == Phys.PhysStepUp || d.PhysState == Phys.PhysStepDown || CollideWall.Z >= Phys.FloorZ));
            if (MoveTrace != null) MT($"  stepup-branch tryup={tryup} ps={d.PhysState}");
            if (tryup)
            {
                d.O = old; d.Zmargin = 0;
                if (TryStepUp(d, ref dir, obstacle, Phys.StairHeight, d.PhysState == Phys.PhysSlope || d.PhysState == Phys.PhysFloor ? d.Floor : CollideWall)) { if (MoveTrace != null) MT("  trystepup -> true"); return true; }
            }
            else { d.O = old; d.Zmargin = 0; }
            collided = true;
        }
        else if (d.PhysState == Phys.PhysStepUp)
        {
            bool c = Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ);
            if (MoveTrace != null) MT($"  stepup-state collide_down={c}");
            if (c)
            {
                d.O = old;
                if (TryStepUp(d, ref dir, new Vector3(0, 0, 1), Phys.StairHeight, CollideWall)) { if (MoveTrace != null) MT("  trystepup -> true"); return true; }
                d.O += dir;
            }
        }
        else if (d.PhysState == Phys.PhysStepDown && dir.Dot(d.Floor) <= 1e-6f)
        {
            Vector3 moved = d.O;
            d.O = old;
            if (TryStepDown(d, ref dir)) { if (MoveTrace != null) MT("  trystepdown -> true"); return true; }
            d.O = moved;
        }
        Vector3 floor = Vector3.Zero;
        bool slide = collided;
        bool found = FindFloor(d, collided, obstacle, ref slide, ref floor);
        if (MoveTrace != null) MT($"  findfloor collided={collided} found={found} slide={slide} floor={floor}");
        if (slide || (!collided && floor.Z > 0 && floor.Z < Phys.WallZ))
        {
            SlideAgainst(d, ref dir, slide ? obstacle : floor, found, slidecollide);
            d.Blocked = true;
        }
        if (found) Landing(d, ref dir, floor, collided);
        else Falling(d, ref dir, floor);
        if (MoveTrace != null) MT($"move out collided={collided} found={found} ps={d.PhysState} vel={d.Vel} floor={d.Floor}");
        return !collided;
    }

    // ---- bounce (ENT_BOUNCE physics) ------------------------------------

    /// <summary>game::bounced seam — called with the reflect surface on each bounce
    /// (fpsgame/weapon.cpp:222 plays a sound for the projectile cases). Null in
    /// production; wired by the game layer later.</summary>
    public event System.Action<PhysEnt, Vector3>? Bounced;

    /// <summary>physics.cpp:1796 <c>bounce</c> driver: steps <paramref name="physsteps"/> + 1
    /// fixed substeps from the accumulated <see cref="PhysEnt.NewPos"/>, writing the
    /// result back and refreshing interpolation. Returns true if a player was hit.
    /// The engine reads the global <c>physsteps</c>/<c>physframetime</c>; they are
    /// passed in so the port stays free of the render-time scheduler.</summary>
    public bool Bounce(PhysEnt d, float elasticity, float waterfric, float grav, int physsteps, int physframetime)
    {
        if (physsteps <= 0)
        {
            InterpPos(d, 0, 0, physframetime);
            return false;
        }

        d.O = d.NewPos;
        bool hitplayer = false;
        for (int i = 0; i < physsteps - 1; i++)
            if (Bounce(d, physframetime / 1000.0f, elasticity, waterfric, grav)) hitplayer = true;
        d.DeltaPos = d.O;
        if (Bounce(d, physframetime / 1000.0f, elasticity, waterfric, grav)) hitplayer = true;
        d.NewPos = d.O;
        d.DeltaPos -= d.NewPos;
        InterpPos(d, 0, 0, physframetime, enabled: false);
        return hitplayer;
    }

    /// <summary>
    /// Port of <c>physics.cpp:1446 avoidcollision</c>.
    /// Adjusts <paramref name="d"/> position along <paramref name="dir"/> to avoid starting inside
    /// <paramref name="obstacle"/> bounding box with given <paramref name="space"/> margin.
    /// </summary>
    public static void AvoidCollision(PhysEnt d, Vector3 dir, PhysEnt obstacle, float space)
    {
        float rad = obstacle.Radius + d.Radius;
        Vector3 bbmin = obstacle.O;
        bbmin.X -= rad + space;
        bbmin.Y -= rad + space;
        bbmin.Z -= (obstacle.EyeHeight + d.AboveEye + space);

        Vector3 bbmax = obstacle.O;
        bbmax.X += rad + space;
        bbmax.Y += rad + space;
        bbmax.Z += (obstacle.AboveEye + d.EyeHeight + space);

        if (d.O.X <= bbmin.X || d.O.X >= bbmax.X ||
            d.O.Y <= bbmin.Y || d.O.Y >= bbmax.Y ||
            d.O.Z <= bbmin.Z || d.O.Z >= bbmax.Z)
        {
            return;
        }

        float minDist = 1e16f;
        for (int i = 0; i < 3; i++)
        {
            float di = i switch { 0 => dir.X, 1 => dir.Y, _ => dir.Z };
            if (di != 0f)
            {
                float targetCoord = di > 0f ? (i switch { 0 => bbmax.X, 1 => bbmax.Y, _ => bbmax.Z })
                                            : (i switch { 0 => bbmin.X, 1 => bbmin.Y, _ => bbmin.Z });
                float currentCoord = i switch { 0 => d.O.X, 1 => d.O.Y, _ => d.O.Z };
                float dist = (targetCoord - currentCoord) / di;
                minDist = Mathf.Min(minDist, dist);
            }
        }

        if (minDist >= 0.0f && minDist < 1e15f)
        {
            d.O += dir * minDist;
            d.NewPos = d.O;
        }
    }

    /// <summary>physics.cpp:1764 <c>interppos</c>. <paramref name="lastphysframe"/> and
    /// <paramref name="lastmillis"/> are passed in (the engine reads globals); with the
    /// defaults (<c>diff &lt;= 0</c>) this is just <c>o = newpos</c>, matching a settled
    /// frame. <paramref name="enabled"/> maps to <c>physinterp</c>.</summary>
    public static void InterpPos(PhysEnt pl, int lastphysframe, int lastmillis, int physframetime, bool enabled = true)
    {
        pl.O = pl.NewPos;

        int diff = lastphysframe - lastmillis;
        if (diff <= 0 || !enabled) return;

        Vector3 deltapos = pl.DeltaPos;
        deltapos *= Mathf.Min(diff, physframetime) / (float)physframetime;
        pl.O += deltapos;
    }

    /// <summary>physics.cpp:1402 <c>bounce</c> — one substep of ENT_BOUNCE physics:
    /// gravity, then per sub-collision reflect the velocity off the surface scaled by
    /// <paramref name="elasticity"/>. Returns true if a player was hit.</summary>
    public bool Bounce(PhysEnt d, float secs, float elasticity, float waterfric, float grav)
    {
        // make sure bouncers don't start inside geometry
        if (d.PhysState != Phys.PhysBounce && Collide(d, Vector3.Zero, 0.0f, false, false)) return true;
        int mat = _octa.LookupMaterial(new Vector3(d.O.X, d.O.Y, d.O.Z + (d.AboveEye - d.EyeHeight) / 2));
        bool water = Phys.IsLiquid((ushort)(mat & Phys.MatFVolume));
        if (water)
        {
            d.Vel = d.Vel with { Z = d.Vel.Z - grav * Phys.Gravity / 16 * secs };
            d.Vel *= Mathf.Max(1.0f - secs / waterfric, 0.0f);
        }
        else d.Vel = d.Vel with { Z = d.Vel.Z - grav * Phys.Gravity * secs };
        Vector3 old = d.O;
        for (int i = 0; i < 2; i++)
        {
            Vector3 dir = d.Vel * secs;
            d.O += dir;
            if (!Collide(d, dir, 0.0f, true, true))
            {
                if (CollideInside != 0)
                {
                    d.O = old;
                    d.Vel *= -elasticity;
                }
                break;
            }
            else if (CollidePlayer != null) break;
            d.O = old;
            Bounced?.Invoke(d, CollideWall);
            float c = CollideWall.Dot(d.Vel),
                  k = 1.0f + (1.0f - elasticity) * c / d.Vel.Length();
            d.Vel *= k;
            d.Vel -= CollideWall * (elasticity * 2.0f * c);
        }
        if (d.PhysState != Phys.PhysBounce)
        {
            // make sure bouncers don't start inside geometry
            if (d.O == old) return CollidePlayer == null;
            d.PhysState = Phys.PhysBounce;
        }
        return CollidePlayer != null;
    }

    // ---- moveplayer ------------------------------------------------------

    /// <summary>
    /// physics.cpp:1681 <c>moveplayer</c>. <paramref name="floatingOverride"/> is a test
    /// seam: when null (the default) floating is derived from the player state exactly
    /// like the engine (<c>state == CS_EDITING || state == CS_SPECTATOR</c>).
    /// </summary>
    public void MovePlayer(PhysEnt pl, int moveres, int curtime, bool? floatingOverride = null, bool local = true)
    {
        int material = _octa.LookupMaterial(new Vector3(pl.O.X, pl.O.Y, pl.O.Z + (3 * pl.AboveEye - pl.EyeHeight) / 4));
        bool water = Phys.IsLiquid((ushort)(material & Phys.MatFVolume));
        bool floating = floatingOverride ?? (pl.Type == Phys.EntPlayer && (pl.State == PhysEnt.CsEditing || pl.State == PhysEnt.CsSpectator));
        float secs = curtime / 1000f;

        // apply gravity
        if (!floating) Movement.ModifyGravity(pl, water, curtime);
        // apply any player generated changes in velocity
        Movement.ModifyVelocity(pl, water, floating, curtime);

        Vector3 d = pl.Vel;
        if (!floating && water) d *= 0.5f;
        d += pl.Falling;
        d *= secs;
        pl.Blocked = false;

        if (floating)                // just apply velocity
        {
            if (pl.PhysState != Phys.PhysFloat) { pl.PhysState = Phys.PhysFloat; pl.TimeInAir = 0; pl.Falling = Vector3.Zero; }
            pl.O += d;
        }
        else                        // apply velocity with collision
        {
            float f = 1.0f / moveres;
            int collisions = 0;
            d *= f;
            for (int i = 0; i < moveres; i++)
                if (!Move(pl, ref d) && ++collisions < 5) i--; // discrete steps + sliding
        }

        // physics.cpp:1725 — smooth strafe roll (camera only; never affects collision).
        UpdateRoll(pl, floating, curtime);

        // physics.cpp:1731 — play sounds on water transitions. The engine re-looks-up
        // the material at the mid-body when the eye has left the volume, so a
        // chest-high step out of water still reports "exited" rather than swimming.
        if (pl.InWater != Phys.MatAir && !water)
        {
            material = _octa.LookupMaterial(new Vector3(pl.O.X, pl.O.Y, pl.O.Z + (pl.AboveEye - pl.EyeHeight) / 2));
            water = Phys.IsLiquid((ushort)(material & Phys.MatFVolume));
        }
        if (pl.InWater == Phys.MatAir && water) PhysicalTrigger(pl, local, 0, -1, material & Phys.MatFVolume);
        else if (pl.InWater != Phys.MatAir && !water) PhysicalTrigger(pl, local, 0, 1, pl.InWater);
        pl.InWater = water ? (material & Phys.MatFVolume) : Phys.MatAir;

        // physics.cpp:1740 — suicide on death material / out of the world (game logic).
        if (pl.O.Z < 0 || (material & Phys.MatDeath) != 0) pl.Dead = true;
    }

    /// <summary>physics.cpp:1725 — the automatic strafe roll (camera only).</summary>
    private static void UpdateRoll(PhysEnt pl, bool floating, int curtime)
    {
        if (floating) { pl.Roll = 0; return; }
        const float maxroll = 0.0f; // VARP(maxroll, 0, 0, 20): default off in r6889
        if (pl.Strafe != 0 && maxroll != 0) pl.Roll = 0; // (kept for parity; off by default)
        else if (curtime == Phys.PhysFrameTime) pl.Roll *= 0.95f;
        else pl.Roll *= Mathf.Pow(0.95f, curtime / (float)Phys.PhysFrameTime);
    }

    /// <summary>game::physicstrigger seam — sounds/events on floor/water transitions.
    /// Null in production; wired by the game layer later.</summary>
    public event System.Action<PhysEnt, bool, int, int, int>? PhysTrigger;
    private void PhysicalTrigger(PhysEnt pl, bool local, int floorLevel, int waterLevel, int material = -1)
        => PhysTrigger?.Invoke(pl, local, floorLevel, waterLevel, material);

    // ---- vector helpers (engine vec::project / projectxy) ----------------

    // geom.h:146 project(n): assumes a unit normal.
    private static Vector3 Project(Vector3 v, Vector3 n)
    {
        float k = v.Dot(n);
        return v - k * n;
    }

    // geom.h:148 projectxy(n): put z on the plane, then rescale to keep the length.
    private static Vector3 ProjectXy(Vector3 v, Vector3 n)
    {
        float m = v.LengthSquared(), k = v.Dot(n);
        if (n.Z != 0) v = v with { Z = -(v.X * n.X / n.Z + v.Y * n.Y / n.Z) };
        float mag = v.Length();
        if (mag != 0) v *= Mathf.Sqrt(Mathf.Max(m - k * k, 0f)) / mag;
        return v;
    }

    // geom.h:155 projectxy(n, threshold): as above, with dot clamped to the threshold.
    private static Vector3 ProjectXy(Vector3 v, Vector3 n, float threshold)
    {
        float m = v.LengthSquared(), k = Mathf.Min(v.Dot(n), threshold);
        if (n.Z != 0) v = v with { Z = -(v.X * n.X / n.Z + v.Y * n.Y / n.Z) };
        float mag = v.Length();
        if (mag != 0) v *= Mathf.Sqrt(Mathf.Max(m - k * k, 0f)) / mag;
        return v;
    }
}
