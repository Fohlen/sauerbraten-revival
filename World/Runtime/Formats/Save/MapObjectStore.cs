using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.World.Runtime.Formats.Save;

/// <summary>Stable discriminator byte at the beginning of each content-addressed object.</summary>
public enum MapObjectType : byte
{
    OctaNode = 1,
    EntityList = 2,
    WorldTree = 3,
    Commit = 4,
    CommandBlob = 5,
}

/// <summary>
/// A true commit object in the linear history chain:
/// Identifies author, timestamp, parent commit, tree state, and command batch.
/// Its <see cref="Id"/> is the content hash (XxHash128) over its serialized payload.
/// </summary>
public sealed class MapCommit
{
    public ObjectId Id { get; set; }
    public ObjectId ParentId { get; set; }
    public ObjectId TreeId { get; set; }
    public ObjectId CommandsId { get; set; }
    public long Timestamp { get; set; }
    public string Author { get; set; } = "";
    public string Message { get; set; } = "";

    public override string ToString() => $"Commit {Id} (parent {ParentId}, tree {TreeId}): {Message}";
}

/// <summary>
/// Merkle octree and content-addressed object store (docs/adr/0008-reference-based-maps.md).
/// Provides structural sharing across commits and deduplication against a base reference map.
/// </summary>
public sealed class MapObjectStore
{
    private readonly Dictionary<ObjectId, byte[]> _objects = new();

    /// <summary>Optional fallback store (e.g. the shipped empty_map) for zero-copy base referencing.</summary>
    public MapObjectStore? BaseStore { get; set; }

    public IReadOnlyDictionary<ObjectId, byte[]> Objects => _objects;

    public int LocalObjectCount => _objects.Count;

    public void Put(ObjectId id, byte[] data)
    {
        if (id.IsEmpty) return;
        _objects[id] = data;
    }

    public bool Has(ObjectId id)
    {
        if (id.IsEmpty) return false;
        if (_objects.ContainsKey(id)) return true;
        return BaseStore != null && BaseStore.Has(id);
    }

    public byte[]? Get(ObjectId id)
    {
        if (id.IsEmpty) return null;
        if (_objects.TryGetValue(id, out var data)) return data;
        return BaseStore?.Get(id);
    }

    public byte[] GetRequired(ObjectId id) =>
        Get(id) ?? throw new KeyNotFoundException($"Object '{id}' not found in store or base store.");

    // ---- Octa Merkle tree packing / unpacking ----

    /// <summary>
    /// Pack a live octree and entity list into content-addressed Merkle objects.
    /// Unmodified subtrees already present in this store or <see cref="BaseStore"/>
    /// are not re-stored. Returns the <see cref="ObjectId"/> of the <see cref="MapObjectType.WorldTree"/>.
    /// </summary>
    public ObjectId PackTree(Octa octa, IReadOnlyList<MapEntity>? entities = null)
    {
        ObjectId rootNodeId = PackCubes(octa.WorldRoot);
        ObjectId entityListId = PackEntityList(entities);
        return PackWorldTree(octa.WorldScale, rootNodeId, entityListId);
    }

    /// <summary>
    /// Materialize a live <see cref="Octa"/> and entity list from a <see cref="MapObjectType.WorldTree"/> object.
    /// Direct O(nodes) deserialization without replaying historical commands.
    /// </summary>
    public (Octa Octa, List<MapEntity> Entities) UnpackTree(ObjectId treeId)
    {
        var (scale, rootNodeId, entityListId) = UnpackWorldTree(treeId);
        Cube[] rootCubes = UnpackCubes(rootNodeId);
        List<MapEntity> entities = UnpackEntityList(entityListId);
        int nodeCount = CountAllocNodes(rootCubes);
        var octa = new Octa(scale, 1 << scale, rootCubes, nodeCount);
        return (octa, entities);
    }

    private ObjectId PackCubes(Cube[] cubes)
    {
        Span<ObjectId> childIds = stackalloc ObjectId[8];
        for (int i = 0; i < 8; i++)
        {
            if (cubes[i].Children != null)
                childIds[i] = PackCubes(cubes[i].Children!);
            else
                childIds[i] = ObjectId.Empty;
        }

        byte[] nodeBytes = SerializeOctaNode(cubes, childIds);
        ObjectId id = ObjectId.Compute(nodeBytes);
        if (!Has(id))
            Put(id, nodeBytes);

        return id;
    }

