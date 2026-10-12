using System;
using System.IO;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Meshing;

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
/// It deliberately reads OGZ at runtime for tooling convenience — it is NOT the
/// shipping path. The shipped importer converts maps to the native format; see
/// docs/ogz-import.md.
///
/// It is a <c>[Tool]</c> script so the geometry also appears in the editor.
/// </summary>
[Tool]
[GlobalClass]
public partial class MapWorld : Node3D
{
    /// <summary>Override map to build. Absolute, or relative to the reference dump's <c>packages/</c> dir (e.g. <c>base/church51.ogz</c>); empty uses the default.</summary>
    [Export] public string OgzPath { get; set; } = "";
    [Export] public int ChunkSize { get; set; } = 512;

    /// <summary>The parsed map (slots, entities, metadata). Null until <see cref="Build"/>.</summary>
    public OgzMap? Map { get; private set; }

    /// <summary>The runtime octree — the collision authority. Null until <see cref="Build"/>.</summary>
    public Octa? Octa { get; private set; }

    /// <summary>The texture source used while building (for later sky/material use).</summary>
    public ITextureSource? Textures { get; private set; }

    /// <summary>The converted-model catalogue (glTF paths + Cube2 parameters), if present.</summary>
    public ModelCatalog? Models { get; private set; }

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
        if (!File.Exists(path))
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

        string mapName = Path.GetFileNameWithoutExtension(path);
        ITextureSource textures = new FallbackTextureSource(new ResourceTextureSource(), new DumpTextureSource(MapRoots.ReferenceRoot));
        Textures = textures;
        var catalogue = MetadataCatalogue.TryLoadForMap(mapName);

        // Radiance hints volume: bake sky, sun, and direct entity lights (docs/radiance-hints.md §4.1, §4.2)
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
        var modelCatalog = ModelCatalog.Load(Path.Combine(modelsDir, "models.json"), modelsDir, modelsResDir);
        Models = modelCatalog;

        AddChild(LightBuilder.Build(map));
        AddChild(EnvMapBuilder.Build(map));
        AddChild(PlayerStartBuilder.Build(map, textures));
        AddChild(ModeEntityBuilder.Build(map, catalogue, modelCatalog, textures));
        AddChild(MonsterBuilder.Build(map, modelCatalog, textures));
        AddChild(TriggerBuilder.Build(map, catalogue, textures));
        AddChild(ItemBuilder.Build(map, modelCatalog, textures));
        AddChild(ParticleBuilder.Build(map, textures));
        AddChild(SoundBuilder.Build(map, catalogue, MapRoots.ReferenceRoot));
        var mapModels = MapModelBuilder.Build(map, catalogue, modelCatalog, textures);
        AddChild(mapModels);
        int loadedModels = 0, totalModels = 0;
        foreach (var child in mapModels.GetChildren())
        {
            totalModels++;
            if (child is MapModel { ModelLoaded: true }) loadedModels++;
        }
        GD.Print($"[world] mapmodels: {loadedModels}/{totalModels} loaded");

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

    private static string DefaultPath() => MapRoots.Resolve("base/church51");

    private static string ResolvePath(string ogzPath) => MapRoots.Resolve(ogzPath);
}
