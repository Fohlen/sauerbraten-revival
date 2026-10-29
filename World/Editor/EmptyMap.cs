using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Ctf;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Meshing;
using SauerbratenRevival.World.Models;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Runtime;
using SimClock = SauerbratenRevival.World.Runtime.SimClock;

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
public partial class EmptyMap : Node3D, IGameWorld
{
    /// <summary>Octree world scale (worldsize = 1 &lt;&lt; WorldScale). Clamped to 10..16 by the NewMap op.</summary>
    [Export] public int WorldScale { get; set; } = 10; // worldsize 1024; SplitOcta is covered by tests

    /// <summary>Chunk size for the mesher.</summary>
    [Export] public int ChunkSize { get; set; } = 128;

    public Node3D WorldNode => this;
    /// <summary>The texture source used while building (player/hudgun skins).</summary>
    public Materials.ITextureSource? Textures { get; private set; }

    /// <summary>The converted-model catalogue, if present (player/hudgun models).</summary>
    public Models.IModelCatalog? Models { get; private set; }
    public ItemManager? Items => null;
    public FlagManager? Flags => null;
    public IReadOnlyList<Entities.JumpPad> JumpPads => System.Array.Empty<Entities.JumpPad>();
    public IReadOnlyList<Entities.Teleport> Teleports => System.Array.Empty<Entities.Teleport>();
    public Effects.WeaponEffectsRenderer? WeaponEffects { get; set; }
    public SimClock? Clock => _clock;
    public bool Built => Octa != null;

    /// <summary>Active flying projectiles (rockets) stepped centrally by this world's physics loop.</summary>
    public List<Weapons.RocketProjectile> ActiveRockets { get; } = new();

    /// <summary>Active bouncers (grenades, debris, gibs) stepped centrally by this world's physics loop.</summary>
    public List<Weapons.BounceBody> ActiveBouncers { get; } = new();

    /// <summary>Backing read-only list for active bouncers.</summary>
    public IReadOnlyList<Weapons.BounceBody> Bouncers => ActiveBouncers;

    public MapPlayer? LocalPlayer { get; set; }
    public Game.GameModeInfo? Mode { get; set; }

    public void RegisterBouncer(Weapons.BounceBody bouncer)
    {
        if (!ActiveBouncers.Contains(bouncer))
            ActiveBouncers.Add(bouncer);
    }

    public void UnregisterBouncer(Weapons.BounceBody bouncer)
    {
        ActiveBouncers.Remove(bouncer);
    }

    public Octa? Octa { get; set; }
    public CommandSink? Sink { get; set; }
    public WorldChunkRenderer? Renderer { get; set; }
    public Lighting.RadianceVolume? Radiance { get; set; }

    /// <summary>Optional quality settings for the baked radiance hints volume; null uses the defaults (32³, RadianceHints).</summary>
    public Lighting.LightingSettings? RadianceSettings { get; set; }

    /// <summary>
    /// Bake the radiance hints volume (sky + sun + ambient) during <see cref="Build"/>
    /// and shade the room with it. Off by default so tests and the editor viewport
    /// stay fast; <c>scenes/empty_map.tscn</c> turns it on so the synthetic room has
    /// a sun to exercise the lighting algorithm against.
    /// </summary>
    [Export] public bool BakeRadiance { get; set; }

    /// <summary>Sunlight colour as packed 0xRRGGBB (engine <c>sunlight</c>); 0 disables the sun.</summary>
    [Export] public int Sunlight { get; set; } = 0xE8E8E8;
    [Export] public float SunlightScale { get; set; } = 1.0f;
    [Export] public int SunlightYaw { get; set; } = 315;
    [Export] public int SunlightPitch { get; set; } = 45;

    /// <summary>Ambient colour as packed 0xRRGGBB (engine <c>ambient</c>).</summary>
    [Export] public int Ambient { get; set; } = 0x191919;

    /// <summary>Sky tint colour as packed 0xRRGGBB (engine <c>skyboxcolour</c>).</summary>
    [Export] public int SkyboxColour { get; set; } = 0xFFFFFF;

    /// <summary>The shared octree physics authority for this world.</summary>
    public OctaPhysics? Physics { get; private set; }

    private readonly SimClock _clock = new();
    private readonly List<MapPlayer> _players = new();

    /// <summary>Active players stepped by this world's physics loop.</summary>
    public IReadOnlyList<MapPlayer> Players => _players;

    /// <summary>Registers a player to be stepped by the world's physics loop.</summary>
    public void RegisterPlayer(MapPlayer player)
    {
        if (!_players.Contains(player))
        {
            _players.Add(player);
            player.AttachClock(_clock);
        }
    }

    /// <summary>Unregisters a player from the world's physics loop.</summary>
    public void UnregisterPlayer(MapPlayer player)
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

    private readonly WorldSimulation _simulation = new();

    public override void _PhysicsProcess(double delta)
    {
        _simulation.Step(this, delta);
    }

