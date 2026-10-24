using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SauerbratenRevival.World.Content;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Meshing;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Ogz;

/// <summary>
/// Builds and owns the runtime octree world for one legacy <c>.ogz</c> map: the
/// <see cref="Octa"/> collision geometry plus all rendered geometry and entities,
/// under a single node rotated Z-up → Y-up.
///
/// This is the shared world layer. A scene attaches its own camera/controller
/// (<see cref="FreeFlyCamera"/> for the inspection/editor scene, a player
/// controller for the play scene); the collision layer reads <see cref="Octa"/>.
///
/// This is the load path for classic maps (it also backs the play scene); see
/// docs/adr/0006-ogz-import.md.
///
/// It is a <c>[Tool]</c> script so the geometry also appears in the editor.
/// </summary>
[Tool]
[GlobalClass]
public partial class MapWorld : MapSourceNode
{
    /// <summary>Override map to build. Absolute, or package-relative (e.g. <c>base/church51.ogz</c>), resolved through <see cref="ContentRoots.ResolveMap"/>; empty uses the default.</summary>
    [Export] public string OgzPath { get; set; } = "";

    /// <summary>Show edit-mode blue entity markers and facing arrows.</summary>
    [Export]
    public bool ShowEntityMarkers
    {
        get => _showEntityMarkers;
        set
        {
            _showEntityMarkers = value;
            EntityMarker.SetVisible(this, value);
        }
    }
    private bool _showEntityMarkers = true;

    /// <summary>The parsed map (slots, entities, metadata). Null until <see cref="Build"/>, or pre-assigned to inject a map.</summary>
    public OgzMap? Map { get; set; }


    /// <summary>
    /// The local player, whose <see cref="MapPlayer.Team"/> drives local-only
    /// presentation (flag colours). Set by the scene that spawns it; null in
    /// world-only contexts such as the edit viewer or tests.
    /// </summary>
    public MapPlayer? LocalPlayer { get; set; }

    /// <summary>Active flying projectiles (rockets) stepped centrally by this world's physics loop.</summary>
    public List<Weapons.RocketProjectile> ActiveRockets { get; } = new();

    /// <summary>Active bouncers (grenades, debris, gibs) stepped centrally by this world's physics loop.</summary>
    public List<BounceBody> ActiveBouncers { get; } = new();

    /// <summary>Backing read-only list for active bouncers.</summary>
    public IReadOnlyList<BounceBody> Bouncers => ActiveBouncers;

    /// <summary>Registers a player to be stepped by the world's physics loop.</summary>
    public override void RegisterPlayer(MapPlayer player)
    {
        base.RegisterPlayer(player);
        if (Mode != null) player.Mode = Mode;
    }

    /// <summary>Unregisters a player from the world's physics loop.</summary>
    public override void UnregisterPlayer(MapPlayer player)
    {
        base.UnregisterPlayer(player);
        if (ReferenceEquals(LocalPlayer, player)) LocalPlayer = null;
    }

    /// <summary>Registers a bouncer to be stepped by the world's physics loop.</summary>
    public void RegisterBouncer(BounceBody bouncer)
    {
        if (!ActiveBouncers.Contains(bouncer))
        {
            ActiveBouncers.Add(bouncer);
        }
    }

    /// <summary>Unregisters a bouncer from the world's physics loop.</summary>
    public void UnregisterBouncer(BounceBody bouncer)
    {
        ActiveBouncers.Remove(bouncer);
    }

