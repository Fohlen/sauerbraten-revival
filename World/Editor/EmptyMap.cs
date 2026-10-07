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

    public Octa? Octa { get; private set; }
    public WorldCommandSink? Sink { get; private set; }
    public WorldChunkRenderer? Renderer { get; private set; }

    public override void _Ready()
    {
        RotationDegrees = new Vector3(-90, 0, 0); // Z-up -> Y-up

        // A fresh octree; the actual "new map" is issued as a WorldOp through the
        // sink, exactly as the editor will (world.cpp emptymap).
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

        Renderer = new WorldChunkRenderer
        {
            Name = "WorldMesh",
            ChunkSize = ChunkSize,
            BudgetPerFrame = 4,
        };
        AddChild(Renderer);
        Renderer.Setup(octa, materials: null);

        // Hand the world to any child editor controller (it draws in this Z-up space).
        foreach (var child in GetChildren())
            if (child is EditorController ec)
            {
                ec.Octa = octa;
                ec.Sink = Sink;
                ec.Renderer = Renderer;
            }

        GD.Print($"[emptymap] worldsize={size} chunks={Renderer.ChunkCount} tris={Renderer.TriangleCount}");
    }

}
