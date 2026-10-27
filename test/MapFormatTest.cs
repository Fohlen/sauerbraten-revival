using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
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
            new(WorldOp.EditFace, new EditFacePayload(
                new SelectionPayload(new Vector3I(64, 64, 64), new Vector3I(2, 2, 2), 8, 0), -1, 1)),
            new(WorldOp.SetFaceTexture, new SetFaceTexturePayload(
                new SelectionPayload(new Vector3I(72, 72, 72), new Vector3I(1, 1, 1), 8, 0), 5, -1)),
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
        sink.Submit(new Command(WorldOp.DeleteCube, new DeleteCubePayload(
            new SelectionPayload(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), 8, 0))));
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
    public void ObjectIdRoundTripAndHashing()
    {
        byte[] sample = System.Text.Encoding.UTF8.GetBytes("sauerbraten-revival-merkle-node");
        var id = ObjectId.Compute(sample);
        string hex = id.ToString();
        TestAssert.Equal(32, hex.Length, "hex string is 32 characters (128 bits)");

        var parsed = ObjectId.Parse(hex);
        TestAssert.Equal(id, parsed, "Parse round-trips correctly");
        TestAssert.True(ObjectId.TryParse(hex, out var tryParsed) && tryParsed == id, "TryParse works");
    }

    [Test]
    public void MerkleStoreStructuralSharingAndDedup()
    {
        var octa1 = new Octa(8);
        octa1.EmptyMap(8);

        var store = new MapObjectStore();
        var tree1 = store.PackTree(octa1);
        int initialObjectCount = store.LocalObjectCount;
        TestAssert.True(initialObjectCount > 0, "packed initial world objects");

        // Packing the exact same world again should NOT allocate any new objects
        var tree1Again = store.PackTree(octa1);
        TestAssert.Equal(tree1, tree1Again, "identical world produces identical tree ObjectId");
        TestAssert.Equal(initialObjectCount, store.LocalObjectCount, "structural deduplication allocates zero new objects");

        // Clone world and edit a single cube
        var octa2 = octa1.Clone();
        OctaEdit.DeleteCube(octa2, new Block3(new Vector3I(64, 64, 64), new Vector3I(1, 1, 1), 8, 0));

        var tree2 = store.PackTree(octa2);
        TestAssert.True(tree1 != tree2, "edited world produces distinct tree ObjectId");
        // Only the nodes along the modified branch (depth ~6-8) are added, NOT the thousands of untouched nodes
        int added = store.LocalObjectCount - initialObjectCount;
        TestAssert.True(added < 15, $"only modified branch nodes are added (got {added})");
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
        sink.Submit(new Command(WorldOp.AddEntity, new EntityPayload(entity)));
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
        sink.Submit(new Command(WorldOp.EditEntity, new EntityPayload(edited)));
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
        sink.Submit(new Command(WorldOp.RemoveEntity, new EntityPayload { Id = 42 }));
        TestAssert.Equal(0, world.Entities.Count, "entity removed");

        // Undo remove -> restores entity
        sink.Undo();
        TestAssert.Equal(1, world.Entities.Count, "undo remove restored entity");
        TestAssert.Equal(42UL, world.Entities[0].Id, "restored entity has correct ID");
    }

    [Test]
    public void CanonicalEmptyMapAndZeroCopyCheckout()
    {
        MapStore.CreateCanonicalEmptyMap(MapStore.EmptyMapDir);
        TestAssert.True(File.Exists(ProjectSettings.GlobalizePath(MapStore.EmptyMapWorldPath)), "canonical empty_map world.res exists");

        var loadedBase = MapStore.Load(MapStore.EmptyMapDir);
        TestAssert.True(loadedBase.HeadCommit != null, "empty_map has HEAD commit");
        TestAssert.Equal(1, loadedBase.Snapshot.Entities.Count, "empty_map has PlayerStart entity");

        // Now initialize a child map referencing empty_map
        string childDir = TempDir("child_map");
        MapStore.InitFromReference(childDir, baseRef: MapStore.EmptyMapDir);

        var childHistory = MapStore.LoadHistoryResource(childDir);
        TestAssert.True(childHistory != null, "child history.res exists");
        TestAssert.Equal(0, childHistory!.Objects.Count, "zero-copy: zero octree objects stored locally in child map");

        // Loading child map reconstructs the exact empty_map world
        var loadedChild = MapStore.Load(childDir);
        TestAssert.Equal(Checksum(loadedBase.World), Checksum(loadedChild.World), "child map matches base map geometry exactly");
        TestAssert.Equal(1, loadedChild.Snapshot.Entities.Count, "child map entity list matches");
    }

    [Test]
    public void CommitChainCheckoutAndVerify()
    {
        string emptyDir = TempDir("empty_base");
        MapStore.CreateCanonicalEmptyMap(emptyDir);

        string childDir = TempDir("chain_map");
        MapStore.InitFromReference(childDir, baseRef: emptyDir);

        var loaded = MapStore.Load(childDir);
        var octa = loaded.World;
        var sink = new CommandSink(new OctaWorld(octa, loaded.Snapshot.Entities));

        // Submit edit
        var editCmd = new Command(WorldOp.DeleteCube, new DeleteCubePayload(
            new SelectionPayload(new Vector3I(64, 64, 64), new Vector3I(2, 2, 2), 8, 0)));
        sink.Submit(editCmd);

        // Commit to child map
        var commit = MapStore.Commit(childDir, octa, sink.World.Entities, new[] { editCmd }, "TestAuthor", "Add ramp");

        // Verify commit: replaying commands from parent reproduces the tree exactly
        bool valid = MapStore.VerifyCommit(childDir, commit.Id);
        TestAssert.True(valid, "replaying commit commands reproduces the Merkle tree");

        // Checkout previous commit (base)
        var (checkedOutOcta, _) = MapStore.Checkout(childDir, commit.ParentId);
        var baseLoaded = MapStore.Load(emptyDir);
        TestAssert.Equal(Checksum(baseLoaded.World), Checksum(checkedOutOcta), "checkout restores parent geometry without command replay");

        // Checkout HEAD commit again
        var (headOcta, _) = MapStore.Checkout(childDir, commit.Id);
        TestAssert.Equal(Checksum(octa), Checksum(headOcta), "checkout restores committed edit");
    }

    [Test]
    public void GarbageCollectionPrunesOrphanedObjects()
    {
        string dir = TempDir("gc_test");
        var octa = new Octa(8);
        octa.EmptyMap(8);

        var store = new MapObjectStore();
        var tree1 = store.PackTree(octa);
        var commit1 = store.CreateCommit(ObjectId.Empty, tree1, null, "Author", "Init");

        // Create an orphaned commit with a modified world
        var octa2 = octa.Clone();
        OctaEdit.DeleteCube(octa2, new Block3(new Vector3I(64, 64, 64), new Vector3I(1, 1, 1), 8, 0));
        var tree2 = store.PackTree(octa2);
        var orphanedCommit = store.CreateCommit(commit1.Id, tree2, null, "Author", "Orphan");

        int countBefore = store.LocalObjectCount;

        // GC only keeping commit1
        store.PruneUnreachable(new[] { commit1.Id });
        int countAfter = store.LocalObjectCount;

        TestAssert.True(countAfter < countBefore, $"GC pruned orphaned objects (before: {countBefore}, after: {countAfter})");
        TestAssert.True(store.Has(commit1.Id), "kept active commit");
        TestAssert.True(!store.Has(orphanedCommit.Id), "pruned orphaned commit");
    }
}
