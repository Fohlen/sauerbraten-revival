using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SauerbratenRevival.Format;
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
/// Scene-level root for a native-format map (docs/editor-and-commands.md §7.1, ADR 0008, ADR 0009).
/// Holds external references to the directory containing <c>map.bin</c>. The octree, entities, environment,
/// and command deltas live entirely in <c>map.bin</c>, so no scene file is needed in the map package.
///
/// On <c>_Ready</c> it materializes the octree from <c>map.bin</c> and replays trailing
/// deltas through the command sink, then meshes it. Entities are runtime projections
/// rebuilt from the package's entity list (§7.2).
///
/// The octree is Sauerbraten Z-up; like <see cref="Ogz.MapWorld"/> this node rotates
/// −90° about X so it sits in Godot Y-up space.
///
/// It is a <c>[Tool]</c> script for the same reason as <see cref="Editor.EmptyMap"/> and
/// <see cref="Ogz.MapWorld"/>: the editor viewport must build <em>this</em> node. A
/// non-tool script is never instantiated in the editor, so a parent
/// <see cref="Ogz.OgzMapViewer"/> would not see it as an <see cref="Runtime.IMapSource"/>,
/// fall back to its own default OGZ map, and the preview would disagree with the
/// map the scene actually loads at runtime.
/// </summary>
[Tool]
[GlobalClass]
public partial class MapLevel : Node3D, IGameWorld
{
    /// <summary>Chunk size for the mesher.</summary>
    [Export] public int ChunkSize { get; set; } = 512;

    public Node3D WorldNode => this;
    public Materials.ITextureSource? Textures => _textures;
    public Models.IModelCatalog? Models { get; private set; }
    public Node3D? EnvMaps { get; private set; }
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
    public CommandSink? Sink { get; set; }
    public WorldChunkRenderer? Renderer { get; set; }
    public Lighting.RadianceVolume? Radiance { get; set; }

    /// <summary>
    /// When true, bakes a radiance hints volume for the map during <see cref="Build"/>
    /// and uses it for surface shading (docs/adr/0002-radiance-hints.md).
    /// </summary>
    [Export] public bool BakeRadiance { get; set; }

    /// <summary>Optional quality settings overriding default radiance hints volume quality.</summary>
    public Lighting.LightingSettings? RadianceSettings { get; set; }

    /// <summary>The loaded FlatSharp MapPackage.</summary>
    public MapPackage? Package { get; private set; }

    /// <summary>
    /// Root of the reference dump's <c>packages/</c> tree, used to resolve the
    /// manifest's full texture paths when the project Resources do not have them.
    /// Empty disables the dump fallback (project Resources only).
    /// </summary>
    [Export] public string ReferenceRoot { get; set; } = "";

    /// <summary>The map directory (os or res:// path) the manifest and map.bin are loaded from.</summary>
    [Export] public string MapDir { get; set; } = "";

    /// <summary>Backing list of entities loaded from map.bin.</summary>
    public List<MapEntity> EntitiesList { get; } = new();

    /// <summary>Entities loaded from the map package (view of the command log; §7.2).</summary>
    public IReadOnlyList<MapEntity> Entities => Sink != null ? Sink.Entities : EntitiesList;

    public override void _Ready() => Build();

