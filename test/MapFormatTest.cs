using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using FlatSharp;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Runtime.Formats.Ogz;
using SauerbratenRevival.World.Runtime.Formats.Save;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers M5's map-directory format (docs/editor-and-commands.md §7): save/load the
/// native format under <c>user://</c>, history replay, compaction, <c>Level.tscn</c>
/// references, and the archive content hash.
/// </summary>
public class MapFormatTest : GoTestBase
{
    public MapFormatTest(Node testScene) : base(testScene) { }

    private static string TempDir(string name)
    {
        string dir = $"user://test_maps/{name}_{System.Guid.NewGuid():N}";
        string abs = ProjectSettings.GlobalizePath(dir);
        if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
        return dir;
    }

    private static ulong Checksum(Octa octa) => OgzGeometry.Compute(octa.WorldRoot).Checksum;

    /// <summary>
    /// A subdivision-invariant content digest — sampling leaves via LookupCube at a
    /// fixed grid. Replaying a command log without the sink's pre-state capture can
    /// leave a different *structure* (LookupCube subdivides as a side effect) while
    /// describing the same solid volume, so structural checksums are the wrong
    /// primitive here (see CommandSystemTest and docs §9).
    /// </summary>
    private static ulong ContentDigest(Octa octa)
    {
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
        return h;
    }

    private static Octa EditedWorld(out List<Command> history)
    {
        var octa = new Octa(8);
        octa.EmptyMap(8);
        var sink = new CommandSink(new OctaWorld(octa));
        var cmds = new List<Command>
        {
            Command.CreateEditFace(
                new Selection(new Vector3I(64, 64, 64), new Vector3I(2, 2, 2), 8, 0), -1, 1),
            Command.CreateSetTexture(
                new Selection(new Vector3I(72, 72, 72), new Vector3I(1, 1, 1), 8, 0), 5, -1),
        };
        foreach (var c in cmds) sink.Submit(c);
        history = cmds;
        return octa;
    }

    [Test]
    public void SaveCompactedWritesWorldAndLevel()
    {
        string dir = TempDir("compact");
        var octa = EditedWorld(out _);
        MapStore.SaveCompacted(dir, octa);

        string abs = ProjectSettings.GlobalizePath(dir);
        TestAssert.True(File.Exists(Path.Combine(abs, MapStore.WorldResourceName)), "world.res written");
        TestAssert.True(File.Exists(Path.Combine(abs, MapStore.LevelSceneName)), "Level.tscn written");
        TestAssert.True(Directory.Exists(Path.Combine(abs, MapStore.MaterialsDirName)), "materials/ written");
        TestAssert.True(!File.Exists(Path.Combine(abs, MapStore.HistoryResourceName)), "compact drops history");
    }

    [Test]
    public void LoadReproducesCompactedWorld()
    {
        string dir = TempDir("reload");
        var octa = EditedWorld(out _);
        ulong expected = Checksum(octa);
        MapStore.SaveCompacted(dir, octa);

        var loaded = MapStore.Load(dir);
        TestAssert.Equal(expected, Checksum(loaded.World), "loaded world matches saved geometry");
    }

    [Test]
    public void SnapshotPlusHistoryReproducesWorld()
    {
        string dir = TempDir("history");
        // Save the base BEFORE the edits, then the edits as history.
        var baseOcta = new Octa(8);
        baseOcta.EmptyMap(8);
        var baseSnap = WorldSnapshot.Capture(baseOcta);

        var edited = EditedWorld(out var history);
        MapStore.Save(dir, baseSnap, history, compact: false);
        ulong expected = ContentDigest(edited);

        var loaded = MapStore.Load(dir);
        TestAssert.Equal(history.Count, loaded.History.Count, "history entries loaded");
        TestAssert.Equal(expected, ContentDigest(loaded.World), "base + history reproduces the edited world content");
    }

    [Test]
    public void CompactionTruncatesHistory()
    {
        string dir = TempDir("truncate");

        var edited = EditedWorld(out var history);
        // First write a non-compacted save with history...
        MapStore.Save(dir, WorldSnapshot.Capture(new Octa(8)), history, compact: false);
        TestAssert.True(File.Exists(Path.Combine(ProjectSettings.GlobalizePath(dir), MapStore.HistoryResourceName)),
            "history present");

        // ...then compact: history must be gone and the world must be the new base.
        MapStore.SaveCompacted(dir, edited);
        TestAssert.True(!File.Exists(Path.Combine(ProjectSettings.GlobalizePath(dir), MapStore.HistoryResourceName)),
            "history truncated by compaction");
        TestAssert.Equal(Checksum(edited), Checksum(MapStore.Load(dir).World), "compacted base is current");
    }

