using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// A runtime <c>ENT_BOUNCE</c> body (grenades, debris, gibs — fpsgame/weapon.cpp
/// <c>bouncer</c>) driven by the faithfully-ported <see cref="OctaPhysics.Bounce"/>.
/// This is the **wiring** of the ported bounce physics into the scene: each fixed
/// 5 ms step it advances the entity against the world octree and the dynent cache
/// (players/movables), syncing the node transform.
///
/// It simulates in Sauerbraten coordinates, so it lives under the Z-up world node
/// (like <see cref="Ogz.MapPlayer"/>). The bouncer dimensions follow
/// <c>newbouncer</c>: radius 1.5 (0.5 for debris) with <c>eyeheight = aboveeye =
/// radius</c>; a grenade uses <c>COLLIDE_ELLIPSE_PRECISE</c>.
/// </summary>
[GlobalClass]
public partial class BounceBody : Node3D
{
    /// <summary>fpsgame/weapon.cpp <c>bouncer</c> kinds (drives size/elasticity).</summary>
    public enum Kind { Grenade, Debris, Gibs }

    [Export] public Kind BodyKind { get; set; } = Kind.Grenade;

    /// <summary>Facing yaw (degrees) — only affects the ellipse collision shape.</summary>
    [Export] public float Yaw { get; set; }

    /// <summary>The simulated entity (Sauerbraten Z-up). Read by tests/diagnostics.</summary>
    public PhysEnt Entity { get; } = new();

    /// <summary>Number of surface reflections so far (<c>bouncer::bounces</c>).</summary>
    public int Bounces { get; private set; }

    /// <summary>Set when a bounce hit a dynent (player/movable).</summary>
    public PhysEnt? LastPlayerHit { get; private set; }

    /// <summary>Raised on each surface reflection with the surface normal.</summary>
    public event System.Action<PhysEnt, Vector3>? Reflected;

    /// <summary>Remaining time to live in milliseconds (fuse for grenades).</summary>
    public int LifeTime { get; set; } = 1500;

    /// <summary>The owner who fired the projectile/bouncer.</summary>
    public IExplosionTarget? ShooterOwner { get; set; }

    /// <summary>Weapon info for grenade damage/radius.</summary>
    public Weapons.WeaponInfo? GunInfo { get; set; }

    public bool QuadDamage { get; set; }
    public bool Local { get; set; } = true;
    public bool Exploded { get; private set; }

    /// <summary>Raised when a grenade detonates (timeout or player contact).</summary>
    public event System.Action<Vector3, Weapons.ExplosionResult<IExplosionTarget>>? Detonated;

    private OctaPhysics? _phys;
    private double _accum;
    private const double StepSeconds = Phys.PhysFrameTime / 1000.0;

    public bool Initialized { get; private set; }

    // The engine's game::bounced / game::dynentcollide are single global callbacks
    // that receive the entity, so they route by identity through a registry here
    // (the engine handler dispatches on d->type/bouncetype; we dispatch on instance).
    private static readonly Dictionary<PhysEnt, BounceBody> Bodies = new();
    private static bool _hooked;

    /// <summary>
    /// Initialise against a world. <paramref name="position"/>, <paramref name="velocity"/>
    /// and <paramref name="yawDegrees"/> are Sauerbraten units/degrees.
    /// </summary>
    public void Setup(
        OctaPhysics phys,
        Vector3 position,
        Vector3 velocity,
        float yawDegrees = 0f,
        IExplosionTarget? owner = null,
        Weapons.WeaponInfo? gun = null,
        bool quadDamage = false,
        int lifeTime = 1500,
        bool local = true)
    {
        if (Initialized) _phys?.DynEnts.Remove(Entity);
        _phys = phys;
        ShooterOwner = owner;
        GunInfo = gun ?? (BodyKind == Kind.Grenade ? WeaponCatalog.Get(Gun.GrenadeLauncher) : null);
        QuadDamage = quadDamage;
        LifeTime = lifeTime;
        Local = local;
        Exploded = false;

        ConfigureEntity(position, velocity, yawDegrees);

        // avoidcollision(&bnc, dir, owner, 0.1f);
        if (owner != null)
        {
            Vector3 dir = velocity.Normalized();
            if (dir.LengthSquared() > 1e-6f)
            {
                OctaPhysics.AvoidCollision(Entity, dir, owner.CollisionEntity, 0.1f);
            }
        }

        phys.DynEnts.Add(Entity);
        Bodies[Entity] = this;
        Hook();

        if (GetNodeOrNull<MeshInstance3D>("Visual") == null)
        {
            var sphere = new SphereMesh { Radius = 0.5f, Height = 1.0f };
            var mat = new StandardMaterial3D
            {
                AlbedoColor = BodyKind == Kind.Grenade ? new Color(0.2f, 0.4f, 0.2f) : new Color(0.5f, 0.3f, 0.1f),
                Roughness = 0.3f
            };
            var mi = new MeshInstance3D { Name = "Visual", Mesh = sphere, MaterialOverride = mat };
            AddChild(mi);
        }

        Initialized = true;
        SyncTransform();
    }

    public override void _ExitTree()
    {
        _phys?.DynEnts.Remove(Entity);
        Bodies.Remove(Entity);
        if (Bodies.Count == 0) Unhook();
    }