    private Cube[] UnpackCubes(ObjectId nodeId)
    {
        byte[] data = GetRequired(nodeId);
        return DeserializeOctaNode(data);
    }

    private ObjectId PackWorldTree(int scale, ObjectId rootNodeId, ObjectId entityListId)
    {
        using var ms = new MemoryStream(37);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((byte)MapObjectType.WorldTree);
        w.Write(scale);
        Span<byte> span = stackalloc byte[16];
        rootNodeId.Write(span);
        w.Write(span);
        entityListId.Write(span);
        w.Write(span);
        w.Flush();

        byte[] bytes = ms.ToArray();
        ObjectId id = ObjectId.Compute(bytes);
        if (!Has(id)) Put(id, bytes);
        return id;
    }

    private (int Scale, ObjectId RootNodeId, ObjectId EntityListId) UnpackWorldTree(ObjectId treeId)
    {
        byte[] data = GetRequired(treeId);
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        byte type = r.ReadByte();
        if (type != (byte)MapObjectType.WorldTree)
            throw new InvalidDataException($"Expected WorldTree object, got type {type}");

        int scale = r.ReadInt32();
        var rootNodeId = ObjectId.Read(r.ReadBytes(16));
        var entityListId = ObjectId.Read(r.ReadBytes(16));
        return (scale, rootNodeId, entityListId);
    }

    // ---- Entity list packing / unpacking ----

    private ObjectId PackEntityList(IReadOnlyList<MapEntity>? entities)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((byte)MapObjectType.EntityList);

        if (entities == null || entities.Count == 0)
        {
            w.Write(0);
        }
        else
        {
            var sorted = new List<MapEntity>(entities);
            sorted.Sort((a, b) => a.Id.CompareTo(b.Id));

            w.Write(sorted.Count);
            foreach (var e in sorted)
            {
                var (x, y, z) = e.FixedPosition;
                w.Write(e.Id);
                w.Write(e.Type);
                w.Write(x); w.Write(y); w.Write(z);
                w.Write(e.Attr1); w.Write(e.Attr2); w.Write(e.Attr3); w.Write(e.Attr4); w.Write(e.Attr5);
            }
        }

