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
    public const ushort MatWater = 1 << 2, MatLava = 2 << 2, MatGlass = 3 << 2;
    public const ushort MatNoClip = 1 << 5, MatClip = 2 << 5, MatGameClip = 3 << 5;
    public const ushort MatFIndex = 3, MatFVolume = 7 << 2, MatFClip = 3 << 5;
    public const ushort MatAlpha = 4 << 8;

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
    public float Zmargin = 0.0f;
    public int PhysState = Phys.PhysFall;
    public int Type = Phys.EntPlayer;
    public int CollideType = Phys.CollideEllipse;
    public int Move, Strafe;
    public bool Blocked;
    public int TimeInAir;
    public float Yaw;
    public bool Jumping;
    public float MaxSpeed = 100.0f;
}

/// <summary>The move()/collide() state machine. One per world (collision is stateless).</summary>
public sealed class OctaPhysics
{
    private readonly Octa _octa;
    public OctaPhysics(Octa octa) { _octa = octa; }

    // collision outputs (engine globals collidewall / collideinside)
    public Vector3 CollideWall;
    public int CollideInside;

    // ---- octree collision -------------------------------------------------

    /// <summary>physics.cpp:1048 <c>collide</c>.</summary>
    public bool Collide(PhysEnt d, Vector3 dir, float cutoff = 0.0f)
    {
        CollideInside = 0;
        CollideWall = Vector3.Zero;
        var bo = new Vector3I((int)(d.O.X - d.Radius), (int)(d.O.Y - d.Radius), (int)(d.O.Z - d.EyeHeight));
        var bs = new Vector3I((int)(d.O.X + d.Radius), (int)(d.O.Y + d.Radius), (int)(d.O.Z + d.AboveEye));
        bs += Vector3I.One; // guard space (physics.cpp:1055)
        return OctaCollide(d, dir, cutoff, bo, bs, _octa.WorldRoot, Vector3I.Zero, _octa.WorldSize >> 1);
    }