    public void Build()
    {
        if (Built) return;

        RotationDegrees = new Vector3(-90, 0, 0); // Z-up -> Y-up
        Scale = new Vector3(-1, 1, 1);

        // The room itself is untextured, but player/hudgun models and their skins
        // still come from the project's imported content (same sources MapWorld
        // uses), so the editor scene shows them instead of warning about a missing
        // catalogue.
        Models = ModelCatalog.LoadFromProject();
        Textures ??= Materials.TextureSources.CreateDefault();

        if (!BakeRadiance && ShouldUseSeedCache && SeedCache.TryGetValue((WorldScale, ChunkSize), out var seed))
        {
            // Fast path: clone the cached octree and reuse its meshes.
            var cachedOcta = seed.Seed.Clone();
            Octa = cachedOcta;
            Sink = new CommandSink(cachedOcta);
            Physics = new OctaPhysics(cachedOcta);
            CreateRenderer(cachedOcta, seed.Mesh, materials: null);
            LinkChildEditors();
            GD.Print($"[emptymap] seed-cache worldsize={cachedOcta.WorldSize} chunks={Renderer!.ChunkCount} tris={Renderer.TriangleCount}");
            return;
        }

        // Build path: construct the octree, carve the room, mesh it, and (when the
        // seed cache is on) remember it for later instances.
        var octa = new Octa(WorldScale);
        Octa = octa;
        Sink = new CommandSink(octa);
        Sink.Submit(Command.CreateNewMap(WorldScale));
        Physics = new OctaPhysics(octa);

        // Carve a hollow room so the prototype has interior space to fly around.
        int size = octa.WorldSize;
        int inner = size / 2;
        var sel = new Block3(new Vector3I(inner / 2, inner / 2, inner / 2),
                             new Vector3I(inner / 8, inner / 8, inner / 8), 8, 0);
        OctaEdit.DeleteCube(octa, sel);

        // Lighting: bake the room through the same radiance hints pipeline as a real
        // map so a scene can exercise the sky/sun bake and incremental rebake.
        Materials.MaterialSet? materials = null;
        if (BakeRadiance)
        {
            var config = new Materials.MapConfig
            {
                Ambient = Ambient,
                Sunlight = Sunlight,
                SunlightScale = SunlightScale,
                SunlightYaw = SunlightYaw,
                SunlightPitch = SunlightPitch,
                SkyboxColour = SkyboxColour,
            };
            var volume = Lighting.RadianceVolume.CreateForOcta(octa, RadianceSettings);
            volume.Bake(octa, config);
            Radiance = volume;
            materials = BuildRadianceMaterials(volume, config);
        }

        ChunkedWorldMesh? prebuilt = null;
        if (ShouldUseSeedCache && !BakeRadiance)
        {
            var mesher = new OctaMesher(octa, null);
            var mesh = mesher.BuildChunked(ChunkSize);
            SeedCache[(WorldScale, ChunkSize)] = (octa.Clone(), mesh);
            prebuilt = mesh;
        }

        CreateRenderer(octa, prebuilt, materials);
        LinkChildEditors();

        GD.Print($"[emptymap] worldsize={size} chunks={Renderer!.ChunkCount} tris={Renderer.TriangleCount}");
    }


    private void CreateRenderer(Octa octa, ChunkedWorldMesh? prebuilt, Materials.MaterialSet? materials)
    {
        Renderer = new WorldChunkRenderer
        {
            Name = "WorldMesh",
            ChunkSize = ChunkSize,
            BudgetPerFrame = 4,
            Radiance = Radiance,
        };
        AddChild(Renderer);
        Renderer.Setup(octa, materials, prebuilt);
    }

    /// <summary>
    /// Build the single default-geometry material used by the room, shading it with
    /// the baked radiance volume. The room has no textures, so the albedo texture is
    /// left unset and the shader falls back to the tint/vertex colour.
    /// </summary>
    private static Materials.MaterialSet BuildRadianceMaterials(
        Lighting.RadianceVolume volume, Materials.MapConfig config)
    {
        var set = new Materials.MaterialSet();
        var vs = new Materials.CatalogueVSlot { Index = Octa.DEFAULT_GEOM, Slot = Octa.DEFAULT_GEOM };
        var material = Materials.MaterialBuilder.BuildRadianceMaterial(
            albedo: null,
            tint: Colors.White,
            radianceTex: volume.Texture,
            gridOrigin: volume.Grid.Origin,
            gridExtent: volume.Grid.Extent,
            skyTint: Lighting.RadianceVolume.DecodeColor(config.SkyboxColour),
            ambientColor: Lighting.RadianceVolume.DecodeColor(config.Ambient),
            useRadiance: volume.Settings.Mode == Lighting.LightingQualityMode.RadianceHints,
            radianceAmbTex: volume.AmbientTexture);

        var uv = new Materials.TexGen[3];
        var texSize = new Vector2I(64, 64);
        for (int dim = 0; dim < 3; dim++) uv[dim] = Materials.MaterialBuilder.CalcTexGen(vs, dim, texSize);
        set.Add(Octa.DEFAULT_GEOM, material, uv);
        return set;
    }

    // Hand the world to any child editor controller (it draws in this Z-up space).
    private void LinkChildEditors()
    {
        foreach (var child in GetChildren())
            if (child is EditorController ec)
                ec.BindMapSource(this);
    }

}
