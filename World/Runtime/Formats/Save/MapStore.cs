using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Godot;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Network;

namespace SauerbratenRevival.World.Runtime.Formats.Save;

/// <summary>
/// Loads and saves a map in the native format (docs/editor-and-commands.md §7,
/// vision.md §9). A map is a **directory of external resources**:
///
/// <code>
/// map/
/// ├── Level.tscn            # scene: mode config, environment; REFS the data below
/// ├── world.res             # base octree snapshot (canonical, versioned)
/// ├── history.res           # appended edit commands since world.res (optional)
/// └── materials/            # material manifest
/// </code>
///
/// The directory is the unit of distribution: the content hash is computed over the
/// **directory contents** in a fixed order (the archive's contents), matching how
/// Workshop/parity hashes the packaged archive (§7.4).
///
/// Editing writes to <c>user://</c> (writable at runtime); shipped maps live in
/// <c>res://</c> (read-only once exported) (§7.4).
/// </summary>
public static class MapStore
{
    public const string LevelSceneName = "Level.tscn";
    public const string WorldResourceName = "world.res";
    public const string HistoryResourceName = "history.res";
    public const string MaterialsDirName = "materials";
    public const string ManifestResourceName = "manifest.res";
    public const string EmptyMapDir = "res://resources/maps/empty_map";
    public const string EmptyMapWorldPath = "res://resources/maps/empty_map/world.res";

    /// <summary>Read the base world snapshot from a map directory.</summary>
    public static WorldSnapshot LoadWorld(string mapDir)
    {
        string path = Globalize(Path.Combine(mapDir, WorldResourceName));
        if (!ResourceLoader.Exists(path) && !File.Exists(path))
            throw new FileNotFoundException($"No {WorldResourceName} in '{mapDir}'.", path);
        var res = ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace);
        if (res == null) throw new InvalidDataException($"Failed to load {path}.");

