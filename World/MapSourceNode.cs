using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Meshing;
using SauerbratenRevival.World.Models;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World;

/// <summary>
/// Shared base ("mixin") for every runtime map provider — <see cref="Ogz.MapWorld"/>,
/// <see cref="Editor.EmptyMap"/> and <see cref="Save.MapLevel"/>. It owns everything
/// that is identical no matter where the map came from:
///
/// <list type="bullet">
///   <item><description>the <see cref="IMapSource"/> handles (octree, sink, renderer, radiance, physics);</description></item>
///   <item><description>the simulation clock and the fixed 5 ms player physics loop;</description></item>
///   <item><description>chunk-renderer construction and geometry bounds;</description></item>
///   <item><description>player registration and editor-controller wiring.</description></item>
/// </list>
///
/// Subclasses only implement <see cref="Build"/> (how the octree and geometry are
/// materialised) and opt into gameplay via <see cref="OnStepped"/> /
/// <see cref="OnAfterSteps"/>.
/// </summary>
public abstract partial class MapSourceNode : Node3D, IMapSource
{
    /// <summary>Chunk edge length passed to the mesher and renderer (power of two).</summary>
    [Export] public int ChunkSize { get; set; } = 512;

    public Node3D WorldNode => this;
    public Octa? Octa { get; set; }
    public WorldCommandSink? Sink { get; set; }
    public WorldChunkRenderer? Renderer { get; set; }

    /// <summary>The radiance hints volume, when the subclass baked one.</summary>
    public Lighting.RadianceVolume? Radiance { get; protected set; }

    /// <summary>The shared octree physics authority for this world, when it simulates one.</summary>
    public OctaPhysics? Physics { get; protected set; }

    public virtual ITextureSource? Textures { get; protected set; }
    public virtual IModelCatalog? Models { get; protected set; }
    public virtual ItemManager? Items { get; protected set; }
    public virtual Game.FlagManager? Flags { get; protected set; }
    public virtual IReadOnlyList<JumpPad> JumpPads { get; protected set; } = Array.Empty<JumpPad>();
    public virtual IReadOnlyList<Teleport> Teleports { get; protected set; } = Array.Empty<Teleport>();

    /// <summary>Active weapon effects renderer, if attached.</summary>
    public Effects.WeaponEffectsRenderer? WeaponEffects { get; set; }

    /// <summary>True once <see cref="Build"/> has produced an <see cref="Octa"/>.</summary>
    public bool Built => Octa != null;

    private SimClock _clock = new();

    /// <summary>
    /// The simulation clock for this world (engine <c>lastmillis</c>). Assigning null
    /// falls back to a fresh clock. Per-world/per-session by design — never a
    /// process-wide autoload — so hosting two sessions keeps independent time bases.
    /// </summary>
    [Export]
    public SimClock Clock
    {
        get => _clock;
        set => _clock = value ?? new SimClock();
    }

    /// <summary>Current simulation time in milliseconds.</summary>
    public int LastMillis => _clock.NowMillis;

    private Aabb? _bounds;

    /// <summary>
    /// AABB of the geometry in Sauerbraten (Z-up, local) space. Defaults to the whole
    /// octree until <see cref="CreateRenderer"/> records the meshed bounds.
    /// </summary>
    public Aabb Bounds
    {
        get => _bounds ?? (Octa != null
            ? new Aabb(Vector3.Zero, new Vector3(Octa.WorldSize, Octa.WorldSize, Octa.WorldSize))
            : default);
        protected set => _bounds = value;
    }

    /// <summary>Centre of the geometry in Godot world space (Y-up), for framing a camera.</summary>
    public Vector3 Centre => IMapSource.ComputeCentre(Bounds);

    /// <summary>Bounding radius of the geometry, for framing a camera.</summary>
    public float Radius => IMapSource.ComputeRadius(Bounds);

    private readonly List<Ogz.MapPlayer> _players = new();

    /// <summary>Active players stepped by this world's physics loop.</summary>
    public IReadOnlyList<Ogz.MapPlayer> Players => _players;

    /// <summary>Mutable view of the registered players for subclass simulation hooks.</summary>
    protected IReadOnlyList<Ogz.MapPlayer> PlayerList => _players;

    /// <summary>Materialise the octree, geometry and entities. Idempotent.</summary>
    public abstract void Build();

    public override void _Ready()
    {
        if (!Built) Build();
    }

    /// <summary>Create the shared octree physics authority for a built octree.</summary>
    protected void InitPhysics(Octa octa) => Physics = new OctaPhysics(octa);

    /// <summary>Create the command sink that applies edits to this octree.</summary>
    protected void InitSink(Octa octa) => Sink = new WorldCommandSink(new OctaWorld(octa));

