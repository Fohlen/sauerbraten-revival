using System;
using System.Collections.Generic;
using Godot;
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
public partial class MapWorld : Node3D
{
    /// <summary>Override map to build. Absolute, or package-relative (e.g. <c>base/church51.ogz</c>), resolved against <c>res://assets/packages/</c> then the reference dump; empty uses the default.</summary>
    [Export] public string OgzPath { get; set; } = "";
    [Export] public int ChunkSize { get; set; } = 512;

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

    /// <summary>The parsed map (slots, entities, metadata). Null until <see cref="Build"/>.</summary>
    public OgzMap? Map { get; private set; }

    /// <summary>The runtime octree — the collision authority. Null until <see cref="Build"/>.</summary>
    public Octa? Octa { get; private set; }

    /// <summary>The shared octree physics simulation authority for this world.</summary>
    public OctaPhysics? Physics { get; private set; }

    private SimClock _clock = new();

    /// <summary>
    /// The simulation clock for this world (engine <c>lastmillis</c>). Exported so a
    /// scene or session can assign and persist one; assigning null falls back to a
    /// fresh clock. Per-world/per-session by design — never a process-wide autoload —
    /// so hosting two sessions in one process keeps independent time bases.
    /// </summary>
    [Export]
    public SimClock Clock
    {
        get => _clock;
        set => _clock = value ?? new SimClock();
    }

    /// <summary>Current simulation time in milliseconds.</summary>
    public int LastMillis => Clock.NowMillis;

    private readonly List<MapPlayer> _players = new();
    /// <summary>Active players stepped by this world's physics loop.</summary>
    public IReadOnlyList<MapPlayer> Players => _players;

    /// <summary>Active flying projectiles (rockets) stepped centrally by this world's physics loop.</summary>
    public List<Weapons.RocketProjectile> ActiveRockets { get; } = new();

    /// <summary>Active bouncers (grenades, debris, gibs) stepped centrally by this world's physics loop.</summary>
    public List<BounceBody> ActiveBouncers { get; } = new();

    /// <summary>Backing read-only list for active bouncers.</summary>
    public IReadOnlyList<BounceBody> Bouncers => ActiveBouncers;

    /// <summary>Registers a player to be stepped by the world's physics loop.</summary>
    public void RegisterPlayer(MapPlayer player)
    {
        if (!_players.Contains(player))
        {
            _players.Add(player);
            player.AttachClock(Clock);
        }
    }

