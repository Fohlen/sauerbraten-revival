using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Meshing;

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
[GlobalClass]
public partial class EmptyMap : Node3D
{
    /// <summary>Octree world scale (worldsize = 1 &lt;&lt; WorldScale). Clamped to 10..16 by the NewMap op.</summary>
    [Export] public int WorldScale { get; set; } = 10; // worldsize 1024; SplitOcta is covered by tests

    /// <summary>Chunk size for the mesher.</summary>
    [Export] public int ChunkSize { get; set; } = 128;

    public Octa? Octa { get; set; }
    public WorldCommandSink? Sink { get; set; }
    public WorldChunkRenderer? Renderer { get; set; }

    /// <summary>
    /// Reuse a built-once canonical world (octree + meshes) across instances by
    /// cloning the octree and sharing the meshes, instead of rebuilding and
    /// re-meshing from scratch. Every <see cref="EmptyMap"/> builds the same world,
    /// so the seed is deterministic; the clone keeps mutations instance-local. This
    /// matters in tests, where dozens of fresh maps otherwise pay the full build +
    /// mesh cost each. Meshes are shared read-only (Godot resources), which is
    /// safe because the seed meshes are never mutated.
    /// </summary>
    public static bool UseSeedCache { get; set; }

    private static readonly Dictionary<(int Scale, int Chunk), (Octa Seed, ChunkedWorldMesh Mesh)> SeedCache = new();

    public override void _Ready()
    {
        RotationDegrees = new Vector3(-90, 0, 0); // Z-up -> Y-up

        if (UseSeedCache && SeedCache.TryGetValue((WorldScale, ChunkSize), out var seed))
        {
            // Fast path: clone the cached octree and reuse its meshes.
            var cachedOcta = seed.Seed.Clone();
            Octa = cachedOcta;
            Sink = new WorldCommandSink(new OctaWorld(cachedOcta));
            CreateRenderer(cachedOcta, seed.Mesh);
            LinkChildEditors(cachedOcta);
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

        // Carve a hollow room so the prototype has interior space to fly around.
        int size = octa.WorldSize;
        int inner = size / 2;
        var sel = new Block3(new Vector3I(inner / 2, inner / 2, inner / 2),
                             new Vector3I(inner / 8, inner / 8, inner / 8), 8, 0);
        OctaEdit.DeleteCube(octa, sel);

        ChunkedWorldMesh? prebuilt = null;
        if (UseSeedCache)
        {
            var mesher = new OctaMesher(octa, null);
            var mesh = mesher.BuildChunked(ChunkSize);
            SeedCache[(WorldScale, ChunkSize)] = (octa.Clone(), mesh);
            prebuilt = mesh;
        }

        CreateRenderer(octa, prebuilt);
        LinkChildEditors(octa);

        GD.Print($"[emptymap] worldsize={size} chunks={Renderer!.ChunkCount} tris={Renderer.TriangleCount}");
    }

    private void CreateRenderer(Octa octa, ChunkedWorldMesh? prebuilt)
    {
        Renderer = new WorldChunkRenderer
        {
            Name = "WorldMesh",
            ChunkSize = ChunkSize,
            BudgetPerFrame = 4,
        };
        AddChild(Renderer);
        Renderer.Setup(octa, materials: null, prebuilt);
    }

    // Hand the world to any child editor controller (it draws in this Z-up space).
    private void LinkChildEditors(Octa octa)
    {
        foreach (var child in GetChildren())
            if (child is EditorController ec)
            {
                ec.Octa = octa;
                ec.Sink = Sink;
                ec.Renderer = Renderer;
            }
    }

}