    /// <summary>Seed a fresh clock from wall time; a session-assigned clock keeps its value.</summary>
    protected void BootstrapClock()
    {
        if (_clock.NowMillis == 0) _clock.Set((int)Time.GetTicksMsec());
    }

    /// <summary>Registers a player to be stepped by the world's physics loop.</summary>
    public virtual void RegisterPlayer(Ogz.MapPlayer player)
    {
        if (_players.Contains(player)) return;
        _players.Add(player);
        player.AttachClock(_clock);
    }

    /// <summary>Unregisters a player from the world's physics loop.</summary>
    public virtual void UnregisterPlayer(Ogz.MapPlayer player)
    {
        if (_players.Remove(player)) player.DetachClock();
    }

    /// <summary>
    /// Build the chunked renderer, record the geometry bounds when the subclass asks
    /// for them, and hand it the materials and pre-built chunk mesh.
    /// </summary>
    protected WorldChunkRenderer CreateRenderer(
        Octa octa, MaterialSet? materials, ChunkedWorldMesh chunks, Material? fallbackMaterial = null)
    {
        if (UseGeometryBounds) Bounds = ComputeBounds(chunks);
        Renderer = new WorldChunkRenderer
        {
            Name = "WorldMesh",
            ChunkSize = ChunkSize,
            BudgetPerFrame = 4,
            Radiance = Radiance,
            FallbackMaterial = fallbackMaterial,
        };
        AddChild(Renderer);
        Renderer.Setup(octa, materials, chunks);
        return Renderer;
    }

    /// <summary>Hand the world to any child editor controller (it draws in this Z-up space).</summary>
    protected void LinkChildEditors()
    {
        foreach (var child in GetChildren())
            if (child is Editor.EditorController ec)
                ec.BindMapSource(this);
    }

    /// <summary>
    /// Whether <see cref="CreateRenderer"/> records the meshed geometry AABB as
    /// <see cref="Bounds"/> (occlusion-accurate, used by <see cref="Ogz.MapWorld"/>) or
    /// leaves the default whole-octree bounds in place.
    /// </summary>
    protected virtual bool UseGeometryBounds => false;

    private static Aabb ComputeBounds(ChunkedWorldMesh chunks)
    {
        Aabb bounds = default;
        bool hasBounds = false;
        foreach (var chunk in chunks.Chunks)
        {
            var aabb = chunk.Mesh.GetAabb();
            aabb.Position += new Vector3(chunk.Origin.X, chunk.Origin.Y, chunk.Origin.Z);
            bounds = hasBounds ? bounds.Merge(aabb) : aabb;
            hasBounds = true;
        }
        return hasBounds ? bounds : new Aabb(Vector3.Zero, Vector3.One);
    }

    private double _physicsAccum;
    private const double StepSeconds = Phys.PhysFrameTime / 1000.0;

    /// <summary>
    /// Fixed 5 ms simulation loop shared by every map source: advance the clock,
    /// gather player input (including mouse look), step the dynent cache and players,
    /// then post-step. Gameplay-specific updates hang off the two hooks.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (Engine.IsEditorHint() || Octa == null || Physics == null) return;

        _physicsAccum += delta;
        int guard = 0;
        while (_physicsAccum >= StepSeconds && guard++ < 100)
        {
            _physicsAccum -= StepSeconds;
            _clock.Advance(Phys.PhysFrameTime);

            // 1. Gather player inputs (including mouse look) before stepping physics.
            for (int i = 0; i < _players.Count; i++) _players[i].PrePhysicsStep();

            // 2. Clear and update the dynent cache once per 5 ms substep.
            Physics.ClearDynentCache();
            foreach (var dynent in Physics.DynEnts) Physics.UpdateDynentCache(dynent);

            // 3. Step registered players.
            for (int i = 0; i < _players.Count; i++) _players[i].Step(Phys.PhysFrameTime);

            // 4. Gameplay-specific per-substep updates (projectiles, flags, items).
            OnStepped(Phys.PhysFrameTime);

            // 5. Post physics step for players (attack cooldowns).
            for (int i = 0; i < _players.Count; i++) _players[i].PostPhysicsStep();
        }

        // Per-frame work after the fixed substeps (e.g. visual sync).
        OnAfterSteps();
    }

    /// <summary>Per-substep gameplay updates. Default is a no-op (pure editor worlds).</summary>
    protected virtual void OnStepped(int timeMillis) { }

    /// <summary>Per-frame work after the fixed substeps. Default is a no-op.</summary>
    protected virtual void OnAfterSteps() { }
}