        // The typed cast works when Godot resolved the C# script; when it did not
        // (headless/CLI contexts), read the exported blob property generically.
        if (res is WorldResource typed) return typed.ToSnapshot();
        if (res.Get("Data").VariantType == Variant.Type.PackedByteArray)
            return WorldSnapshot.Decode(res.Get("Data").As<byte[]>());
        throw new InvalidDataException($"{path} is not a world snapshot.");
    }

    /// <summary>Load the HistoryResource from a map directory, or null if absent.</summary>
    public static HistoryResource? LoadHistoryResource(string mapDir)
    {
        string path = Globalize(Path.Combine(mapDir, HistoryResourceName));
        if (!ResourceLoader.Exists(path) && !File.Exists(path)) return null;
        var res = ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace);
        if (res == null) return null;
        if (res is HistoryResource typed) return typed;

        // Fall back to untyped dictionary reflection (headless/CLI contexts).
        var historyRes = new HistoryResource();
        var baseRef = res.Get("BaseRef");
        if (baseRef.VariantType == Variant.Type.String) historyRes.BaseRef = baseRef.AsString();
        var expectedBaseTreeId = res.Get("ExpectedBaseTreeId");
        if (expectedBaseTreeId.VariantType == Variant.Type.String) historyRes.ExpectedBaseTreeId = expectedBaseTreeId.AsString();
        var headCommitId = res.Get("HeadCommitId");
        if (headCommitId.VariantType == Variant.Type.String) historyRes.HeadCommitId = headCommitId.AsString();
        var commitChain = res.Get("CommitChain");
        if (commitChain.VariantType == Variant.Type.Array)
            foreach (var item in commitChain.As<Godot.Collections.Array>()) historyRes.CommitChain.Add(item.AsString());
        var objects = res.Get("Objects");
        if (objects.VariantType == Variant.Type.Dictionary)
        {
            var dict = objects.As<Godot.Collections.Dictionary>();
            foreach (var key in dict.Keys)
                historyRes.Objects[key.AsString()] = dict[key].As<byte[]>();
        }
        var commands = res.Get("Commands");
        if (commands.VariantType == Variant.Type.Array)
            foreach (var blob in commands.As<Godot.Collections.Array>())
                historyRes.Commands.Add(blob.As<byte[]>());

        return historyRes;
    }

    /// <summary>Read the command history from a map directory (empty if the file is absent).</summary>
    public static List<Command> LoadHistory(string mapDir)
    {
        var historyRes = LoadHistoryResource(mapDir);
        if (historyRes != null) return historyRes.ToCommands();
        return new List<Command>();
    }

    /// <summary>
    /// Result of loading a map's data: a live octree plus the session command log
    /// needed to reach the saved state. Applying <see cref="History"/> to
    /// <see cref="World"/> reproduces the map.
    /// </summary>
    public sealed class LoadedMap
    {
        public Octa World = null!;
        public WorldSnapshot Snapshot = null!;
        public List<Command> History = new();
    }

    /// <summary>Load a map: materializes world snapshot and replays command history.</summary>
    public static LoadedMap Load(string mapDir, OctaWorld? world = null)
    {
        var snapshot = LoadWorld(mapDir);
        var octa = snapshot.ToOcta();
        var target = world ?? new OctaWorld(octa, snapshot.Entities);
        var history = LoadHistory(mapDir);
        foreach (var cmd in history) target.Apply(cmd);
        return new LoadedMap
        {
            World = octa,
            Snapshot = snapshot,
            History = history,
        };
    }

    /// <summary>
    /// Generate and dump the canonical empty_map base snapshot to disk (res://resources/maps/empty_map/).
    /// </summary>
    public static void CreateCanonicalEmptyMap(string targetDir = EmptyMapDir)
    {
        string dir = Globalize(targetDir);
        Directory.CreateDirectory(dir);

        var octa = new Octa(10);
        octa.EmptyMap(10);
        int size = octa.WorldSize;
        int inner = size / 2;
        var sel = new Block3(new Vector3I(inner / 2, inner / 2, inner / 2),
                             new Vector3I(inner / 8, inner / 8, inner / 8), 8, 0);
        OctaEdit.DeleteCube(octa, sel);

        var entities = new List<MapEntity>
        {
            new MapEntity
            {
                Id = 1,
                Type = EntityType.PlayerStart,
                Position = new Vector3(512, 512, 514),
                Attr1 = 0, Attr2 = 0, Attr3 = 0, Attr4 = 0, Attr5 = 0,
            }
        };

        SaveCompacted(targetDir, octa, entities);
    }

    /// <summary>
    /// Write a map directory: a fresh <c>world.res</c> snapshot and an (optional)
    /// <c>history.res</c>. When <paramref name="compact"/> is true the world is
    /// snapshotted as-is and no history is written — the compaction path (§7.3).
    /// When false, the caller's history is written alongside the snapshot.
    ///
    /// When <paramref name="manifest"/> is supplied it is written to
    /// <c>materials/manifest.res</c>, making the directory self-describing; a null
    /// manifest (editor maps with no imported resources) leaves the dir empty.
    /// </summary>
    public static void Save(string mapDir, WorldSnapshot snapshot, IReadOnlyList<Command>? history,
        bool compact = true, ManifestData? manifest = null)
    {
        string dir = Globalize(mapDir);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, MaterialsDirName));

        var worldRes = WorldResource.FromSnapshot(snapshot);
        Error err = ResourceSaver.Save(worldRes, Path.Combine(dir, WorldResourceName));
        if (err != Error.Ok) throw new IOException($"Saving {WorldResourceName} failed: {err}");

        if (manifest != null)
        {
            var manifestRes = ResourceManifest.FromData(manifest);
            err = ResourceSaver.Save(manifestRes, Path.Combine(dir, MaterialsDirName, ManifestResourceName));
            if (err != Error.Ok) throw new IOException($"Saving {ManifestResourceName} failed: {err}");
        }

        string historyPath = Path.Combine(dir, HistoryResourceName);
        if (compact || history == null || history.Count == 0)
        {
            // Compaction: the snapshot is authoritative; drop any stale history.
            if (File.Exists(historyPath)) File.Delete(historyPath);
        }
        else
        {
            var historyRes = HistoryResource.FromCommands(history);
            err = ResourceSaver.Save(historyRes, historyPath);
            if (err != Error.Ok) throw new IOException($"Saving {HistoryResourceName} failed: {err}");
        }

        WriteLevelScene(dir);
    }

    /// <summary>Convenience: snapshot a live octree and save it, compacted.</summary>
    public static void SaveCompacted(string mapDir, Octa octa, IEnumerable<MapEntity>? entities = null,
        ManifestData? manifest = null) =>
        Save(mapDir, WorldSnapshot.Capture(octa, entities), history: null, compact: true, manifest: manifest);

    /// <summary>Load the resource manifest from a map directory, or null if the map has none.</summary>
    public static ManifestData? LoadManifest(string mapDir)
    {
        string path = Path.Combine(Globalize(mapDir), MaterialsDirName, ManifestResourceName);
        if (!ResourceLoader.Exists(path) && !File.Exists(path)) return null;
        var res = ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace);
        if (res == null) throw new InvalidDataException($"Failed to load {path}.");

        if (res is ResourceManifest typed) return typed.ToData();

        // Fall back to the exported blob property (headless/CLI contexts).
        if (res.Get("Data").VariantType == Variant.Type.PackedByteArray)
            return ManifestData.Decode(res.Get("Data").As<byte[]>());
        throw new InvalidDataException($"{path} is not a resource manifest.");
    }

    /// <summary>
    /// Content hash of a map directory — the unit of Workshop distribution and
    /// multiplayer parity (vision.md §12). It hashes the **semantic content**
    /// (the decoded snapshot and command history), not the raw <c>.res</c> framing:
    /// Godot embeds resource paths/uids in the framing, so raw bytes differ across
    /// save locations even for identical worlds (docs §5.1 parity caveat). This is
    /// stable under any re-save producing the same logical content.
    /// </summary>
    public static string ContentHash(string mapDir)
    {
        string dir = Globalize(mapDir);
        using var sha = SHA256.Create();

        // World snapshot: its canonical content hash covers the octree + entities.
        var snapshot = LoadWorld(mapDir);
        Mix(sha, snapshot.Hash());

        // Command history, in order (base + tail fully determine the session world).
        foreach (var cmd in LoadHistory(mapDir)) Mix(sha, cmd.Sequence, NetMessage.Encode(cmd));

        // Material manifest files, sorted, so a manifest change also changes the hash.
        string materials = Path.Combine(dir, MaterialsDirName);
        if (Directory.Exists(materials))
        {
            var files = new List<string>(Directory.GetFiles(materials, "*", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            foreach (var file in files)
            {
                string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                Mix(sha, 0UL, System.Text.Encoding.UTF8.GetBytes(rel), File.ReadAllBytes(file));
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void Mix(SHA256 sha, ulong value)
    {
        var bytes = BitConverter.GetBytes(value);
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }

    private static void Mix(SHA256 sha, ulong sequence, byte[] command)
    {
        Mix(sha, sequence);
        var len = BitConverter.GetBytes(command.Length);
        sha.TransformBlock(len, 0, len.Length, null, 0);
        sha.TransformBlock(command, 0, command.Length, null, 0);
    }

    private static void Mix(SHA256 sha, ulong sequence, byte[] name, byte[] content)
    {
        Mix(sha, sequence);
        Mix(sha, (ulong)name.Length);
        sha.TransformBlock(name, 0, name.Length, null, 0);
        Mix(sha, (ulong)content.Length);
        sha.TransformBlock(content, 0, content.Length, null, 0);
    }

    // ---- Level.tscn (references, not data) ----

    /// <summary>Scene script resource path; the scene only holds data references (§7.1).</summary>
    private const string LevelScript = "res://World/Runtime/Formats/Save/MapLevel.cs";

    /// <summary>
    /// Write (or rewrite) <c>Level.tscn</c>. It holds only scene-level concerns and
    /// external references to <c>world.res</c>/<c>history.res</c>; editing geometry
    /// never changes it (§7.1). Emitted as text so it is diffable and reviewable (§7.3).
    /// </summary>
    public static void WriteLevelScene(string dir)
    {
        string worldPath = File.Exists(Path.Combine(dir, WorldResourceName)) ? WorldResourceName : "";
        string historyPath = File.Exists(Path.Combine(dir, HistoryResourceName)) ? HistoryResourceName : "";
        string text = BuildLevelSceneText(worldPath, historyPath);
        File.WriteAllText(Path.Combine(dir, LevelSceneName), text);
    }

    /// <summary>Build the <c>Level.tscn</c> text. Kept internal so tests can assert it byte-for-byte.</summary>
    public static string BuildLevelSceneText(string? worldPath, string? historyPath)
    {
        bool hasWorld = !string.IsNullOrEmpty(worldPath);
        bool hasHistory = !string.IsNullOrEmpty(historyPath);
        int steps = 1 + (hasWorld ? 1 : 0) + (hasHistory ? 1 : 0);
        var sb = new System.Text.StringBuilder();
        sb.Append("[gd_scene load_steps=").Append(steps).Append(" format=3]\n\n");
        sb.Append("[ext_resource type=\"Script\" path=\"").Append(LevelScript).Append("\" id=\"1_level\"]\n");
        int nextId = 2;
        int worldId = 0, historyId = 0;
        if (hasWorld)
        {
            worldId = nextId++;
            sb.Append("[ext_resource type=\"Resource\" path=\"").Append(PathForExtRef(worldPath!)).Append("\" id=\"").Append(worldId).Append("_world\"]\n");
        }
        if (hasHistory)
        {
            historyId = nextId++;
            sb.Append("[ext_resource type=\"Resource\" path=\"").Append(PathForExtRef(historyPath!)).Append("\" id=\"").Append(historyId).Append("_history\"]\n");
        }
        sb.Append("\n[node name=\"Level\" type=\"Node3D\"]\n");
        sb.Append("script = ExtResource(\"1_level\")\n");
        if (hasWorld)
            sb.Append("World = ExtResource(\"").Append(worldId).Append("_world\")\n");
        if (hasHistory)
            sb.Append("History = ExtResource(\"").Append(historyId).Append("_history\")\n");
        return sb.ToString();
    }

    // The scene references the data by a path **relative to itself** (the archive is
    // relocatable between res://maps/<id>/ and user://maps/<id>/; a res:// path would
    // pin it to a fixed location). Godot resolves a path with no scheme against the
    // scene file.
    private static string PathForExtRef(string name) => name.Replace('\\', '/');

    private static string Globalize(string path) =>
        path.StartsWith("res://") || path.StartsWith("user://") ? ProjectSettings.GlobalizePath(path) : path;
}
