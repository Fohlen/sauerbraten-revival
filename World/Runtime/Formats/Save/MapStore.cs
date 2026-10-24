using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Godot;
using SauerbratenRevival.World.Commands;

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

    /// <summary>Read the command history from a map directory (empty if the file is absent).</summary>
    public static List<WorldCommand> LoadHistory(string mapDir)
    {
        string path = Globalize(Path.Combine(mapDir, HistoryResourceName));
        if (!ResourceLoader.Exists(path) && !File.Exists(path)) return new List<WorldCommand>();
        var res = ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace);
        if (res == null) throw new InvalidDataException($"Failed to load {path}.");
        if (res is HistoryResource typed) return typed.ToCommands();

        var list = new List<WorldCommand>();
        var commands = res.Get("Commands");
        if (commands.VariantType == Variant.Type.Array)
            foreach (var blob in commands.As<Godot.Collections.Array>())
                list.Add(WorldCommandCodec.Decode(blob.As<byte[]>()));
        return list;
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
        public List<WorldCommand> History = new();
    }

    /// <summary>Load the base world, then replay the history tail on top of it (§4 strategy 2).</summary>
    public static LoadedMap Load(string mapDir, IWorld? world = null)
    {
        var snapshot = LoadWorld(mapDir);
        var octa = snapshot.ToOcta();
        var target = world ?? new OctaWorld(octa);
        var loaded = new LoadedMap { World = octa, Snapshot = snapshot, History = LoadHistory(mapDir) };
        foreach (var cmd in loaded.History) target.Apply(cmd);
        return loaded;
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
    public static void Save(string mapDir, WorldSnapshot snapshot, IReadOnlyList<WorldCommand>? history,
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
        foreach (var cmd in LoadHistory(mapDir)) Mix(sha, cmd.Sequence, WorldCommandCodec.Encode(cmd));

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
        string worldPath = WorldResourceName;
        string historyPath = File.Exists(Path.Combine(dir, HistoryResourceName)) ? HistoryResourceName : "";
        string text = BuildLevelSceneText(worldPath, historyPath);
        File.WriteAllText(Path.Combine(dir, LevelSceneName), text);
    }

    /// <summary>Build the <c>Level.tscn</c> text. Kept internal so tests can assert it byte-for-byte.</summary>
    public static string BuildLevelSceneText(string worldPath, string historyPath)
    {
        bool hasHistory = !string.IsNullOrEmpty(historyPath);
        int steps = hasHistory ? 4 : 3;
        var sb = new System.Text.StringBuilder();
        sb.Append("[gd_scene load_steps=").Append(steps).Append(" format=3]\n\n");
        sb.Append("[ext_resource type=\"Script\" path=\"").Append(LevelScript).Append("\" id=\"1_level\"]\n");
        sb.Append("[ext_resource type=\"Resource\" path=\"").Append(PathForExtRef(worldPath)).Append("\" id=\"2_world\"]\n");
        if (hasHistory)
            sb.Append("[ext_resource type=\"Resource\" path=\"").Append(PathForExtRef(historyPath)).Append("\" id=\"3_history\"]\n");
        sb.Append("\n[node name=\"Level\" type=\"Node3D\"]\n");
        sb.Append("script = ExtResource(\"1_level\")\n");
        sb.Append("World = ExtResource(\"2_world\")\n");
        if (hasHistory)
            sb.Append("History = ExtResource(\"3_history\")\n");
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