        w.Flush();
        byte[] bytes = ms.ToArray();
        ObjectId id = ObjectId.Compute(bytes);
        if (!Has(id)) Put(id, bytes);
        return id;
    }

    private List<MapEntity> UnpackEntityList(ObjectId entityListId)
    {
        if (entityListId.IsEmpty) return new List<MapEntity>();
        byte[] data = GetRequired(entityListId);
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        byte type = r.ReadByte();
        if (type != (byte)MapObjectType.EntityList)
            throw new InvalidDataException($"Expected EntityList object, got type {type}");

        int count = r.ReadInt32();
        var list = new List<MapEntity>(count);
        for (int i = 0; i < count; i++)
        {
            var e = new MapEntity
            {
                Id = r.ReadUInt64(),
                Type = r.ReadByte(),
                Position = new Godot.Vector3(
                    r.ReadInt32() / (float)FixedPoint.DMF,
                    r.ReadInt32() / (float)FixedPoint.DMF,
                    r.ReadInt32() / (float)FixedPoint.DMF),
                Attr1 = r.ReadInt16(),
                Attr2 = r.ReadInt16(),
                Attr3 = r.ReadInt16(),
                Attr4 = r.ReadInt16(),
                Attr5 = r.ReadInt16(),
            };
            list.Add(e);
        }
        return list;
    }

    // ---- Commit management ----

    public MapCommit CreateCommit(ObjectId parentId, ObjectId treeId, IReadOnlyList<Command>? commands,
        string author, string message, long? timestamp = null)
    {
        ObjectId commandsId = PackCommands(commands);
        long ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((byte)MapObjectType.Commit);
        Span<byte> span = stackalloc byte[16];
        parentId.Write(span); w.Write(span);
        treeId.Write(span); w.Write(span);
        commandsId.Write(span); w.Write(span);
        w.Write(ts);
        w.Write(author ?? "");
        w.Write(message ?? "");
        w.Flush();

        byte[] bytes = ms.ToArray();
        ObjectId commitId = ObjectId.Compute(bytes);
        Put(commitId, bytes);

        return new MapCommit
        {
            Id = commitId,
            ParentId = parentId,
            TreeId = treeId,
            CommandsId = commandsId,
            Timestamp = ts,
            Author = author ?? "",
            Message = message ?? "",
        };
    }

    public MapCommit GetCommit(ObjectId commitId)
    {
        byte[] data = GetRequired(commitId);
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        byte type = r.ReadByte();
        if (type != (byte)MapObjectType.Commit)
            throw new InvalidDataException($"Expected Commit object, got type {type}");

        var parentId = ObjectId.Read(r.ReadBytes(16));
        var treeId = ObjectId.Read(r.ReadBytes(16));
        var commandsId = ObjectId.Read(r.ReadBytes(16));
        long ts = r.ReadInt64();
        string author = r.ReadString();
        string message = r.ReadString();

        return new MapCommit
        {
            Id = commitId,
            ParentId = parentId,
            TreeId = treeId,
            CommandsId = commandsId,
            Timestamp = ts,
            Author = author,
            Message = message,
        };
    }

    // ---- Command blob packing / unpacking ----

    public ObjectId PackCommands(IReadOnlyList<Command>? commands)
    {
        if (commands == null || commands.Count == 0) return ObjectId.Empty;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((byte)MapObjectType.CommandBlob);
        w.Write(commands.Count);
        foreach (var cmd in commands)
        {
            byte[] encoded = CommandCodec.Encode(cmd);
            w.Write(encoded.Length);
            w.Write(encoded);
        }
        w.Flush();

        byte[] bytes = ms.ToArray();
        ObjectId id = ObjectId.Compute(bytes);
        if (!Has(id)) Put(id, bytes);
        return id;
    }

    public List<Command> GetCommands(ObjectId commandsId)
    {
        if (commandsId.IsEmpty) return new List<Command>();

        byte[] data = GetRequired(commandsId);
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        byte type = r.ReadByte();
        if (type != (byte)MapObjectType.CommandBlob)
            throw new InvalidDataException($"Expected CommandBlob object, got type {type}");

        int count = r.ReadInt32();
        var list = new List<Command>(count);
        for (int i = 0; i < count; i++)
        {
            int len = r.ReadInt32();
            byte[] cmdBytes = r.ReadBytes(len);
            list.Add(CommandCodec.Decode(cmdBytes));
        }
        return list;
    }

    // ---- Garbage Collection ----

    /// <summary>
    /// Identify all reachable objects from a set of root commit/tree IDs.
    /// Used by <see cref="MapStore.CollectGarbage"/> to prune orphaned edits.
    /// </summary>
    public HashSet<ObjectId> CollectReachableObjects(IEnumerable<ObjectId> roots)
    {
        var visited = new HashSet<ObjectId>();
        var queue = new Queue<ObjectId>(roots);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (id.IsEmpty || !visited.Add(id)) continue;

            byte[]? data = Get(id);
            if (data == null || data.Length == 0) continue;

            var objType = (MapObjectType)data[0];
            using var ms = new MemoryStream(data, writable: false);
            using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            r.ReadByte(); // type

            switch (objType)
            {
                case MapObjectType.Commit:
                    {
                        var parentId = ObjectId.Read(r.ReadBytes(16));
                        var treeId = ObjectId.Read(r.ReadBytes(16));
                        var commandsId = ObjectId.Read(r.ReadBytes(16));
                        if (!parentId.IsEmpty) queue.Enqueue(parentId);
                        if (!treeId.IsEmpty) queue.Enqueue(treeId);
                        if (!commandsId.IsEmpty) queue.Enqueue(commandsId);
                        break;
                    }
                case MapObjectType.WorldTree:
                    {
                        r.ReadInt32(); // scale
                        var rootNodeId = ObjectId.Read(r.ReadBytes(16));
                        var entityListId = ObjectId.Read(r.ReadBytes(16));
                        if (!rootNodeId.IsEmpty) queue.Enqueue(rootNodeId);
                        if (!entityListId.IsEmpty) queue.Enqueue(entityListId);
                        break;
                    }
                case MapObjectType.OctaNode:
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            byte flags = r.ReadByte();
                            if ((flags & 0x01) != 0) // HasChildren
                            {
                                var childId = ObjectId.Read(r.ReadBytes(16));
                                if (!childId.IsEmpty) queue.Enqueue(childId);
                            }
                            else
                            {
                                r.ReadBytes(12 + 12); // faces (3 uints) + textures (6 ushorts)
                                if ((flags & 0x02) != 0) r.ReadUInt16(); // Material
                                if ((flags & 0x04) != 0) SkipSurfaces(r);
                            }
                        }
                        break;
                    }
            }
        }

        return visited;
    }

    public void PruneUnreachable(IEnumerable<ObjectId> roots)
    {
        var reachable = CollectReachableObjects(roots);
        var toRemove = new List<ObjectId>();
        foreach (var id in _objects.Keys)
        {
            if (!reachable.Contains(id))
                toRemove.Add(id);
        }
        foreach (var id in toRemove)
            _objects.Remove(id);
    }

    // ---- OctaNode byte serialization ----

    private static byte[] SerializeOctaNode(Cube[] cubes, ReadOnlySpan<ObjectId> childIds)
    {
        using var ms = new MemoryStream(128);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((byte)MapObjectType.OctaNode);

        Span<byte> span = stackalloc byte[16];
        for (int i = 0; i < 8; i++)
        {
            var c = cubes[i];
            bool hasChildren = c.Children != null && !childIds[i].IsEmpty;
            byte flags = 0;
            if (hasChildren) flags |= 0x01;
            if (c.Material != Octa.MAT_AIR) flags |= 0x02;
            if (c.Ext != null) flags |= 0x04;

            w.Write(flags);
            if (hasChildren)
            {
                childIds[i].Write(span);
                w.Write(span);
            }
            else
            {
                w.Write(c.Faces[0]);
                w.Write(c.Faces[1]);
                w.Write(c.Faces[2]);
                for (int t = 0; t < 6; t++) w.Write(c.Texture[t]);
                if ((flags & 0x02) != 0) w.Write(c.Material);
                if ((flags & 0x04) != 0) WriteSurfaces(w, c.Ext!);
            }
        }

        w.Flush();
        return ms.ToArray();
    }

    private Cube[] DeserializeOctaNode(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        byte type = r.ReadByte();
        if (type != (byte)MapObjectType.OctaNode)
            throw new InvalidDataException($"Expected OctaNode object, got type {type}");

        var cubes = new Cube[8];
        for (int i = 0; i < 8; i++)
        {
            var c = new Cube();
            cubes[i] = c;
            byte flags = r.ReadByte();
            if ((flags & 0x01) != 0)
            {
                var childId = ObjectId.Read(r.ReadBytes(16));
                c.Children = UnpackCubes(childId);
            }
            else
            {
                c.Faces[0] = r.ReadUInt32();
                c.Faces[1] = r.ReadUInt32();
                c.Faces[2] = r.ReadUInt32();
                for (int t = 0; t < 6; t++) c.Texture[t] = r.ReadUInt16();
                if ((flags & 0x02) != 0) c.Material = r.ReadUInt16();
                if ((flags & 0x04) != 0) c.Ext = ReadSurfaces(r);
            }
        }
        return cubes;
    }

    private static void WriteSurfaces(BinaryWriter w, CubeExt ext)
    {
        for (int f = 0; f < 6; f++)
        {
            var s = ext.Surfaces[f];
            w.Write(s.Lmid0);
            w.Write(s.Lmid1);
            w.Write(s.Verts);
            w.Write(s.NumVerts);
        }
        w.Write((ushort)ext.Verts.Length);
        foreach (var v in ext.Verts)
        {
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            w.Write(v.U); w.Write(v.V); w.Write(v.Norm);
        }
    }

    private static CubeExt ReadSurfaces(BinaryReader r)
    {
        var ext = new CubeExt();
        for (int f = 0; f < 6; f++)
        {
            ext.Surfaces[f] = new SurfaceInfo
            {
                Lmid0 = r.ReadByte(),
                Lmid1 = r.ReadByte(),
                Verts = r.ReadByte(),
                NumVerts = r.ReadByte(),
            };
        }
        int count = r.ReadUInt16();
        var verts = new VertInfo[count];
        for (int i = 0; i < count; i++)
        {
            verts[i] = new VertInfo
            {
                X = r.ReadUInt16(),
                Y = r.ReadUInt16(),
                Z = r.ReadUInt16(),
                U = r.ReadUInt16(),
                V = r.ReadUInt16(),
                Norm = r.ReadUInt16(),
            };
        }
        ext.Verts = verts;
        return ext;
    }

    private static void SkipSurfaces(BinaryReader r)
    {
        r.ReadBytes(6 * 4);
        int count = r.ReadUInt16();
        r.ReadBytes(count * 12);
    }

    private static int CountAllocNodes(Cube[] cubes)
    {
        int count = 1;
        foreach (var c in cubes)
            if (c.Children != null) count += CountAllocNodes(c.Children);
        return count;
    }
}
