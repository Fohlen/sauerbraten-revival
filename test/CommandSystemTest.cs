using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Network;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the M3 command system: <see cref="CommandCodec"/> round-trips,
/// <see cref="CommandSink"/> apply/undo/redo, and the §5.5 shared-world undo
/// rules (own commands only; rejected undos are discarded).
/// </summary>
public class CommandSystemTest : GoTestBase
{
    public CommandSystemTest(Node testScene) : base(testScene) { }

    private static (CommandSink Sink, Octa Octa) SmallWorld(int worldscale = 6)
    {
        var octa = new Octa(worldscale);
        var sink = new CommandSink(new OctaWorld(octa));
        return (sink, octa);
    }

    private static ulong Checksum(Octa octa) => OgzGeometry.Compute(octa.WorldRoot).Checksum;

    /// <summary>
    /// A canonical, subdivision-invariant view of the world: sample every leaf's
    /// containing cube at a fixed grid and record its observable state. Unlike
    /// <see cref="OgzGeometry"/> (which hashes octree *structure*), this is what undo
    /// restores — equivalent octrees can differ structurally while describing the
    /// same solid volume (see the notebook entry on LookupCube subdivision).
    /// </summary>
    private static string ContentSnapshot(Octa octa)
    {
        // Hash to a short digest so assertion failures are readable.
        ulong h = 14695981039346656037UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        int step = 8;
        for (int z = 0; z < octa.WorldSize; z += step)
            for (int y = 0; y < octa.WorldSize; y += step)
                for (int x = 0; x < octa.WorldSize; x += step)
                {
                    var c = OctaEdit.LookupCube(octa, new Vector3I(x, y, z), 0, out _, out _);
                    if (c == null) continue;
                    Mix((ulong)(c.IsEntirelySolid() ? 2 : c.IsEmpty() ? 1 : 3));
                    Mix(c.Material);
                    for (int f = 0; f < 6; f++) Mix(c.Texture[f]);
                }
        return h.ToString("x16");
    }

    private static SelectionPayload Cell(Vector3I origin, int grid) =>
        new(origin, new Vector3I(1, 1, 1), grid, 0);

    // ---- codec round-trips ----