    /// <summary>Unregisters a player from the world's physics loop.</summary>
    public void UnregisterPlayer(MapPlayer player)
    {
        if (_players.Remove(player)) player.DetachClock();
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
        var targets = candidates ?? (IReadOnlyList<Weapons.IExplosionTarget>)_players;
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
        var targets = candidates ?? (IReadOnlyList<Weapons.IExplosionTarget>)_players;
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

    /// <summary>The texture source used while building (for later sky/material use).</summary>
    public ITextureSource? Textures { get; private set; }

    /// <summary>The converted-model catalogue (glTF paths + Cube2 parameters), if present.</summary>
    public ModelCatalog? Models { get; private set; }

    /// <summary>The item manager holding map item pickups.</summary>
    public ItemManager? Items { get; private set; }

    /// <summary>Flag manager tracking CTF / Protect / Hold state.</summary>
    public Game.FlagManager? Flags { get; private set; }

    /// <summary>Shared weapon tracer and explosion effects renderer for this world.</summary>
    public Effects.WeaponEffectsRenderer? WeaponEffects { get; set; }

    /// <summary>Active game mode info.</summary>
    public Game.GameModeInfo? Mode { get; set; }

    /// <summary>Local player team (1 = blue, 2 = red). Default 1.</summary>
    public int LocalPlayerTeam { get; set; } = 1;

    /// <summary>Optional override for carrier player position and orientation.</summary>
    public Func<int, (Vector3 pos, float yaw, float aboveEye)>? PlayerProvider { get; set; }

    /// <summary>Resolves carrier player position and orientation by player ID from registered players.</summary>
    public (Vector3 pos, float yaw, float aboveEye) GetPlayerOwnerInfo(int playerId)
    {
        if (PlayerProvider != null) return PlayerProvider(playerId);
        for (int i = 0; i < _players.Count; i++)
        {
            var p = _players[i];
            if (p.PlayerId == playerId)
                return (p.Entity.O, p.Entity.Yaw, p.Entity.AboveEye);
        }
        if (_players.Count > 0)
        {
            var p = _players[0];
            return (p.Entity.O, p.Entity.Yaw, p.Entity.AboveEye);
        }
        return default;
    }

    private readonly List<FlagNode> _flagNodes = new();
    public IReadOnlyList<FlagNode> FlagNodes => _flagNodes;

    /// <summary>Active jump pad trigger entities in this map.</summary>
    public IReadOnlyList<JumpPad> JumpPads { get; private set; } = System.Array.Empty<JumpPad>();

    /// <summary>Active teleporter trigger entities in this map.</summary>
    public IReadOnlyList<Teleport> Teleports { get; private set; } = System.Array.Empty<Teleport>();

    /// <summary>Optional settings overriding default radiance hints volume quality.</summary>
    public Lighting.RadianceSettings? RadianceSettings { get; set; }

    /// <summary>The radiance hints volume, when baked.</summary>
    public Lighting.RadianceVolume? Radiance { get; private set; }

    /// <summary>AABB of the geometry in Sauerbraten (Z-up, local) space.</summary>
    public Aabb Bounds { get; private set; }

    /// <summary>Centre of the geometry in Godot world space (Y-up), for framing a camera.</summary>
    public Vector3 Centre { get; private set; }

    /// <summary>Bounding radius of the geometry, for framing a camera.</summary>
    public float Radius { get; private set; } = 1.0f;

    /// <summary>True once <see cref="Build"/> has produced an <see cref="Octa"/>.</summary>
    public bool Built => Octa != null;

    private static readonly Color MeshTint = new(0.8f, 0.8f, 0.85f);

    public override void _Ready()
    {
        if (!Built) Build();
    }

    /// <summary>
    /// Build the world as children of this node. Idempotent: subsequent calls are
    /// no-ops once built. Missing maps log an error and leave <see cref="Built"/> false.
    /// </summary>
    public void Build()
    {
        if (Built) return;

        string path = string.IsNullOrEmpty(OgzPath) ? DefaultPath() : ResolvePath(OgzPath);
        if (!Godot.FileAccess.FileExists(path))
        {
            GD.PushError($"[world] OGZ not found: '{path}'. Set OgzPath or SAUER_REF.");
            return;
        }

        GD.Print($"[world] building {path} (editor={Engine.IsEditorHint()})");

        // The octree/collision layer is Z-up; rotate so the Godot view is Y-up.
        RotationDegrees = new Vector3(-90, 0, 0);

        var map = OgzReader.Read(path);
        var octa = map.ToOcta();
        Map = map;
        Octa = octa;
        Physics = new OctaPhysics(octa);
        // Bootstrap a fresh clock. A session-assigned clock (nonzero) keeps its value
        // so the same session can carry time across worlds.
        if (Clock.NowMillis == 0) Clock.Set((int)Time.GetTicksMsec());

        string mapName = path.GetFile().GetBaseName();
        ITextureSource textures = new FallbackTextureSource(new ResourceTextureSource(), new DumpTextureSource(MapRoots.ReferenceRoot));
        Textures = textures;
        var catalogue = MetadataCatalogue.TryLoadForMap(mapName);

        // Radiance hints volume: bake sky, sun, and direct entity lights (docs/adr/0002-radiance-hints.md §4.1, §4.2)
        Lighting.RadianceVolume? radiance = null;
        if (catalogue != null)
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
        const string modelsResDir = "res://assets/models";
        string modelsDir = ProjectSettings.GlobalizePath(modelsResDir);
        var modelCatalog = ModelCatalog.Load($"{modelsResDir}/models.json", modelsDir, modelsResDir);
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
        var flagsRoot = new Node3D { Name = "Flags" };
        _flagNodes.Clear();
        if (Mode == null || Mode.Has(Game.ModeFlags.Ctf))
        {
            bool isHold = Mode != null && Mode.Has(Game.ModeFlags.Hold);
            if (isHold)
            {
                // ctf.h:729: BASE entities are hold spawns; HOLDFLAGS neutral flags
                // are created dynamically, not baked from the map's FLAG entities.
                foreach (var (_, e) in map.OfType(EntityType.Base))
                    Flags.AddHoldSpawn(e.Position);
                Flags.SetupHoldSpawns();
                foreach (var flag in Flags.Flags)
                {
                    var flagNode = CreateFlagNode(flag.Id, flag.Team, flag.SpawnLoc, 0f, modelCatalog, textures);
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
                    var flagNode = CreateFlagNode(flagId, e.Attr2, e.Position, e.Attr1, modelCatalog, textures);
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

        Aabb bounds = default;
        bool hasBounds = false;
        foreach (var chunk in chunked.Chunks)
        {
            AddChild(new MeshInstance3D
            {
                Name = $"Chunk_{chunk.Origin.X}_{chunk.Origin.Y}_{chunk.Origin.Z}",
                Mesh = chunk.Mesh,
                Position = new Vector3(chunk.Origin.X, chunk.Origin.Y, chunk.Origin.Z),
                MaterialOverride = fallback,
            });

            var aabb = chunk.Mesh.GetAabb();
            aabb.Position += new Vector3(chunk.Origin.X, chunk.Origin.Y, chunk.Origin.Z);
            bounds = hasBounds ? bounds.Merge(aabb) : aabb;
            hasBounds = true;
        }

        // Sauerbraten bounds are Z-up; the node rotates them to Godot Y-up.
        Bounds = hasBounds ? bounds : new Aabb(Vector3.Zero, Vector3.One);
        Vector3 sauerCentre = Bounds.GetCenter();
        Centre = new Vector3(sauerCentre.X, sauerCentre.Z, -sauerCentre.Y);
        Radius = Mathf.Max(Bounds.Size.Length() * 0.5f, 1.0f);

        GD.Print($"[world] {path}: chunks={chunked.Chunks.Count} triangles={chunked.TriangleCount}");
    }

    private double _physicsAccum;
    private const double StepSeconds = Phys.PhysFrameTime / 1000.0;

    public override void _PhysicsProcess(double delta)
    {
        if (Engine.IsEditorHint() || Octa == null || Physics == null) return;

        _physicsAccum += delta;
        int guard = 0;
        while (_physicsAccum >= StepSeconds && guard++ < 100)
        {
            _physicsAccum -= StepSeconds;
            Clock.Advance(Phys.PhysFrameTime);

            // 1. Gather player inputs before stepping physics
            for (int i = 0; i < _players.Count; i++)
            {
                _players[i].PrePhysicsStep();
            }

            // 2. Clear and update dynent cache once per 5 ms substep
            Physics.ClearDynentCache();
            foreach (var dynent in Physics.DynEnts)
            {
                Physics.UpdateDynentCache(dynent);
            }

            // 3. Step registered players
            for (int i = 0; i < _players.Count; i++)
            {
                _players[i].Step(Phys.PhysFrameTime);
            }

            // 4. Update active projectiles (weapon.cpp:523 updateprojectiles)
            UpdateProjectiles(Phys.PhysFrameTime);

            // 5. Update active bouncers (weapon.cpp:231 updatebouncers)
            UpdateBouncers(Phys.PhysFrameTime);

            // 6. Update game mode flag rules with synchronized simulation clock
            if (Flags != null && (Mode == null || Mode.Has(Game.ModeFlags.Ctf)))
            {
                var mode = Mode ?? Game.GameModeCatalog.Get("ctf");
                Flags.Update(mode, LastMillis);
            }

            // 7. Update item respawns with synchronized simulation clock
            Items?.UpdateRespawns(Phys.PhysFrameTime);

            // 8. Post physics step for players (attack cooldowns, animations)
            for (int i = 0; i < _players.Count; i++)
            {
                _players[i].PostPhysicsStep();
            }
        }

        for (int i = 0; i < _players.Count; i++)
        {
            _players[i].SyncTransform();
        }
        for (int i = 0; i < ActiveBouncers.Count; i++)
        {
            ActiveBouncers[i].SyncTransform();
        }
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

        if (Flags != null && _flagNodes.Count > 0 && (Mode == null || Mode.Has(Game.ModeFlags.Ctf)))
        {
            var mode = Mode ?? Game.GameModeCatalog.Get("ctf");
            for (int i = 0; i < _flagNodes.Count; i++)
            {
                if (i < Flags.Flags.Count)
                {
                    _flagNodes[i].UpdateVisual(Flags.Flags[i], mode, lastMillis, LocalPlayerTeam, getOwnerInfo: GetPlayerOwnerInfo);
                }
            }
        }

        Items?.UpdateVisual(lastMillis);
    }

    private static string DefaultPath() => MapRoots.Resolve("base/church51");

    /// <summary>Creates the visual node for one flag, oriented by its spawn yaw.</summary>
    private static FlagNode CreateFlagNode(int flagId, int team, Vector3 position, float yaw,
                                           ModelCatalog? models, ITextureSource textures)
    {
        var flagNode = new FlagNode
        {
            Name = $"Flag_{flagId}",
            FlagId = flagId,
            Team = team,
            Models = models,
            Textures = textures,
            Position = position,
        };
        EntityFacing.ApplyYaw(flagNode, yaw);
        return flagNode;
    }

    private static string ResolvePath(string ogzPath) => MapRoots.Resolve(ogzPath);
}
