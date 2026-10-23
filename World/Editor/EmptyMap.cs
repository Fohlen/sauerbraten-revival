using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Meshing;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Editor;

/// <summary>
/// A tiny, self-contained editable world for the editor prototype: a hollow room
/// carved out of a solid block, meshed with the chunked mesher and exposed for the
/// <see cref="EditorController"/>. No OGZ, no metadata — it exists so the cursor and
/// edits can be exercised immediately.
///
/// The octree is Sauerbraten Z-up; this node rotates −90° about X like
/// <see cref="Ogz.MapWorld"/>, so it sits in the same space as the editor controller.
/// </summary>
[Tool]
[GlobalClass]
public partial class EmptyMap : Node3D, IMapSource
{
    /// <summary>Octree world scale (worldsize = 1 &lt;&lt; WorldScale). Clamped to 10..16 by the NewMap op.</summary>
    [Export] public int WorldScale { get; set; } = 10; // worldsize 1024; SplitOcta is covered by tests

    /// <summary>Chunk size for the mesher.</summary>
    [Export] public int ChunkSize { get; set; } = 128;

    public Node3D WorldNode => this;
    public Materials.ITextureSource? Textures => null;
    public Entities.IModelCatalog? Models => null;
    public Entities.ItemManager? Items => null;
    public Game.FlagManager? Flags => null;
    public IReadOnlyList<Entities.JumpPad> JumpPads => System.Array.Empty<Entities.JumpPad>();
    public IReadOnlyList<Entities.Teleport> Teleports => System.Array.Empty<Entities.Teleport>();
    public Effects.WeaponEffectsRenderer? WeaponEffects { get; set; }
    public SimClock? Clock => _clock;
    public bool Built => Octa != null;

    public Octa? Octa { get; set; }
    public WorldCommandSink? Sink { get; set; }
    public WorldChunkRenderer? Renderer { get; set; }
    public Lighting.RadianceVolume? Radiance { get; set; }

    /// <summary>The shared octree physics authority for this world.</summary>
    public OctaPhysics? Physics { get; private set; }

    private readonly SimClock _clock = new();
    private readonly List<Ogz.MapPlayer> _players = new();

    /// <summary>Active players stepped by this world's physics loop.</summary>
    public IReadOnlyList<Ogz.MapPlayer> Players => _players;

    /// <summary>Registers a player to be stepped by the world's physics loop.</summary>
    public void RegisterPlayer(Ogz.MapPlayer player)
    {
        if (!_players.Contains(player))
        {
            _players.Add(player);
            player.AttachClock(_clock);
        }
    }

    /// <summary>Unregisters a player from the world's physics loop.</summary>
    public void UnregisterPlayer(Ogz.MapPlayer player)
    {
        if (_players.Remove(player)) player.DetachClock();
    }

    /// <summary>
    /// Reuse a built-once canonical world (octree + meshes) across instances by
    /// cloning the octree and sharing the meshes, instead of rebuilding and
    /// re-meshing from scratch. Every <see cref="EmptyMap"/> builds the same world,
    /// so the seed is deterministic; the clone keeps mutations instance-local. This
    /// matters in tests, where dozens of fresh maps otherwise pay the full build +
    /// mesh cost each. Meshes are shared read-only (Godot resources), which is
    /// safe because the seed meshes are never mutated.
    /// </summary>
    /// <summary>
    /// Per-instance override to use the seed cache. If null, falls back to <see cref="UseSeedCache"/>.
    /// </summary>
    public bool? EnableSeedCache { get; set; }

    /// <summary>
    /// Process-wide default toggle for caching the canonical hollow room seed.
    /// Can be overridden per-instance with <see cref="EnableSeedCache"/>.
    /// </summary>
    public static bool UseSeedCache { get; set; }

    private bool ShouldUseSeedCache => EnableSeedCache ?? UseSeedCache;

    private static readonly Dictionary<(int Scale, int Chunk), (Octa Seed, ChunkedWorldMesh Mesh)> SeedCache = new();

    public override void _Ready() => Build();

    private double _physicsAccum;
    private const double StepSeconds = Phys.PhysFrameTime / 1000.0;

    /// <summary>
    /// Steps registered players on the same fixed 5 ms cadence as
    /// <see cref="Ogz.MapWorld"/>. The empty map has no game entities, projectiles
    /// or game mode, so this is the player simulation only.
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

            // 4. Post physics step for players (attack cooldowns).
            for (int i = 0; i < _players.Count; i++) _players[i].PostPhysicsStep();
        }
    }

    public void Build()
    {
        if (Built) return;

        RotationDegrees = new Vector3(-90, 0, 0); // Z-up -> Y-up

        if (ShouldUseSeedCache && SeedCache.TryGetValue((WorldScale, ChunkSize), out var seed))
        {
            // Fast path: clone the cached octree and reuse its meshes.
            var cachedOcta = seed.Seed.Clone();
            Octa = cachedOcta;
            Sink = new WorldCommandSink(new OctaWorld(cachedOcta));
            Physics = new OctaPhysics(cachedOcta);
            CreateRenderer(cachedOcta, seed.Mesh);
            LinkChildEditors();
            GD.Print($"[emptymap] seed-cache worldsize={cachedOcta.WorldSize} chunks={Renderer!.ChunkCount} tris={Renderer.TriangleCount}");
            return;
        }

        // Build path: construct the octree, carve the room, mesh it, and (when the
        // seed cache is on) remember it for later instances.
        var octa = new Octa(WorldScale);
        Octa = octa;
        Sink = new WorldCommandSink(new OctaWorld(octa));
        Sink.Submit(new WorldCommand(WorldOp.NewMap, new NewMapPayload(WorldScale),
            source: CommandSource.Editor));
        Physics = new OctaPhysics(octa);

        // Carve a hollow room so the prototype has interior space to fly around.
        int size = octa.WorldSize;
        int inner = size / 2;
        var sel = new Block3(new Vector3I(inner / 2, inner / 2, inner / 2),
                             new Vector3I(inner / 8, inner / 8, inner / 8), 8, 0);
        OctaEdit.DeleteCube(octa, sel);

        ChunkedWorldMesh? prebuilt = null;
        if (ShouldUseSeedCache)
        {
            var mesher = new OctaMesher(octa, null);
            var mesh = mesher.BuildChunked(ChunkSize);
            SeedCache[(WorldScale, ChunkSize)] = (octa.Clone(), mesh);
            prebuilt = mesh;
        }

        CreateRenderer(octa, prebuilt);
        LinkChildEditors();

        GD.Print($"[emptymap] worldsize={size} chunks={Renderer!.ChunkCount} tris={Renderer.TriangleCount}");
    }


    private void CreateRenderer(Octa octa, ChunkedWorldMesh? prebuilt)
    {
        Renderer = new WorldChunkRenderer
        {
            Name = "WorldMesh",
            ChunkSize = ChunkSize,
            BudgetPerFrame = 4,
            Radiance = Radiance,
        };
        AddChild(Renderer);
        Renderer.Setup(octa, materials: null, prebuilt);
    }

    // Hand the world to any child editor controller (it draws in this Z-up space).
    private void LinkChildEditors()
    {
        foreach (var child in GetChildren())
            if (child is EditorController ec)
                ec.BindMapSource(this);
    }

}