    [Test]
    public void CodecRoundTripsDeleteCube()
    {
        var cmd = new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(8, 0, 0), 8)));
        var back = CommandCodec.Decode(CommandCodec.Encode(cmd));
        TestAssert.Equal(WorldOp.DeleteCube, back.Op, "op");
        var p = (DeleteCubePayload)back.Payload;
        TestAssert.Equal(new Vector3I(8, 0, 0), p.Selection.Origin, "origin");
        TestAssert.Equal(8, p.Selection.Grid, "grid");
    }

    [Test]
    public void CodecRoundTripsFill()
    {
        var cmd = new Command(WorldOp.EditFace, new EditFacePayload(Cell(new Vector3I(0, 0, 8), 4), dir: -1, mode: 1));
        var back = (EditFacePayload)CommandCodec.Decode(CommandCodec.Encode(cmd)).Payload;
        TestAssert.Equal(-1, back.Dir, "dir");
        TestAssert.Equal(1, back.Mode, "mode");
        TestAssert.Equal(new Vector3I(0, 0, 8), back.Selection.Origin, "origin");
    }

    [Test]
    public void CodecRoundTripsSetFaceTextureAndMaterial()
    {
        var tex = new Command(WorldOp.SetFaceTexture, new SetFaceTexturePayload(Cell(new Vector3I(0, 0, 0), 8), 7, -1));
        var btex = (SetFaceTexturePayload)CommandCodec.Decode(CommandCodec.Encode(tex)).Payload;
        TestAssert.Equal(7, btex.Texture, "texture");
        TestAssert.Equal(-1, btex.Orient, "orient");

        var mat = new Command(WorldOp.SetFaceMaterial, new SetFaceMaterialPayload(Cell(new Vector3I(0, 0, 0), 8), Octa.MAT_WATER, 0x3F));
        var bmat = (SetFaceMaterialPayload)CommandCodec.Decode(CommandCodec.Encode(mat)).Payload;
        TestAssert.Equal(Octa.MAT_WATER, bmat.MaterialId, "material");
        TestAssert.Equal(0x3F, bmat.Filter, "filter");
    }

    [Test]
    public void CodecRoundTripsReplaceBlockWithCubeData()
    {
        // Build a block with a subdivided cube (children) so the recursive encoder
        // is exercised.
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        var src = new Block3(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), 4, 0);
        var block = OctaEdit.BlockCopy(w, src, 4);

        var cmd = new Command(WorldOp.ReplaceBlock, new ReplaceBlockPayload(Cell(new Vector3I(4, 0, 0), 4), block));
        var back = (ReplaceBlockPayload)CommandCodec.Decode(CommandCodec.Encode(cmd)).Payload;

        TestAssert.True(back.Block != null, "block present");
        TestAssert.Equal(block.Cubes.Length, back.Block!.Cubes.Length, "cube count");
        TestAssert.True(back.Block.Cubes[0].Children != null, "children preserved");
        TestAssert.Equal(block.Descriptor.O, back.Block.Descriptor.O, "descriptor origin");
    }

    [Test]
    public void CodecRoundTripsNewMap()
    {
        var cmd = new Command(WorldOp.NewMap, new NewMapPayload(12));
        var back = CommandCodec.Decode(CommandCodec.Encode(cmd));
        TestAssert.Equal(WorldOp.NewMap, back.Op, "op");
        TestAssert.Equal(12, ((NewMapPayload)back.Payload).Scale, "scale");
    }

    [Test]
    public void NewMapCommandBuildsFloorAndAir()
    {
        // world.cpp emptymap: bottom four octants solid (floor), top four empty.
        var (sink, octa) = SmallWorld();
        sink.Submit(new Command(WorldOp.NewMap, new NewMapPayload(10)));

        TestAssert.Equal(1024, octa.WorldSize, "worldsize = 1<<scale");
        for (int i = 0; i < 4; i++)
            TestAssert.True(octa.WorldRoot[i].IsEntirelySolid(), $"octant {i} solid (floor)");
        for (int i = 4; i < 8; i++)
            TestAssert.True(octa.WorldRoot[i].IsEmpty(), $"octant {i} empty (air)");
    }

    [Test]
    public void NewMapClampsScale()
    {
        var (sink, octa) = SmallWorld();
        sink.Submit(new Command(WorldOp.NewMap, new NewMapPayload(2))); // below 10
        TestAssert.Equal(1024, octa.WorldSize, "scale clamped up to 10");
    }

    [Test]
    public void EmptyMapPreSubdividesLargeWorlds()
    {
        // octaprobe (world.cpp splitocta): a world larger than 0x1000 is pre-subdivided
        // so no leaf exceeds 0x1000.
        var w = new Octa(14);
        w.EmptyMap(14);
        int nodes = 0, maxLeaf = 0;
        foreach (var r in w.WorldRoot) { nodes += Size(r); maxLeaf = System.Math.Max(maxLeaf, MaxLeaf(r, w.WorldSize >> 1)); }
        TestAssert.Equal(72, nodes, "nodes match engine (8 roots + 64 split children)");
        TestAssert.Equal(4096, maxLeaf, "no leaf exceeds 0x1000");
    }

    [Test]
    public void EmptyMapLeavesSmallWorldsFlat()
    {
        // octaprobe: scale 12 (worldsize 4096) is not split.
        var w = new Octa(12);
        w.EmptyMap(12);
        int nodes = 0, maxLeaf = 0;
        foreach (var r in w.WorldRoot) { nodes += Size(r); maxLeaf = System.Math.Max(maxLeaf, MaxLeaf(r, w.WorldSize >> 1)); }
        TestAssert.Equal(8, nodes, "no subdivision at 4096");
        TestAssert.Equal(2048, maxLeaf, "leaves are worldsize/2");
    }

    private static int Size(Cube c) { int n = 1; if (c.Children != null) foreach (var x in c.Children) n += Size(x); return n; }
    private static int MaxLeaf(Cube c, int size)
    {
        if (c.Children == null) return size;
        int m = 0;
        foreach (var x in c.Children) m = System.Math.Max(m, MaxLeaf(x, size >> 1));
        return m;
    }

    [Test]
    public void CodecRoundTripsRemipAndPreservesSequence()
    {
        var cmd = new Command(WorldOp.Remip, new RemipPayload(true)) { Sequence = 42, AuthorPeerId = 3 };
        var back = CommandCodec.Decode(CommandCodec.Encode(cmd));
        TestAssert.Equal(WorldOp.Remip, back.Op, "op");
        TestAssert.Equal(42UL, back.Sequence, "sequence");
        TestAssert.Equal(3, back.AuthorPeerId, "author");
        TestAssert.True(((RemipPayload)back.Payload).MipVis, "mipvis");
    }

    // ---- apply through the sink matches direct OctaEdit ----

    [Test]
    public void SinkApplyMatchesDirectEdit()
    {
        var (sink, octa) = SmallWorld();
        var direct = new Octa(6);
        var region = Cell(new Vector3I(0, 0, 0), 8);

        sink.Submit(new Command(WorldOp.DeleteCube, new DeleteCubePayload(region)));
        OctaEdit.DeleteCube(direct, region.ToBlock());

        TestAssert.Equal(Checksum(direct), Checksum(octa), "sink apply == direct op");
    }

    [Test]
    public void SinkAssignsMonotonicSequences()
    {
        var (sink, _) = SmallWorld();
        var a = sink.Submit(new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(0, 0, 0), 8))));
        var b = sink.Submit(new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(8, 0, 0), 8))));
        TestAssert.Equal(0UL, a.Command.Sequence, "first sequence");
        TestAssert.Equal(1UL, b.Command.Sequence, "second sequence");
    }

    // ---- undo / redo ----

    [Test]
    public void UndoRestoresDeletedCube()
    {
        var (sink, octa) = SmallWorld();
        string before = ContentSnapshot(octa);

        sink.Submit(new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(0, 0, 0), 8))));
        TestAssert.True(ContentSnapshot(octa) != before, "delete changed the world");

        var undone = sink.Undo();
        TestAssert.True(undone != null, "undo returned an entry");
        TestAssert.Equal(before, ContentSnapshot(octa), "undo restored the world content");
    }

    [Test]
    public void RedoReappliesUndoneEdit()
    {
        var (sink, octa) = SmallWorld();
        var cmd = new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(0, 0, 0), 8)));
        sink.Submit(cmd);
        string afterEdit = ContentSnapshot(octa);

        sink.Undo();
        var redone = sink.Redo();
        TestAssert.True(redone != null, "redo returned an entry");
        TestAssert.Equal(afterEdit, ContentSnapshot(octa), "redo restored the edited state");
    }

    [Test]
    public void UndoThenRedoThenUndoIsStable()
    {
        var (sink, octa) = SmallWorld();
        string before = ContentSnapshot(octa);
        sink.Submit(new Command(WorldOp.SetFaceTexture, new SetFaceTexturePayload(Cell(new Vector3I(0, 0, 0), 8), 5, -1)));
        string after = ContentSnapshot(octa);

        sink.Undo(); TestAssert.Equal(before, ContentSnapshot(octa), "undo -> before");
        sink.Redo(); TestAssert.Equal(after, ContentSnapshot(octa), "redo -> after");
        sink.Undo(); TestAssert.Equal(before, ContentSnapshot(octa), "undo again -> before");
    }

    [Test]
    public void NewEditClearsRedo()
    {
        var (sink, octa) = SmallWorld();
        sink.Submit(new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(0, 0, 0), 8))));
        sink.Undo();
        TestAssert.True(sink.Log.PeekRedo() != null, "redo available after undo");

        sink.Submit(new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(8, 0, 0), 8))));
        TestAssert.True(sink.Log.PeekRedo() == null, "new edit clears redo");
    }

    // ---- §5.5 shared-world rules ----

    [Test]
    public void UndoOnlyRollsBackOwnCommands()
    {
        var (sink, octa) = SmallWorld();
        // A remote peer deletes a cube — it enters the log but must not be undoable.
        sink.Receive(new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(0, 0, 0), 8)), author: 7));
        TestAssert.True(sink.Log.PeekUndo() == null, "remote command is not an undo candidate");
        TestAssert.True(sink.Undo() == null, "undo returns null with no own commands");
    }

    [Test]
    public void OwnCommandIsUndoableEvenAfterRemoteEdit()
    {
        var (sink, octa) = SmallWorld();
        sink.Submit(new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(0, 0, 0), 8))));
        ulong afterOwn = Checksum(octa);
        sink.Receive(new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(8, 0, 0), 8)), author: 7));

        var entry = sink.Log.PeekUndo();
        TestAssert.True(entry != null, "own command is undoable");
        TestAssert.Equal(0, entry!.Command.AuthorPeerId, "candidate is our own command");
    }

    [Test]
    public void CodecRoundTripsCommand()
    {
        var cmd = new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(0, 0, 0), 8)), 2);
        var back = CommandCodec.Decode(CommandCodec.Encode(cmd));
        TestAssert.Equal(WorldOp.DeleteCube, back.Op, "op preserved");
        TestAssert.Equal(2, back.AuthorPeerId, "author preserved");
    }

    [Test]
    public void RemoteCommandIsNotUndoable()
    {
        var (sink, octa) = SmallWorld();
        // A remote peer's command submits via the sink but is never undoable by local peer 0.
        sink.Submit(new Command(WorldOp.DeleteCube,
            new DeleteCubePayload(Cell(new Vector3I(0, 0, 0), 8)), author: 7));

        TestAssert.True(sink.Log.PeekUndo() == null, "remote command is not an undo candidate");
        TestAssert.True(sink.Undo() == null, "undo does not touch remote geometry");
    }

    [Test]
    public void RejectedUndoIsDiscarded()
    {
        var (sink, octa) = SmallWorld();
        sink.Submit(new Command(WorldOp.DeleteCube, new DeleteCubePayload(Cell(new Vector3I(0, 0, 0), 8))));
        TestAssert.True(sink.Log.PeekUndo() != null, "undo candidate present");

        var discarded = sink.Log.DiscardForUndo();
        TestAssert.True(discarded != null, "discarded an entry");
        TestAssert.True(sink.Log.PeekUndo() == null, "stack no longer offers it");
    }

    [Test]
    public void NetMessageRoundTripsCommands()
    {
        var sel = new SelectionPayload(new Vector3I(16, 32, 64), new Vector3I(2, 4, 8), 8, 1);
        var cmds = new List<Command>
        {
            new(WorldOp.DeleteCube, new DeleteCubePayload(sel), author: 3) { Sequence = 10 },
            new(WorldOp.EditFace, new EditFacePayload(sel, dir: 1, mode: 0), author: 3) { Sequence = 11 },
            new(WorldOp.SetFaceTexture, new SetFaceTexturePayload(sel, texture: 42, orient: 2), author: 3) { Sequence = 12 },
            new(WorldOp.Remip, new RemipPayload(mipVis: true), author: 3) { Sequence = 13 },
            new(WorldOp.AddEntity, new EntityPayload(101, 1, 100 * FixedPoint.DMF, 200 * FixedPoint.DMF, 300 * FixedPoint.DMF, 90), author: 3) { Sequence = 14 },
            new(WorldOp.SetEnvironment, new SetEnvironmentPayload
            {
                SunColor = 0xFFAA55,
                SunScale = 1.5f,
                SunYaw = 120f,
                SunPitch = 45f,
                Skybox = "skyboxes/morning",
            }, author: 3) { Sequence = 15 },
        };

        foreach (var original in cmds)
        {
            var flat = original.ToFlatCommand(timestamp: 12345);
            var back = flat.ToCommand();

            TestAssert.Equal(original.Op, back.Op, $"{original.Op} preserves Op");
            TestAssert.Equal(original.Sequence, back.Sequence, $"{original.Op} preserves Sequence");
            TestAssert.Equal(original.AuthorPeerId, back.AuthorPeerId, $"{original.Op} preserves Author");

            switch (original.Payload)
            {
                case DeleteCubePayload d:
                    var bd = (DeleteCubePayload)back.Payload;
                    TestAssert.Equal(d.Selection.Origin, bd.Selection.Origin, "DeleteCube origin matches");
                    TestAssert.Equal(d.Selection.Size, bd.Selection.Size, "DeleteCube size matches");
                    break;
                case EditFacePayload ef:
                    var bef = (EditFacePayload)back.Payload;
                    TestAssert.Equal(ef.Dir, bef.Dir, "EditFace dir matches");
                    TestAssert.Equal(ef.Mode, bef.Mode, "EditFace mode matches");
                    break;
                case SetFaceTexturePayload st:
                    var bst = (SetFaceTexturePayload)back.Payload;
                    TestAssert.Equal(st.Texture, bst.Texture, "SetFaceTexture texture matches");
                    TestAssert.Equal(st.Orient, bst.Orient, "SetFaceTexture orient matches");
                    break;
                case RemipPayload rp:
                    var brp = (RemipPayload)back.Payload;
                    TestAssert.Equal(rp.MipVis, brp.MipVis, "Remip mipvis matches");
                    break;
                case EntityPayload ep:
                    var bep = (EntityPayload)back.Payload;
                    TestAssert.Equal(ep.Id, bep.Id, "Entity id matches");
                    TestAssert.Equal(ep.Attr1, bep.Attr1, "Entity attr1 matches");
                    break;
                case SetEnvironmentPayload env:
                    var benv = (SetEnvironmentPayload)back.Payload;
                    TestAssert.Equal(env.SunColor, benv.SunColor, "SetEnvironment sun_color matches");
                    TestAssert.Equal(env.SunYaw, benv.SunYaw, "SetEnvironment sun_yaw matches");
                    TestAssert.Equal(env.Skybox, benv.Skybox, "SetEnvironment skybox matches");
                    break;
            }
        }
    }

    [Test]
    public void NetMessagePackUnpackPacket()
    {
        var sel = new SelectionPayload(new Vector3I(64, 64, 64), new Vector3I(1, 1, 1), 8, 0);
        var cmds = new List<Command>
        {
            new(WorldOp.DeleteCube, new DeleteCubePayload(sel), author: 1) { Sequence = 1 },
            new(WorldOp.Remip, new RemipPayload(false), author: 1) { Sequence = 2 },
        };

        byte[] packetBytes = NetMessage.Pack(packetId: 77, cmds, timestamp: 999);
        TestAssert.True(packetBytes.Length > 0, "Pack generated byte payload");

        var packet = NetMessage.Unpack(packetBytes);
        TestAssert.Equal(77u, packet.PacketId, "packet ID matches");
        TestAssert.Equal(2, packet.Commands?.Count ?? 0, "packet command count matches");

        var unpackedCmds = NetMessage.UnpackCommands(packetBytes);
        TestAssert.Equal(2, unpackedCmds.Count, "unpacked commands count matches");
        TestAssert.Equal(WorldOp.DeleteCube, unpackedCmds[0].Op, "first op matches");
        TestAssert.Equal(WorldOp.Remip, unpackedCmds[1].Op, "second op matches");
        TestAssert.Equal(1UL, unpackedCmds[0].Sequence, "first sequence matches");
        TestAssert.Equal(2UL, unpackedCmds[1].Sequence, "second sequence matches");
    }
}
