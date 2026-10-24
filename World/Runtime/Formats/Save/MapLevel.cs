using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Effects;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Ctf;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Game.Monsters;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Lighting;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Meshing;
using SauerbratenRevival.World.Models;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Runtime;
using SauerbratenRevival.World.Weapons;
using SimClock = SauerbratenRevival.World.Runtime.SimClock;

namespace SauerbratenRevival.World.Runtime.Formats.Save;

/// <summary>
/// Scene-level root for a native-format map (docs/editor-and-commands.md §7.1). It
/// holds only references to the data (<c>world.res</c>, <c>history.res</c>) — the
/// octree and command history live outside the scene, so editing geometry never
/// rewrites <c>Level.tscn</c>.
///
/// On <c>_Ready</c> it materializes the octree from <see cref="World"/> and replays
/// <see cref="History"/> through the command sink, then meshes it. Entities are
/// runtime projections rebuilt from the snapshot's entity list (§7.2).
///
/// The octree is Sauerbraten Z-up; like <see cref="Ogz.MapWorld"/> this node rotates
/// −90° about X so it sits in Godot Y-up space.
/// </summary>
[GlobalClass]
public partial class MapLevel : Node3D, IGameWorld
{
    /// <summary>External reference to the base octree snapshot (<c>world.res</c>).</summary>
    [Export] public WorldResource? World { get; set; }

    /// <summary>External reference to the command history (<c>history.res</c>); may be null.</summary>
    [Export] public HistoryResource? History { get; set; }

    /// <summary>Chunk size for the mesher.</summary>
    [Export] public int ChunkSize { get; set; } = 512;

    public Node3D WorldNode => this;
    public Materials.ITextureSource? Textures => _textures;
    public Models.IModelCatalog? Models { get; private set; }
    public ItemManager? Items { get; private set; }
    public FlagManager? Flags => null;
    public IReadOnlyList<Entities.JumpPad> JumpPads { get; private set; } = System.Array.Empty<Entities.JumpPad>();
    public IReadOnlyList<Entities.Teleport> Teleports { get; private set; } = System.Array.Empty<Entities.Teleport>();
    public Effects.WeaponEffectsRenderer? WeaponEffects { get; set; }
    public SimClock? Clock => _clock;
    public bool Built => Octa != null;

    /// <summary>Active flying projectiles (rockets) stepped centrally by this world's physics loop.</summary>
    public List<RocketProjectile> ActiveRockets { get; } = new();

    /// <summary>Active bouncers (grenades, debris, gibs) stepped centrally by this world's physics loop.</summary>
    public List<BounceBody> ActiveBouncers { get; } = new();

    /// <summary>Backing read-only list for active bouncers.</summary>
    public IReadOnlyList<BounceBody> Bouncers => ActiveBouncers;

    private readonly List<MapPlayer> _players = new();
    public IReadOnlyList<MapPlayer> Players => _players;

    public MapPlayer? LocalPlayer { get; set; }
    public Game.GameModeInfo? Mode { get; set; }

    /// <summary>The shared octree physics simulation authority for this world.</summary>
    public OctaPhysics? Physics { get; private set; }

    private SimClock _clock = new();

    public void RegisterPlayer(MapPlayer player)
    {
        if (!_players.Contains(player))
        {
            if (Mode != null) player.Mode = Mode;
            _players.Add(player);
            player.AttachClock(_clock);
        }
    }

    public void UnregisterPlayer(MapPlayer player)
    {
        if (_players.Remove(player)) player.DetachClock();
        if (ReferenceEquals(LocalPlayer, player)) LocalPlayer = null;
    }

    public void RegisterBouncer(BounceBody bouncer)
    {
        if (!ActiveBouncers.Contains(bouncer))
            ActiveBouncers.Add(bouncer);
    }

    public void UnregisterBouncer(BounceBody bouncer)
    {
        ActiveBouncers.Remove(bouncer);
    }

    private Materials.ITextureSource? _textures;

    public Octa? Octa { get; set; }
    public WorldCommandSink? Sink { get; set; }
    public WorldChunkRenderer? Renderer { get; set; }
    public Lighting.RadianceVolume? Radiance { get; set; }

    /// <summary>The loaded snapshot (octree + entities).</summary>
    public WorldSnapshot? Snapshot { get; private set; }

    /// <summary>
    /// Root of the reference dump's <c>packages/</c> tree, used to resolve the
    /// manifest's full texture paths when the project Resources do not have them.
    /// Empty disables the dump fallback (project Resources only).
    /// </summary>
    [Export] public string ReferenceRoot { get; set; } = "";