    /// <summary>
    /// Updates all active flying projectiles (rockets) in the world.
    /// Port of <c>fpsgame/weapon.cpp:523 updateprojectiles</c>.
    /// </summary>
    public void UpdateProjectiles(int timeMillis, IReadOnlyList<Weapons.IExplosionTarget>? candidates = null)
    {
        var targets = candidates ?? (IReadOnlyList<Weapons.IExplosionTarget>)PlayerList;
        for (int i = ActiveRockets.Count - 1; i >= 0; i--)
        {
            var rocket = ActiveRockets[i];
            if (rocket.Advance(timeMillis, Octa, targets))
            {
                ActiveRockets.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Updates all active bouncers (grenades, debris, gibs) in the world.
    /// Port of <c>fpsgame/weapon.cpp:231 updatebouncers</c>.
    /// </summary>
    public void UpdateBouncers(int timeMillis, IReadOnlyList<Weapons.IExplosionTarget>? candidates = null)
    {
        var targets = candidates ?? (IReadOnlyList<Weapons.IExplosionTarget>)PlayerList;
        for (int i = ActiveBouncers.Count - 1; i >= 0; i--)
        {
            var bouncer = ActiveBouncers[i];
            if (bouncer.Exploded || !GodotObject.IsInstanceValid(bouncer) || bouncer.IsQueuedForDeletion())
            {
                ActiveBouncers.RemoveAt(i);
                continue;
            }
            bouncer.Step(timeMillis, targets);
            if (bouncer.Exploded)
            {
                ActiveBouncers.RemoveAt(i);
            }
        }
    }

    private Game.GameModeInfo? _mode;

    /// <summary>Active game mode info.</summary>
    public Game.GameModeInfo? Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            if (value != null)
            {
                if (Items != null) Items.Mode = value;
                if (Flags != null) Flags.Mode = value;
                for (int i = 0; i < PlayerList.Count; i++) PlayerList[i].Mode = value;
                foreach (var child in GetChildren())
                {
                    if (child is MapPlayer p) p.Mode = value;
                    else if (child is ItemManager im) im.Mode = value;
                }
            }
        }
    }

    /// <summary>Resolves carrier player position and orientation by player ID from registered players.</summary>
    public (Vector3 pos, float yaw, float aboveEye) GetPlayerOwnerInfo(int playerId)
    {
        for (int i = 0; i < PlayerList.Count; i++)
        {
            var p = PlayerList[i];
            if (p.PlayerId == playerId)
                return (p.Entity.O, p.Entity.Yaw, p.Entity.AboveEye);
        }
        return default;
    }

    private readonly List<FlagNode> _flagNodes = new();
    public IReadOnlyList<FlagNode> FlagNodes => _flagNodes;

    /// <summary>Optional settings overriding default radiance hints volume quality.</summary>
    public Lighting.RadianceSettings? RadianceSettings { get; set; }

    /// <summary>Whether to bake the radiance hints volume during <see cref="Build"/>. True by default; disable in headless tests for fast world initialization.</summary>
    [Export] public bool BakeRadiance { get; set; } = true;

    private static readonly Color MeshTint = new(0.8f, 0.8f, 0.85f);

    private static readonly Dictionary<string, OgzMap> ParsedMapCache = new();

    /// <summary>Process-wide toggle to cache parsed OGZ maps across MapWorld instances (cloning each time).</summary>
    public static bool UseMapCache { get; set; }

    /// <summary>Clear the static map cache (e.g. on test cleanup or asset reload).</summary>
    public static void ClearMapCache() => ParsedMapCache.Clear();

    private static OgzMap LoadMap(string path)
    {
        if (UseMapCache)
        {
            if (ParsedMapCache.TryGetValue(path, out var cached))
                return cached.Clone();
            var read = OgzReader.Read(path);
            ParsedMapCache[path] = read;
            return read.Clone();
        }
        return OgzReader.Read(path);
    }

    /// <summary>
    /// Build the world as children of this node. Idempotent: subsequent calls are
    /// no-ops once built. Missing maps log an error and leave <see cref="Built"/> false.
    /// </summary>
    public override void Build()
    {
        if (Built) return;

        string path = string.IsNullOrEmpty(OgzPath) ? DefaultPath() : ResolvePath(OgzPath);
        if (Map == null && !Godot.FileAccess.FileExists(path))
        {
            GD.PushError($"[world] OGZ not found: '{path}'. Set OgzPath or SAUER_REF.");
            return;
        }

        GD.Print($"[world] building {path} (editor={Engine.IsEditorHint()})");

        // The octree/collision layer is Z-up; rotate so the Godot view is Y-up.
        RotationDegrees = new Vector3(-90, 0, 0);

        var map = Map ?? LoadMap(path);
        var octa = Octa ?? map.ToOcta();
        Map = map;
        Octa = octa;
        InitPhysics(octa);
        // Bootstrap a fresh clock. A session-assigned clock (nonzero) keeps its value
        // so the same session can carry time across worlds.
        BootstrapClock();

        string mapName = path.GetFile().GetBaseName();
        ITextureSource textures = TextureSources.CreateDefault();
        Textures = textures;
        var catalogue = MetadataCatalogue.TryLoadForMap(mapName);

        // Radiance hints volume: bake sky, sun, and direct entity lights (docs/adr/0002-radiance-hints.md §4.1, §4.2)
        Lighting.RadianceVolume? radiance = null;
        if (BakeRadiance && catalogue != null)
        {
            var volume = Lighting.RadianceVolume.CreateForOcta(octa, RadianceSettings);
            var directLights = Lighting.DirectLightSource.CollectFromMap(map);
            volume.Bake(octa, catalogue.Config, directLights, catalogue, textures);
            radiance = volume;
            Radiance = volume;
        }

        MaterialSet? materials = catalogue != null ? MaterialBuilder.Build(catalogue, textures, radiance) : null;
        if (materials == null) GD.PushWarning($"[world] no metadata catalogue for '{mapName}'; showing untextured geometry.");

        var chunked = new OctaMesher(octa, materials).BuildChunked(ChunkSize);

        // Converted mapmodels (assimp), imported by Godot as Resources.
        var modelCatalog = ModelCatalog.LoadFromProject();
        Models = modelCatalog;

        AddChild(LightBuilder.Build(map));
        AddChild(EnvMapBuilder.Build(map));
        AddChild(PlayerStartBuilder.Build(map, textures));
        AddChild(ModeEntityBuilder.Build(map, catalogue, modelCatalog, textures, Mode));
        AddChild(MonsterBuilder.Build(map, modelCatalog, textures));
        var (triggers, jumpPads, teleports) = TriggerBuilder.BuildTriggers(map, catalogue, modelCatalog, textures);
        JumpPads = jumpPads;
        Teleports = teleports;
        AddChild(triggers);
        Items = ItemBuilder.Build(map, modelCatalog, textures);
        Items.AutoAdvance = false;
        AddChild(Items);

        // Flag manager and visual flag nodes (ctf.h setup). Flags are a ctf-mode
        // concept: the engine selects ctf/capture mode code from the flags
        // (client.cpp setclientmode), and only m_ctf builds flag state. Other modes
        // get an empty manager so player/HUD code can keep a stable reference.
        Flags = new Game.FlagManager();
        if (Mode != null) Flags.Mode = Mode;
        var flagsRoot = new Node3D { Name = "Flags" };
        _flagNodes.Clear();
        if (Mode != null && Mode.Has(Game.ModeFlags.Ctf))
        {
            bool isHold = Mode.Has(Game.ModeFlags.Hold);
            if (isHold)
            {
                // ctf.h:729: BASE entities are hold spawns; HOLDFLAGS neutral flags
                // are created dynamically, not baked from the map's FLAG entities.
                foreach (var (_, e) in map.OfType(EntityType.Base))
                    Flags.AddHoldSpawn(e.Position);
                Flags.SetupHoldSpawns();
                foreach (var flag in Flags.Flags)
                {
                    var flagNode = CreateFlagNode(flag.Id, flag.Team, flag.SpawnLoc, 0f, modelCatalog, textures, Mode);
                    _flagNodes.Add(flagNode);
                    flagsRoot.AddChild(flagNode);
                }
            }
            else
            {
                // ctf.h:311: one flag per team-coloured map FLAG entity.
                foreach (var (_, e) in map.OfType(EntityType.Flag))
                {
                    if (e.Attr2 < 1 || e.Attr2 > 2) continue;
                    int flagId = Flags.Flags.Count;
                    Flags.AddFlag(flagId, e.Position, e.Attr2, 0, isProtect: false, spawnAngle: e.Attr1);
                    var flagNode = CreateFlagNode(flagId, e.Attr2, e.Position, e.Attr1, modelCatalog, textures, Mode);
                    _flagNodes.Add(flagNode);
                    flagsRoot.AddChild(flagNode);
                }
            }
        }
        AddChild(flagsRoot);

        AddChild(ParticleBuilder.Build(map, textures));
        AddChild(SoundBuilder.Build(map, catalogue));
        var mapModels = MapModelBuilder.Build(map, catalogue, modelCatalog, textures);
        AddChild(mapModels);
        int loadedModels = 0, totalModels = 0;
        foreach (var child in mapModels.GetChildren())
        {
            totalModels++;
            if (child is MapModel { ModelLoaded: true }) loadedModels++;
        }
        GD.Print($"[world] mapmodels: {loadedModels}/{totalModels} loaded");

        if (!ShowEntityMarkers)
            EntityMarker.SetVisible(this, false);

        // Water/lava/glass material surfaces (empty-space materials).
        var materialSurfaces = MaterialMesher.Build(octa);
        GD.Print($"[world] material surfaces: {materialSurfaces.Count}");
        foreach (var surface in materialSurfaces)
        {
            AddChild(new MeshInstance3D
            {
                Name = $"Material_{surface.Material}",
                Mesh = surface.Mesh,
                MaterialOverride = MaterialTextures.Create(surface.Material, textures),
            });
        }

        // Fallback so geometry is still visible if there is no catalogue.
        StandardMaterial3D? fallback = materials == null
            ? new StandardMaterial3D { AlbedoColor = MeshTint, CullMode = BaseMaterial3D.CullModeEnum.Back }
            : null;

        CreateRenderer(octa, materials, chunked, fallback);
        InitSink(octa);
        LinkChildEditors();

        GD.Print($"[world] {path}: chunks={chunked.Chunks.Count} triangles={chunked.TriangleCount}");
    }

    /// <summary>Use the meshed geometry AABB for spawn fallback and camera framing.</summary>
    protected override bool UseGeometryBounds => true;

    /// <summary>Finds a player start spawn point in Sauerbraten coordinates, falling back to bounds center.</summary>
    public (Vector3 Position, float Yaw) FindSpawn(int index = 0) =>
        Map != null
            ? IMapSource.ResolveSpawn(Map.OfType(Entities.EntityType.PlayerStart).Select(t => (t.Entity.Position, (float)t.Entity.Attr1)), Bounds.GetCenter(), index)
            : (Bounds.GetCenter(), 0f);

    protected override void OnStepped(int timeMillis)
    {
        // Gameplay-specific per-substep updates.
        UpdateProjectiles(timeMillis);
        UpdateBouncers(timeMillis);

        if (Flags != null && Mode != null && Mode.Has(Game.ModeFlags.Ctf))
            Flags.Update(LastMillis);

        Items?.UpdateRespawns(timeMillis);
    }

    protected override void OnAfterSteps()
    {
        for (int i = 0; i < ActiveBouncers.Count; i++)
            ActiveBouncers[i].SyncTransform();
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint()) return;
        int lastMillis = LastMillis;
        if (Teleports != null)
        {
            for (int i = 0; i < Teleports.Count; i++)
            {
                Teleports[i].UpdateVisual(lastMillis);
            }
        }

        if (Flags != null && _flagNodes.Count > 0 && Mode != null && Mode.Has(Game.ModeFlags.Ctf))
        {
            for (int i = 0; i < _flagNodes.Count; i++)
            {
                if (i < Flags.Flags.Count)
                {
                    _flagNodes[i].UpdateVisual(Flags.Flags[i], lastMillis, LocalPlayer?.Team ?? 1, getOwnerInfo: GetPlayerOwnerInfo);
                }
            }
        }

        Items?.UpdateVisual(lastMillis);
    }

    private static string DefaultPath() => ContentRoots.ResolveMap("base/church51");

    /// <summary>Creates the visual node for one flag, oriented by its spawn yaw.</summary>
    private static FlagNode CreateFlagNode(int flagId, int team, Vector3 position, float yaw,
                                           IModelCatalog? models, ITextureSource textures, Game.GameModeInfo? mode = null)
    {
        var flagNode = new FlagNode
        {
            Name = $"Flag_{flagId}",
            FlagId = flagId,
            Team = team,
            Models = models,
            Textures = textures,
            Position = position,
            Mode = mode ?? Game.GameModeCatalog.Get("ctf"),
        };
        EntityFacing.ApplyYaw(flagNode, yaw);
        return flagNode;
    }

    private static string ResolvePath(string ogzPath) => ContentRoots.ResolveMap(ogzPath);
}