    /// <summary>Materialize the octree and deltas from map.bin, then mesh. Idempotent.</summary>
    public void Build()
    {
        if (Octa != null) return;

        RotationDegrees = new Vector3(-90, 0, 0); // Z-up -> Y-up
        Scale = new Vector3(-1, 1, 1);

        string effectiveMapDir = !string.IsNullOrEmpty(MapDir) ? MapDir : "";
        if (string.IsNullOrEmpty(effectiveMapDir) && !string.IsNullOrEmpty(SceneFilePath))
        {
            effectiveMapDir = SceneFilePath.GetBaseDir();
        }

        if (!string.IsNullOrEmpty(effectiveMapDir))
        {
            string binPath = Path.Combine(ProjectSettings.GlobalizePath(effectiveMapDir), MapStore.MapPackageFileName);
            if (File.Exists(binPath) || ResourceLoader.Exists(binPath))
            {
                var loaded = MapStore.Load(effectiveMapDir);
                Octa = loaded.World;
                EntitiesList.Clear();
                EntitiesList.AddRange(loaded.Entities);
                Package = loaded.Package;
                Physics = new OctaPhysics(loaded.World);
                if (_clock.NowMillis == 0) _clock.Set((int)Time.GetTicksMsec());
                // Use the environment resolved from the keyframe + trailing deltas
                // (MapStore.Load), not the package's baseline copy: a streamed
                // SetEnvironment command is the authoritative value (ADR 0009 §6).
                Sink = new CommandSink(loaded.World, EntitiesList, loaded.Environment, initialKeyframe: loaded.LatestKeyframe);
            }
        }

        if (Octa == null) return;

        if (_textures == null) _textures = BuildTextureSource();

        if (BakeRadiance)
        {
            var config = MapEnvironment.ToConfig(Sink?.Environment);
            var directLights = DirectLightSource.CollectFromEntities(Entities);
            var volume = RadianceVolume.CreateForOcta(Octa, RadianceSettings);
            volume.Bake(Octa, config, directLights, catalogue: null, textureSource: _textures);
            Radiance = volume;
        }

        // Resolve the manifest (if any) into a material set so the map renders
        // textured; editor maps without an imported manifest mesh untextured.
        MaterialSet? materials = null;
        if (!string.IsNullOrEmpty(effectiveMapDir))
        {
            var manifest = MapStore.LoadManifest(effectiveMapDir);
            if (manifest != null)
            {
                if (Radiance != null)
                {
                    var config = MapEnvironment.ToConfig(Sink?.Environment);
                    materials = ManifestBuilder.ToRadianceMaterialSet(manifest, _textures, Radiance, config);
                }
                else
                {
                    materials = ManifestBuilder.ToMaterialSet(manifest, _textures);
                }
            }
        }

        Material? fallback = null;
        if (materials == null && Radiance != null)
        {
            var config = MapEnvironment.ToConfig(Sink?.Environment);
            fallback = MaterialBuilder.BuildRadianceMaterial(
                albedo: null,
                tint: Colors.White,
                radianceTex: Radiance.Texture,
                gridOrigin: Radiance.Grid.Origin,
                gridExtent: Radiance.Grid.Extent,
                skyTint: RadianceVolume.DecodeColor(config.SkyboxColour),
                ambientColor: RadianceVolume.DecodeColor(config.Ambient),
                useRadiance: Radiance.Settings.Mode == LightingQualityMode.RadianceHints,
                radianceAmbTex: Radiance.AmbientTexture);
        }

        Renderer = new WorldChunkRenderer
        {
            Name = "WorldMesh",
            ChunkSize = ChunkSize,
            BudgetPerFrame = 4,
            Radiance = Radiance,
            FallbackMaterial = fallback,
        };
        AddChild(Renderer);
        Renderer.Setup(Octa, materials);

        // Converted mapmodels (assimp), imported by Godot as Resources.
        var modelCatalog = ModelCatalog.LoadFromProject();
        Models = modelCatalog;

        SyncLights();
        var envMaps = EnvMapBuilder.Build(Entities);
        EnvMaps = envMaps;
        AddChild(envMaps);
        Radiance?.RegisterEnvMaps(envMaps);
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

        SyncEnvironment();

        GD.Print($"[maplevel] worldsize={Octa.WorldSize} chunks={Renderer.ChunkCount} tris={Renderer.TriangleCount} entities={Entities.Count}");
    }

    private readonly WorldSimulation _simulation = new();

    public override void _PhysicsProcess(double delta)
    {
        _simulation.Step(this, delta);
    }

    // The command sink is replaced wholesale on LoadMap; track both identities so a
    // new sink (or a new environment command on the same sink) re-applies the scene
    // nodes. Reference comparison is enough: commands are immutable payloads.
    private CommandSink? _environmentSink;
    private SetEnvironmentCmd? _appliedEnvironment;

    public override void _Process(double delta)
    {
        SyncEnvironment();
        SyncLights();
    }

    /// <summary>
    /// Materialize the current <see cref="CommandSink.Environment"/> onto this map's
    /// scene nodes (sun, sky, ambient, fog), per ADR 0009 §6.
    /// </summary>
    public void SyncEnvironment()
    {
        if (Sink == null) return;
        if (ReferenceEquals(Sink, _environmentSink) && ReferenceEquals(Sink.Environment, _appliedEnvironment)) return;

        _environmentSink = Sink;
        _appliedEnvironment = Sink.Environment;
        Lighting.MapEnvironment.Apply(this, Sink.Environment, _textures);

        if (Radiance != null && Octa != null)
        {
            var config = MapEnvironment.ToConfig(Sink.Environment);
            var directLights = DirectLightSource.CollectFromEntities(Entities);
            Radiance.Bake(Octa, config, directLights, catalogue: null, textureSource: _textures);
        }
    }

    private CommandSink? _lightsSink;
    private int _appliedLightHash;
    private bool _lightsApplied;

    /// <summary>
    /// Materialize entity lights onto this map's scene nodes under a "Lights" child,
    /// and keep direct lights on the radiance volume in sync (ADR 0009 §6).
    /// </summary>
    public void SyncLights()
    {
        int currentHash = ComputeLightHash(Entities);
        if (_lightsApplied && ReferenceEquals(Sink, _lightsSink) && currentHash == _appliedLightHash)
            return;

        _lightsSink = Sink;
        _appliedLightHash = currentHash;
        _lightsApplied = true;

        LightBuilder.Apply(this, Entities);

        if (Radiance != null)
        {
            var directLights = DirectLightSource.CollectFromEntities(Entities);
            Radiance.UpdateLights(directLights);
        }
    }

    private static int ComputeLightHash(IReadOnlyList<MapEntity> entities)
    {
        var hash = new System.HashCode();
        foreach (var e in entities)
        {
            if (e.Type == EntityType.Light || e.Type == EntityType.SpotLight)
            {
                hash.Add(e.Id);
                hash.Add(e.Type);
                hash.Add(e.Position);
                hash.Add(e.Attr1);
                hash.Add(e.Attr2);
                hash.Add(e.Attr3);
                hash.Add(e.Attr4);
                hash.Add(e.Attr5);
            }
        }
        return hash.ToHashCode();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Radiance?.Dispose();
        }
        base.Dispose(disposing);
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