    /// <summary>The map directory (os or res:// path) the manifest is loaded from.</summary>
    [Export] public string MapDir { get; set; } = "";

    /// <summary>Entities loaded from the snapshot (view of the command log; §7.2).</summary>
    public IReadOnlyList<MapEntity> Entities => Snapshot?.Entities ?? (IReadOnlyList<MapEntity>)System.Array.Empty<MapEntity>();

    public override void _Ready() => Build();

    /// <summary>Materialize the octree and history, then mesh. Idempotent.</summary>
    public void Build()
    {
        if (Octa != null || World == null) return;

        RotationDegrees = new Vector3(-90, 0, 0); // Z-up -> Y-up

        Snapshot = World.ToSnapshot();
        var octa = Snapshot.ToOcta();
        Octa = octa;
        Physics = new OctaPhysics(octa);
        if (_clock.NowMillis == 0) _clock.Set((int)Time.GetTicksMsec());
        Sink = new WorldCommandSink(new OctaWorld(octa));

        // Replay the saved history tail on top of the base snapshot (§4 strategy 2).
        if (History != null)
            foreach (var cmd in History.ToCommands()) Sink.Receive(cmd);

        Renderer = new WorldChunkRenderer
        {
            Name = "WorldMesh",
            ChunkSize = ChunkSize,
            BudgetPerFrame = 4,
            Radiance = Radiance,
        };
        AddChild(Renderer);

        // Resolve the manifest (if any) into a material set so the map renders
        // textured; editor maps without an imported manifest mesh untextured.
        MaterialSet? materials = null;
        string mapDir = !string.IsNullOrEmpty(MapDir) ? MapDir
            : World?.ResourcePath is { Length: > 0 } p ? Path.GetDirectoryName(p) ?? "" : "";
        if (mapDir.Length > 0)
        {
            var manifest = MapStore.LoadManifest(mapDir);
            if (manifest != null)
            {
                var textures = BuildTextureSource();
                _textures = textures;
                materials = ManifestBuilder.ToMaterialSet(manifest, textures);
            }
        }
        Renderer.Setup(octa, materials);

        // Converted mapmodels (assimp), imported by Godot as Resources.
        var modelCatalog = ModelCatalog.LoadFromProject();
        Models = modelCatalog;

        AddChild(LightBuilder.Build(Entities));
        AddChild(EnvMapBuilder.Build(Entities));
        AddChild(PlayerStartBuilder.Build(Entities, _textures));
        AddChild(ModeEntityBuilder.Build(Entities, null, modelCatalog, _textures, Mode));
        AddChild(MonsterBuilder.Build(Entities, modelCatalog, _textures));
        var (triggers, jumpPads, teleports) = TriggerBuilder.BuildTriggers(Entities, null, modelCatalog, _textures);
        JumpPads = jumpPads;
        Teleports = teleports;
        AddChild(triggers);
        Items = ItemBuilder.Build(Entities, modelCatalog, _textures);
        Items.AutoAdvance = false;
        AddChild(Items);
        AddChild(ParticleBuilder.Build(Entities, _textures));
        AddChild(SoundBuilder.Build(Entities, null));
        var mapModels = MapModelBuilder.Build(Entities, null, modelCatalog, _textures);
        AddChild(mapModels);

        // Hand the world to any child editor controller (same contract as EmptyMap).
        foreach (var child in GetChildren())
            if (child is Editor.EditorController ec)
                ec.BindMapSource(this);

        GD.Print($"[maplevel] worldsize={octa.WorldSize} chunks={Renderer.ChunkCount} tris={Renderer.TriangleCount} entities={Entities.Count}");
    }

    private readonly WorldSimulation _simulation = new();

    public override void _PhysicsProcess(double delta)
    {
        _simulation.Step(this, delta);
    }

    /// <summary>Finds a player spawn point in Sauerbraten coordinates.</summary>
    public (Vector3 Position, float Yaw) FindSpawn(int index = 0) =>
        IMapSource.ResolveSpawn(
            Entities.Where(e => e.Type == (byte)SauerbratenRevival.World.Entities.EntityType.PlayerStart).Select(e => (e.Position, (float)e.Attr1)),
            ((IMapSource)this).Bounds.GetCenter(), index);

    /// <summary>Project Resources first, then the reference dump (when configured).</summary>
    private ITextureSource BuildTextureSource()
    {
        var primary = new ResourceTextureSource();
        if (string.IsNullOrEmpty(ReferenceRoot)) return primary;
        return new FallbackTextureSource(primary, new DumpTextureSource(ReferenceRoot));
    }
}
