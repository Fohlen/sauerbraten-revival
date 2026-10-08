using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.World.Save;

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
public partial class MapLevel : Node3D
{
    /// <summary>External reference to the base octree snapshot (<c>world.res</c>).</summary>
    [Export] public WorldResource? World { get; set; }

    /// <summary>External reference to the command history (<c>history.res</c>); may be null.</summary>
    [Export] public HistoryResource? History { get; set; }

    /// <summary>Chunk size for the mesher.</summary>
    [Export] public int ChunkSize { get; set; } = 512;

    public Octa? Octa { get; set; }
    public WorldCommandSink? Sink { get; set; }
    public WorldChunkRenderer? Renderer { get; set; }

    /// <summary>The loaded snapshot (octree + entities).</summary>
    public WorldSnapshot? Snapshot { get; private set; }

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
        Sink = new WorldCommandSink(new OctaWorld(octa));

        // Replay the saved history tail on top of the base snapshot (§4 strategy 2).
        if (History != null)
            foreach (var cmd in History.ToCommands()) Sink.Receive(cmd);

        Renderer = new WorldChunkRenderer
        {
            Name = "WorldMesh",
            ChunkSize = ChunkSize,
            BudgetPerFrame = 4,
        };
        AddChild(Renderer);
        Renderer.Setup(octa, materials: null);

        // Hand the world to any child editor controller (same contract as EmptyMap).
        foreach (var child in GetChildren())
            if (child is Editor.EditorController ec)
            {
                ec.Octa = octa;
                ec.Sink = Sink;
                ec.Renderer = Renderer;
            }

        GD.Print($"[maplevel] worldsize={octa.WorldSize} chunks={Renderer.ChunkCount} tris={Renderer.TriangleCount} entities={Entities.Count}");
    }
}