    [Test]
    public void ContentHashIsStableAndChangesOnEdit()
    {
        string a = TempDir("hash_a");
        string b = TempDir("hash_b");
        var octa = EditedWorld(out _);
        MapStore.SaveCompacted(a, octa);
        MapStore.SaveCompacted(b, octa);

        TestAssert.Equal(MapStore.ContentHash(a), MapStore.ContentHash(b), "same content -> same archive hash");

        var changed = new Octa(8);
        changed.EmptyMap(8);
        var sink = new CommandSink(new OctaWorld(changed));
        sink.Submit(Command.CreateDeleteCube(
            new Selection(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), 8, 0)));
        MapStore.SaveCompacted(a, changed);

        TestAssert.True(MapStore.ContentHash(a) != MapStore.ContentHash(b), "edit changes the archive hash");
    }

    [Test]
    public void LevelSceneReferencesDataAndNeverInlinesIt()
    {
        string withHistory = MapStore.BuildLevelSceneText("world.res", "history.res");
        TestAssert.True(withHistory.Contains("path=\"world.res\""), "world.res referenced externally");
        TestAssert.True(withHistory.Contains("path=\"history.res\""), "history.res referenced externally");

        string noHistory = MapStore.BuildLevelSceneText("world.res", "");
        TestAssert.True(!noHistory.Contains("history.res"), "no history reference when absent");
    }

    [Test]
    public void MissingWorldThrows()
    {
        string dir = TempDir("missing");
        Directory.CreateDirectory(ProjectSettings.GlobalizePath(dir));
        var threw = false;
        try { MapStore.LoadWorld(dir); }
        catch (FileNotFoundException) { threw = true; }
        TestAssert.True(threw, "loading a map without world.res fails loudly");
    }

    // ---- scene path (MapLevel) ----

    [Test]
    public void MapLevelMaterializesTheSavedWorld()
    {
        string dir = TempDir("level");
        var octa = EditedWorld(out _);
        MapStore.SaveCompacted(dir, octa);

        string abs = ProjectSettings.GlobalizePath(dir);
        var level = new MapLevel
        {
            World = ResourceLoader.Load<WorldResource>(Path.Combine(abs, MapStore.WorldResourceName),
                "", ResourceLoader.CacheMode.Replace),
            ChunkSize = 64,
        };
        // MapLevel._Ready builds the world; call Build directly so no tree is needed.
        level.Build();

        TestAssert.True(level.Octa != null, "MapLevel produced an octree");
        TestAssert.Equal(octa.WorldSize, level.Octa!.WorldSize, "worldsize from world.res");
        TestAssert.True(level.Renderer != null, "MapLevel meshed the world");
        level.Free();
    }

    [Test]
    public void MapLevelMaterializesSavedEntities()
    {
        string dir = TempDir("level_entities");
        var octa = EditedWorld(out _);
        var entities = new List<MapEntity>
        {
            new() { Type = EntityType.Light, Position = new Vector3(64, 64, 64), Attr1 = 100, Attr2 = 255, Attr3 = 255, Attr4 = 255 },
            new() { Type = EntityType.PlayerStart, Position = new Vector3(32, 32, 32), Attr1 = 90 },
            new() { Type = EntityType.Health, Position = new Vector3(48, 48, 48) },
            new() { Type = EntityType.Teleport, Position = new Vector3(10, 10, 10), Attr1 = 1 },
            new() { Type = EntityType.TeleDest, Position = new Vector3(20, 20, 20), Attr2 = 1, Attr1 = 180 },
        };
        MapStore.SaveCompacted(dir, octa, entities);

        string abs = ProjectSettings.GlobalizePath(dir);
        var level = new MapLevel
        {
            World = ResourceLoader.Load<WorldResource>(Path.Combine(abs, MapStore.WorldResourceName),
                "", ResourceLoader.CacheMode.Replace),
            ChunkSize = 64,
        };
        level.Build();

        TestAssert.Equal(5, level.Entities.Count, "all entities loaded");
        TestAssert.True(level.GetNodeOrNull("Lights") != null, "Lights root created");
        TestAssert.True(level.GetNodeOrNull("PlayerStarts") != null, "PlayerStarts root created");
        TestAssert.True(level.Items != null, "Items manager created");
        TestAssert.Equal(1, level.Teleports.Count, "Teleport created");
        TestAssert.True(level.Teleports[0].Destination != null, "Teleport linked to TeleDest");

        level.Free();
    }

    // ---- Reference-based maps (ADR 0008) ----

    [Test]
    public void FlatSharpSchemaBasicRoundTrip()
    {
        var package = new Format.MapPackage
        {
            FormatVersion = 1,
            MapName = "test_map",
            Author = "Revival",
            Compression = Format.CompressionCodec.None,
            Keyframes = new List<Format.KeyframePackage>
            {
                new()
                {
                    KeyframeIndex = 0,
                    FormatVersion = 1,
                    WorldScale = 8,
                    GitCommit = "abc1234",
                    Chunks = new List<Format.ChunkKeyframe>
                    {
                        new()
                        {
                            ChunkIndex = 0,
                            UncompressedSize = 100,
                            NodeCount = 10,
                            Data = new byte[] { 1, 2, 3, 4 },
                        }
                    }
                }
            }
        };

        byte[] buffer = new byte[Format.MapPackage.Serializer.GetMaxSize(package)];
        int bytesWritten = Format.MapPackage.Serializer.Write(buffer, package);
        TestAssert.True(bytesWritten > 0, "FlatSharp serialized MapPackage");

        var parsed = Format.MapPackage.Serializer.Parse(buffer);
        TestAssert.Equal("test_map", parsed.MapName, "MapName matches");
        TestAssert.Equal("Revival", parsed.Author, "Author matches");
        TestAssert.Equal(1, parsed.Keyframes.Count, "Keyframe count matches");
        TestAssert.Equal("abc1234", parsed.Keyframes[0].GitCommit, "GitCommit matches");
        TestAssert.Equal(1, parsed.Keyframes[0].Chunks.Count, "Chunk count matches");
        TestAssert.Equal(4, parsed.Keyframes[0].Chunks[0].Data!.Value.Length, "Chunk data length matches");
    }

    [Test]
    public void EntityCommandsAndUndoRedo()
    {
        var octa = new Octa(8);
        octa.EmptyMap(8);
        var world = new OctaWorld(octa);
        var sink = new CommandSink(world);

        var entity = new MapEntity
        {
            Id = 42,
            Type = EntityType.PlayerStart,
            Position = new Vector3(100, 100, 100),
            Attr1 = 90,
        };

        // 1. AddEntity
        sink.Submit(Command.CreateAddEntity(entity));
        TestAssert.Equal(1, world.Entities.Count, "entity added");
        TestAssert.Equal(42UL, world.Entities[0].Id, "entity ID preserved");

        // 2. EditEntity
        var edited = new MapEntity
        {
            Id = 42,
            Type = EntityType.PlayerStart,
            Position = new Vector3(200, 200, 200),
            Attr1 = 180,
        };
        sink.Submit(Command.CreateEditEntity(edited));
        TestAssert.Equal(1, world.Entities.Count, "entity count remains 1");
        TestAssert.Equal(200f, world.Entities[0].Position.X, "entity position edited");
        TestAssert.Equal((short)180, world.Entities[0].Attr1, "entity attr edited");

        // Undo edit -> restores pre-edit state
        sink.Undo();
        TestAssert.Equal(100f, world.Entities[0].Position.X, "undo restored pre-edit position");
        TestAssert.Equal((short)90, world.Entities[0].Attr1, "undo restored pre-edit attr");

        // Redo edit -> reapplies edit
        sink.Redo();
        TestAssert.Equal(200f, world.Entities[0].Position.X, "redo reapplied edited position");

        // 3. RemoveEntity
        sink.Submit(Command.CreateRemoveEntity(42));
        TestAssert.Equal(0, world.Entities.Count, "entity removed");

        // Undo remove -> restores entity
        sink.Undo();
        TestAssert.Equal(1, world.Entities.Count, "undo remove restored entity");
        TestAssert.Equal(42UL, world.Entities[0].Id, "restored entity has correct ID");
    }

    [Test]
    public void CanonicalEmptyMapGeneratesValidSnapshot()
    {
        MapStore.CreateCanonicalEmptyMap(MapStore.EmptyMapDir);
        TestAssert.True(File.Exists(ProjectSettings.GlobalizePath(MapStore.EmptyMapWorldPath)), "canonical empty_map world.res exists");

        var loadedBase = MapStore.Load(MapStore.EmptyMapDir);
        TestAssert.Equal(1, loadedBase.Snapshot.Entities.Count, "empty_map has PlayerStart entity");
    }

    [Test]
    public void KeyframeChunkRoundTrip()
    {
        var root = new Cube
        {
            Material = Octa.MAT_WATER,
            Merged = 0x12,
            Visible = 0x34,
            Children = new Cube[8],
        };
        root.Faces[0] = 0x11223344;
        root.Faces[1] = 0x55667788;
        root.Faces[2] = 0x99AABBCC;
        for (int i = 0; i < 6; i++) root.Texture[i] = (ushort)(i + 10);

        root.Ext = new CubeExt();
        root.Ext.Surfaces[0] = new SurfaceInfo { Lmid0 = 1, Lmid1 = 2, Verts = 0, NumVerts = 1 };
        root.Ext.Verts = new[]
        {
            new VertInfo { X = 1, Y = 2, Z = 3, U = 4, V = 5, Norm = 6 }
        };

        for (int i = 0; i < 8; i++)
        {
            var child = new Cube { Material = Octa.MAT_AIR };
            child.SolidFaces();
            child.Texture[0] = (ushort)(i + 1);
            root.Children[i] = child;
        }

        var chunk = KeyframePackage.SerializeChunk(root, chunkIndex: 3);
        TestAssert.Equal((byte)3, chunk.ChunkIndex, "chunk index matches");
        TestAssert.Equal(9u, chunk.NodeCount, "node count matches 1 root + 8 children");
        TestAssert.True(chunk.UncompressedSize > 0, "uncompressed size > 0");

        var back = KeyframePackage.DeserializeChunk(chunk);
        TestAssert.Equal(Octa.MAT_WATER, back.Material, "material matches");
        TestAssert.Equal((byte)0x12, back.Merged, "merged matches");
        TestAssert.Equal((byte)0x34, back.Visible, "visible matches");
        TestAssert.Equal(0x11223344u, back.Faces[0], "faces[0] matches");
        TestAssert.Equal(0x55667788u, back.Faces[1], "faces[1] matches");
        TestAssert.Equal(0x99AABBCCu, back.Faces[2], "faces[2] matches");
        TestAssert.Equal(10, back.Texture[0], "texture[0] matches");

        TestAssert.True(back.Ext != null, "Ext reconstructed");
        TestAssert.Equal((byte)1, back.Ext!.Surfaces[0].Lmid0, "surface Lmid0 matches");
        TestAssert.Equal(1, back.Ext.Verts.Length, "vert count matches");
        TestAssert.Equal((ushort)1, back.Ext.Verts[0].X, "vert X matches");

        TestAssert.True(back.Children != null, "Children reconstructed");
        TestAssert.Equal(8, back.Children!.Length, "8 children");
        TestAssert.Equal(1, back.Children[0].Texture[0], "child 0 texture matches");
    }

    [Test]
    public void KeyframePackageCaptureAndToOcta()
    {
        var octa = new Octa(10);
        octa.EmptyMap(10);

        var sel = new Selection(new Vector3I(8, 8, 8), new Vector3I(2, 2, 2), 8, 0);
        OctaEdit.DeleteCube(octa, sel.ToBlock());

        var entities = new List<MapEntity>
        {
            new() { Id = 10, Type = EntityType.PlayerStart, Position = new Vector3(16, 16, 16) }
        };
        var env = new Format.SetEnvironmentCmd { SunColor = 0xFF5500, Skybox = "sky/noon" };

        var keyframe = KeyframePackage.Capture(octa, entities, env, keyframeIndex: 0, gitCommit: "test_git_commit_sha");
        TestAssert.Equal(0u, keyframe.KeyframeIndex, "keyframe index matches");
        TestAssert.Equal(1u, keyframe.FormatVersion, "format version is 1");
        TestAssert.Equal(10, keyframe.WorldScale, "world scale is 10");
        TestAssert.Equal("test_git_commit_sha", keyframe.GitCommit, "git commit matches");
        TestAssert.Equal(8, keyframe.Chunks.Count, "chunks count is 8");
        TestAssert.Equal(1, keyframe.Entities.Count, "entities count is 1");
        TestAssert.Equal(0xFF5500u, keyframe.Environment.SunColor, "sun color matches");

        // Encode / Decode FlatSharp round-trip
        byte[] bytes = keyframe.Encode();
        TestAssert.True(bytes.Length > 0, "encoded keyframe bytes > 0");

        var decoded = KeyframePackage.Decode(bytes);
        TestAssert.Equal("test_git_commit_sha", decoded.GitCommit, "decoded git commit matches");
        TestAssert.Equal(8, decoded.Chunks.Count, "decoded chunks count is 8");

        // Reconstruct Octa
        var reconstructed = decoded.ToOcta();
        TestAssert.Equal(octa.WorldScale, reconstructed.WorldScale, "scale matches");
        TestAssert.Equal(octa.WorldSize, reconstructed.WorldSize, "size matches");
        TestAssert.Equal(octa.AllocNodes, reconstructed.AllocNodes, "allocNodes matches");

        for (int i = 0; i < 8; i++)
        {
            TestAssert.Equal(Octa.FamilySize(octa.WorldRoot[i]), Octa.FamilySize(reconstructed.WorldRoot[i]), $"chunk {i} family size matches");
        }
    }

    [Test]
    public void KeyframePackageComputeHashDistinguishesEdits()
    {
        var octa = new Octa(10);
        octa.EmptyMap(10);

        var keyframeA = KeyframePackage.Capture(octa, keyframeIndex: 0, gitCommit: "c1");
        byte[] hashA1 = keyframeA.ComputeHash();
        byte[] hashA2 = keyframeA.ComputeHash();
        TestAssert.True(System.Linq.Enumerable.SequenceEqual(hashA1, hashA2), "hash is deterministic");

        // Mutate world
        OctaEdit.DeleteCube(octa, new Block3(new Vector3I(16, 16, 16), new Vector3I(1, 1, 1), 8, 0));
        var keyframeB = KeyframePackage.Capture(octa, keyframeIndex: 0, gitCommit: "c1");
        byte[] hashB = keyframeB.ComputeHash();
        TestAssert.False(System.Linq.Enumerable.SequenceEqual(hashA1, hashB), "hash distinguishes world edits");
    }

    [Test]
    public void RemipAllInvokesKeyframeCapturePeggedToGitCommit()
    {
        var octa = new Octa(10);
        octa.EmptyMap(10);

        var keyframe = OctaEdit.RemipAll(octa, mipvis: false);
        TestAssert.True(keyframe != null, "RemipAll returned keyframe");
        TestAssert.Equal(8, keyframe!.Chunks.Count, "keyframe contains 8 chunks");
        TestAssert.Equal(10, keyframe.WorldScale, "world scale is 10");
        TestAssert.Equal(GitCommitResolver.GetHeadCommit(), keyframe.GitCommit, "pegged to clean git commit");
    }

    [Test]
    public void CommandSinkRemipCapturesKeyframeAndSquashesDeltas()
    {
        var octa = new Octa(10);
        octa.EmptyMap(10);
        var world = new OctaWorld(octa);
        var sink = new CommandSink(world);

        TestAssert.Equal(0, sink.Keyframes.Count, "initially 0 keyframes");
        TestAssert.Equal(0, sink.Log.Entries.Count, "initially 0 log entries");

        // Submit 3 edits
        var sel1 = new Selection(new Vector3I(8, 8, 8), new Vector3I(1, 1, 1), 8, 0);
        var sel2 = new Selection(new Vector3I(16, 8, 8), new Vector3I(1, 1, 1), 8, 0);
        sink.Submit(Command.CreateDeleteCube(sel1));
        sink.Submit(Command.CreateSetTexture(sel1, 5, -1));
        sink.Submit(Command.CreateDeleteCube(sel2));
        TestAssert.Equal(3, sink.Log.Entries.Count, "3 edits recorded in deltas");

        // Trigger Remip
        sink.Submit(Command.CreateRemip(false));

        // Keyframe captured, pre-remip deltas squashed so only the remip milestone remains
        TestAssert.Equal(1, sink.Keyframes.Count, "1 keyframe captured after remip");
        TestAssert.Equal(0u, sink.LatestKeyframe!.KeyframeIndex, "first keyframe index is 0");
        TestAssert.Equal(1, sink.Log.Entries.Count, "1 entry in log (the remip)");
        TestAssert.Equal(WorldOp.Remip, sink.Log.Entries[0].Command.Op, "entry is Remip");

        // Submit an edit after remip -> recorded as new delta stream
        var sel3 = new Selection(new Vector3I(24, 8, 8), new Vector3I(1, 1, 1), 8, 0);
        sink.Submit(Command.CreateDeleteCube(sel3));
        TestAssert.Equal(2, sink.Log.Entries.Count, "2 entries in log (remip + post-remip delta)");

        // Trigger second Remip
        sink.Submit(Command.CreateRemip(false));
        TestAssert.Equal(2, sink.Keyframes.Count, "2 keyframes captured");
        TestAssert.Equal(1u, sink.LatestKeyframe!.KeyframeIndex, "second keyframe index is 1");
        TestAssert.True(sink.LatestKeyframe.BaseSnapshotHash != null, "second keyframe has parent hash");
        byte[] expectedBaseHash = KeyframePackage.ComputeHash(sink.Keyframes[0]);
        TestAssert.True(System.Linq.Enumerable.SequenceEqual(expectedBaseHash, sink.LatestKeyframe.BaseSnapshotHash!.Value.ToArray()), "base snapshot hash chains correctly");
        TestAssert.Equal(1, sink.Log.Entries.Count, "pre-remip deltas squashed again; only latest remip remains");
    }

    [Test]
    public void MapStoreSavesAndLoadsMapBinFast()
    {
        string dir = TempDir("map_bin_fast");
        var octa = new Octa(10);
        octa.EmptyMap(10);

        var sel = new Selection(new Vector3I(64, 64, 64), new Vector3I(2, 2, 2), 8, 0);
        OctaEdit.DeleteCube(octa, sel.ToBlock());

        var entities = new List<MapEntity>
        {
            new() { Id = 100, Type = EntityType.PlayerStart, Position = new Vector3(64, 64, 70), Attr1 = 180 }
        };

        var trailingCmd = Command.CreateSetTexture(sel, 7, -1);
        MapStore.Save(dir, WorldSnapshot.Capture(octa, entities), history: new[] { trailingCmd }, compact: false);

        string binPath = Path.Combine(ProjectSettings.GlobalizePath(dir), MapStore.MapPackageFileName);
        TestAssert.True(File.Exists(binPath), "map.bin was written to disk");

        // Load via LoadWorld
        var snapshot = MapStore.LoadWorld(dir);
        TestAssert.Equal(10, snapshot.Scale, "scale matches");
        TestAssert.Equal(1, snapshot.Entities.Count, "entities loaded");
        TestAssert.Equal(100UL, snapshot.Entities[0].Id, "entity ID matches");

        // Load via Load (replays trailing deltas)
        var loaded = MapStore.Load(dir);
        TestAssert.Equal(10, loaded.World.WorldScale, "loaded world scale matches");
        TestAssert.Equal(1, loaded.History.Count, "trailing deltas loaded");
        TestAssert.Equal(WorldOp.SetFaceTexture, loaded.History[0].Op, "trailing op is SetFaceTexture");
        TestAssert.True(loaded.Package != null, "loaded Package is non-null");
        TestAssert.Equal(1, loaded.Package!.Keyframes.Count, "package has keyframe");
    }

    [Test]
    public void CanonicalEmptyMapCreatesMapBin()
    {
        string dir = TempDir("empty_map_bin");
        MapStore.CreateCanonicalEmptyMap(dir);

        string binPath = Path.Combine(ProjectSettings.GlobalizePath(dir), MapStore.MapPackageFileName);
        TestAssert.True(File.Exists(binPath), "canonical empty map creates map.bin");

        var loaded = MapStore.Load(dir);
        TestAssert.Equal(10, loaded.World.WorldScale, "empty map scale is 10");
        TestAssert.Equal(1, loaded.Snapshot.Entities.Count, "has playerstart entity");
    }
}