    /// <summary>newbouncer: set the collision volume for the kind.</summary>
    private void ConfigureEntity(Vector3 position, Vector3 velocity, float yawDegrees)
    {
        float radius = BodyKind == Kind.Debris ? 0.5f : 1.5f;
        Entity.Type = Phys.EntBounce;
        Entity.O = position;
        Entity.NewPos = position;
        Entity.Vel = velocity;
        Entity.Yaw = yawDegrees;
        Entity.Radius = Entity.XRadius = Entity.YRadius = radius;
        Entity.EyeHeight = Entity.AboveEye = radius;
        Entity.PhysState = Phys.PhysBounce;
        Entity.State = PhysEnt.CsAlive;
        // A grenade uses the precise ellipse form; debris/gibs the default.
        Entity.CollideType = BodyKind == Kind.Grenade ? Phys.CollideEllipsePrecise : Phys.CollideEllipse;
    }

    /// <summary>
    /// fpsgame/weapon.cpp <c>updatebouncers</c> per-bouncetype constants: grenade is
    /// the driver form (elasticity 0.6, waterfric 0.5, grav 0.8); the rest use the
    /// per-substep form (elasticity 0.6, waterfric 0.5, grav 1).
    /// </summary>
    private float Elasticity => 0.6f;
    private float WaterFric => 0.5f;
    private float Grav => BodyKind == Kind.Grenade ? 0.8f : 1.0f;

    public override void _PhysicsProcess(double delta)
    {
        if (!Initialized || _phys == null) return;
        Advance(delta);
    }

    /// <summary>
    /// Advance by <paramref name="delta"/> seconds, running whole fixed 5 ms steps
    /// caught up by count (the engine's <c>physsteps</c>). Each step is one
    /// <see cref="OctaPhysics.Bounce(PhysEnt, float, float, float, float)"/> substep.
    /// In weapon.cpp:243, a grenade stops and detonates when bounce returns true (hit live player)
    /// or when lifetime expires (<c>(bnc.lifetime -= time) &lt; 0</c>).
    /// </summary>
    public void Advance(double delta, IReadOnlyList<IExplosionTarget>? candidates = null)
    {
        if (!Initialized || _phys == null || Exploded) return;
        _accum += delta;
        int guard = 0;
        while (_accum >= StepSeconds && guard++ < 100)
        {
            // Populate the dynent cache for this frame so plcollide sees everyone
            // (physics.cpp physicsframe() -> cleardynentcache()).
            _phys.ClearDynentCache();
            foreach (var dynent in _phys.DynEnts) _phys.UpdateDynentCache(dynent);

            bool hitPlayer = _phys.Bounce(Entity, Elasticity, WaterFric, Grav, 1, Phys.PhysFrameTime);
            _accum -= StepSeconds;

            if (BodyKind == Kind.Grenade)
            {
                LifeTime -= Phys.PhysFrameTime;
                bool stopped = hitPlayer || LifeTime < 0;
                if (stopped)
                {
                    Detonate(candidates);
                    break;
                }
            }
        }
        SyncTransform();
    }

    private void Detonate(IReadOnlyList<IExplosionTarget>? candidates)
    {
        Exploded = true;
        var gun = GunInfo ?? WeaponCatalog.Get(Gun.GrenadeLauncher);
        if (candidates == null || candidates.Count == 0)
        {
            candidates = ShooterOwner != null ? new[] { ShooterOwner } : System.Array.Empty<IExplosionTarget>();
        }
        else if (ShooterOwner != null)
        {
            bool found = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i] == ShooterOwner) { found = true; break; }
            }
            if (!found)
            {
                var list = new List<IExplosionTarget>(candidates) { ShooterOwner };
                candidates = list;
            }
        }

        var result = WeaponExplosion.ResolveExplosion(
            Entity.O,
            gun,
            QuadDamage,
            shooter: ShooterOwner,
            candidates: candidates);

        for (int i = 0; i < result.Hits.Count; i++)
        {
            var hit = result.Hits[i];
            hit.Target.ApplyHitPush(hit.PushVel);
            hit.Target.TakeDamage(hit.Damage);
        }

        Detonated?.Invoke(Entity.O, result);

        if (IsInsideTree())
        {
            QueueFree();
        }
    }

    private void OnReflected(PhysEnt d, Vector3 surface)
    {
        Bounces++;
        Reflected?.Invoke(d, surface);
    }

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        OctaPhysics.Bounced = DispatchBounced;
        OctaPhysics.DynEntCollide = DispatchDynEntCollide;
    }

    private static void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        OctaPhysics.Bounced = null;
        OctaPhysics.DynEntCollide = null;
    }

    private static void DispatchBounced(PhysEnt d, Vector3 surface)
    {
        if (Bodies.TryGetValue(d, out var body)) body.OnReflected(d, surface);
    }

    private static void DispatchDynEntCollide(PhysEnt d, PhysEnt other, Vector3 normal)
    {
        if (Bodies.TryGetValue(d, out var body)) body.LastPlayerHit = other;
    }

    private void SyncTransform()
    {
        Position = Entity.O;
        Vector3 forward = Movement.VecFromYawPitch(Entity.Yaw, 0f, 1, 0);
        if (forward.LengthSquared() > 1e-6f)
            Basis = Basis.LookingAt(forward, new Vector3(0, 0, 1));
    }
}