    /// <summary>physics.cpp:1014 <c>octacollide</c> (the readable recursive form).</summary>
    private bool OctaCollide(PhysEnt d, Vector3 dir, float cutoff, Vector3I bo, Vector3I bs, Cube[] c, Vector3I cor, int size)
    {
        int possible = OctaBoxOverlap(cor, size, bo, bs);
        for (int i = 0; i < 8; i++)
        {
            if ((possible & (1 << i)) == 0) continue;
            var o = new Vector3I(cor.X + ((i & 1) >> 0) * size, cor.Y + ((i & 2) >> 1) * size, cor.Z + ((i & 4) >> 2) * size);
            if (c[i].Children != null)
            {
                if (OctaCollide(d, dir, cutoff, bo, bs, c[i].Children, o, size >> 1)) return true;
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
        if (c.IsEntirelySolid() || solid) return FuzzyCollideSolid(d, dir, cutoff, c, co, size);
        return FuzzyCollidePlanes(d, dir, cutoff, c, co, size);
    }

    /// <summary>physics.cpp:777 <c>fuzzycollidesolid</c> (AABB test).</summary>
    private bool FuzzyCollideSolid(PhysEnt d, Vector3 dir, float cutoff, Cube c, Vector3I co, int size)
    {
        float crad = size * 0.5f;
        if (Mathf.Abs(d.O.X - co.X - crad) > d.Radius + crad || Mathf.Abs(d.O.Y - co.Y - crad) > d.Radius + crad ||
            d.O.Z + d.AboveEye < co.Z || d.O.Z - d.EyeHeight > co.Z + size) return false;

        CollideWall = Vector3.Zero;
        float bestdist = -1e10f;
        int visible = c.IsEntirelySolid() ? CubeVisibleMask(c, co, size) : 0xFF;

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
        var p = OctaClipping.GetClipPlanes(c, co, size, CubeVisibleMask(c, co, size));
        if (Mathf.Abs(d.O.X - p.O.X) > p.R.X + d.Radius || Mathf.Abs(d.O.Y - p.O.Y) > p.R.Y + d.Radius ||
            d.O.Z + d.AboveEye < p.O.Z - p.R.Z || d.O.Z - d.EyeHeight > p.O.Z + p.R.Z) return false;

        CollideWall = Vector3.Zero;
        float bestdist = -1e10f;
        int visible = p.Visible;

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
        Vector3 stairdir = (obstacle.Z >= 0 && obstacle.Z < Phys.SlopeZ ? new Vector3(-obstacle.X, -obstacle.Y, 0) : new Vector3(dir.X, dir.Y, 0)).Normalized();
        bool cansmooth = true;
        if (d.PhysState != Phys.PhysStepUp)
        {
            Vector3 checkdir = stairdir * 0.1f;
            checkdir.Z += maxstep + 0.1f;
            d.O += checkdir;
            if (Collide(d, Vector3.Zero))
            {
                d.O = old;
                if (!Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ)) return false;
                cansmooth = false;
            }
        }
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
                    smoothdir = smoothdir / magxy;
                    smoothdir.Z = 1.0f / scale;
                    smoothdir *= dir.Length() / smoothdir.Length();
                }
                else smoothdir.Z = dir.Z;
                d.O += smoothdir;
                d.O = d.O with { Z = d.O.Z + maxstep + 0.1f };
                if (!Collide(d, smoothdir))
                {
                    d.O = d.O with { Z = d.O.Z - (maxstep + 0.1f) };
                    if (d.PhysState == Phys.PhysFall || d.Floor != floor)
                    {
                        d.TimeInAir = 0; d.Floor = floor; SwitchFloor(d, ref dir, d.Floor);
                    }
                    d.PhysState = Phys.PhysStepUp;
                    return true;
                }
            }
        }
        d.O = old;
        d.O = d.O with { Z = d.O.Z + dir.Length() };
        if (!Collide(d, new Vector3(0, 0, 1)))
        {
            if (d.PhysState == Phys.PhysFall || d.Floor != floor)
            {
                d.TimeInAir = 0; d.Floor = floor; SwitchFloor(d, ref dir, d.Floor);
            }
            if (cansmooth) d.PhysState = Phys.PhysStepUp;
            return true;
        }
        d.O = old;
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

    /// <summary>physics.cpp:1340 <c>move</c>.</summary>
    public bool Move(PhysEnt d, ref Vector3 dir)
    {
        Vector3 old = d.O;
        bool collided = false, slidecollide = false;
        Vector3 obstacle = Vector3.Zero;
        d.O += dir;
        if (Collide(d, dir))
        {
            obstacle = CollideWall;
            if (d.Type == Phys.EntPlayer && ((CollideWall.Z >= Phys.SlopeZ && dir.Z < 0) || (CollideWall.Z <= -Phys.SlopeZ && dir.Z > 0)) && (dir.X != 0 || dir.Y != 0) && Collide(d, new Vector3(dir.X, dir.Y, 0)))
            {
                if (CollideWall.Dot(dir) >= 0) slidecollide = true;
                obstacle = CollideWall;
            }
            d.O = old;
            d.O = d.O with { Z = d.O.Z - Phys.StairHeight };
            d.Zmargin = -Phys.StairHeight;
            if (d.PhysState == Phys.PhysSlope || d.PhysState == Phys.PhysFloor ||
                (Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ) && (d.PhysState == Phys.PhysStepUp || d.PhysState == Phys.PhysStepDown || CollideWall.Z >= Phys.FloorZ)))
            {
                d.O = old; d.Zmargin = 0;
                if (TryStepUp(d, ref dir, obstacle, Phys.StairHeight, d.PhysState == Phys.PhysSlope || d.PhysState == Phys.PhysFloor ? d.Floor : CollideWall)) return true;
            }
            else { d.O = old; d.Zmargin = 0; }
            collided = true;
        }
        else if (d.PhysState == Phys.PhysStepUp)
        {
            if (Collide(d, new Vector3(0, 0, -1), Phys.SlopeZ))
            {
                d.O = old;
                if (TryStepUp(d, ref dir, new Vector3(0, 0, 1), Phys.StairHeight, CollideWall)) return true;
                d.O += dir;
            }
        }
        else if (d.PhysState == Phys.PhysStepDown && dir.Dot(d.Floor) <= 1e-6f)
        {
            Vector3 moved = d.O;
            d.O = old;
            if (TryStepDown(d, ref dir)) return true;
            d.O = moved;
        }
        Vector3 floor = Vector3.Zero;
        bool slide = collided;
        bool found = FindFloor(d, collided, obstacle, ref slide, ref floor);
        if (slide || (!collided && floor.Z > 0 && floor.Z < Phys.WallZ))
        {
            SlideAgainst(d, ref dir, slide ? obstacle : floor, found, slidecollide);
            d.Blocked = true;
        }
        if (found) Landing(d, ref dir, floor, collided);
        else Falling(d, ref dir, floor);
        return !collided;
    }

    // ---- moveplayer ------------------------------------------------------

    /// <summary>physics.cpp:1681 <c>moveplayer</c>.</summary>
    public void MovePlayer(PhysEnt pl, int moveres, int curtime, bool water = false, bool floating = false)
    {
        float secs = curtime / 1000f;
        if (!floating) Movement.ModifyGravity(pl, water, curtime);
        Movement.ModifyVelocity(pl, water, floating, curtime);

        Vector3 d = pl.Vel;
        if (!floating && water) d *= 0.5f;
        d += pl.Falling;
        d *= secs;
        pl.Blocked = false;

        if (floating)
        {
            if (pl.PhysState != Phys.PhysFloat) { pl.PhysState = Phys.PhysFloat; pl.TimeInAir = 0; pl.Falling = Vector3.Zero; }
            pl.O += d;
        }
        else
        {
            float f = 1.0f / moveres;
            int collisions = 0;
            d *= f;
            for (int i = 0; i < moveres; i++)
                if (!Move(pl, ref d) && ++collisions < 5) i--; // discrete steps + sliding
        }
    }

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
