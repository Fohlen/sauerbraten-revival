using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FlatSharp;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Network;

namespace SauerbratenRevival.World.Runtime.Formats.Save;

/// <summary>
/// Loads and saves a map in the native FlatSharp reference-based format (ADR 0008).
/// A map directory contains:
///
/// <code>
/// map/
/// ├── Level.tscn            # scene root: instantiates MapLevel
/// ├── map.bin               # FlatSharp MapPackage (keyframes, trailing deltas, entities, environment)
/// └── materials/            # material manifest (optional)
/// </code>
/// </summary>
public static class MapStore
{
    public const string LevelSceneName = "Level.tscn";
    public const string MapPackageFileName = "map.bin";
    public const string MaterialsDirName = "materials";
    public const string ManifestResourceName = "manifest.res";
    public const string EmptyMapDir = "res://resources/maps/empty_map";
    public const string EmptyMapPackagePath = "res://resources/maps/empty_map/map.bin";

    /// <summary>Check for drift between keyframe Git commit and current repository HEAD (ADR 0008 §4).</summary>
    public static void CheckGitCommitDrift(string mapDir, string? keyframeCommit)
    {
        if (string.IsNullOrEmpty(keyframeCommit)) return;
        string headCommit = GitCommitResolver.GetHeadCommit();
        if (string.IsNullOrEmpty(headCommit)) return;

        if (!string.Equals(headCommit, keyframeCommit, StringComparison.OrdinalIgnoreCase))
        {
            GD.PushWarning($"[MapStore] Keyframe git commit '{keyframeCommit}' differs from current HEAD '{headCommit}' for map '{mapDir}'.");
        }
    }

    /// <summary>Load a FlatSharp MapPackage from a binary file path.</summary>
    public static MapPackage LoadMapPackageFile(string path)
    {
        string abs = Globalize(path);
        if (!File.Exists(abs))
            throw new FileNotFoundException($"Map package not found: {path}", abs);

        byte[] bytes = File.ReadAllBytes(abs);
        return MapPackage.Serializer.Parse(bytes);
    }

    /// <summary>Load the MapPackage from a map directory, or null if map.bin is absent.</summary>
    public static MapPackage? LoadMapPackage(string mapDir)
    {
        string binPath = Globalize(Path.Combine(mapDir, MapPackageFileName));
        if (!File.Exists(binPath)) return null;
        return LoadMapPackageFile(binPath);
    }

    /// <summary>Serialize and save a MapPackage to map.bin in a map directory.</summary>
    public static void SaveMapPackage(string mapDir, MapPackage package)
    {
        string dir = Globalize(mapDir);
        Directory.CreateDirectory(dir);
        string binPath = Path.Combine(dir, MapPackageFileName);
        byte[] buffer = new byte[MapPackage.Serializer.GetMaxSize(package)];
        int bytesWritten = MapPackage.Serializer.Write(buffer, package);
        File.WriteAllBytes(binPath, buffer.AsSpan(0, bytesWritten).ToArray());
    }

    /// <summary>Read the command history from a map directory.</summary>
    public static List<Command> LoadHistory(string mapDir)
    {
        var package = LoadMapPackage(mapDir);
        return package?.Deltas?.ToList() ?? new List<Command>();
    }

    /// <summary>
    /// Result of loading a map's data: an octree, entity list, trailing deltas, and the source FlatSharp MapPackage.
    /// </summary>
    public sealed class LoadedMap
    {
        public Octa World { get; init; } = null!;
        public List<MapEntity> Entities { get; init; } = new();
        public List<Command> History { get; init; } = new();
        public MapPackage? Package { get; init; }
    }

    /// <summary>Read the world from a map directory via map.bin.</summary>
    public static (Octa World, List<MapEntity> Entities, SetEnvironmentCmd? Environment) LoadWorld(string mapDir)
    {
        var loaded = Load(mapDir);
        return (loaded.World, loaded.Entities, loaded.Package?.Environment);
    }

    /// <summary>Load a map: materializes latest keyframe from map.bin and replays trailing deltas.</summary>
    public static LoadedMap Load(string mapDir, CommandSink? sink = null)
    {
        string binPath = Globalize(Path.Combine(mapDir, MapPackageFileName));
        if (!File.Exists(binPath) && !ResourceLoader.Exists(binPath))
            throw new FileNotFoundException($"No {MapPackageFileName} in '{mapDir}'.", binPath);

        var package = LoadMapPackageFile(binPath);
        if (package.Keyframes == null || package.Keyframes.Count == 0)
            throw new InvalidDataException($"MapPackage in '{mapDir}' contains no keyframes.");

        var latestKeyframe = package.Keyframes[^1];
        CheckGitCommitDrift(mapDir, latestKeyframe.GitCommit);

        var octa = latestKeyframe.ToOcta();
        var entities = new List<MapEntity>();
        if (package.Entities != null && package.Entities.Count > 0)
        {
            foreach (var e in package.Entities) entities.Add(e.ToMapEntity());
        }
        else if (latestKeyframe.Entities != null && latestKeyframe.Entities.Count > 0)
        {
            foreach (var e in latestKeyframe.Entities) entities.Add(e.ToMapEntity());
        }

        var env = latestKeyframe.Environment ?? package.Environment;
        var targetSink = sink ?? new CommandSink(octa, entities, env);
        var deltas = package.Deltas != null ? package.Deltas.ToList() : new List<Command>();
        foreach (var cmd in deltas) targetSink.Apply(cmd);

        return new LoadedMap
        {
            World = octa,
            Entities = targetSink.Entities,
            History = deltas,
            Package = package,
        };
    }

    /// <summary>
    /// Generate and dump the canonical empty_map base package to disk (res://resources/maps/empty_map/).
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
    /// Write a map directory: writes an authoritative map.bin package and Level.tscn.
    /// When compact is true, no deltas are written.
    /// </summary>
    public static void Save(
        string mapDir,
        Octa octa,
        IEnumerable<MapEntity>? entities = null,
        IReadOnlyList<Command>? history = null,
        bool compact = true,
        ManifestData? manifest = null,
        SetEnvironmentCmd? environment = null)
    {
        string dir = Globalize(mapDir);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, MaterialsDirName));

        if (manifest != null)
        {
            var manifestRes = ResourceManifest.FromData(manifest);
            Error err = ResourceSaver.Save(manifestRes, Path.Combine(dir, MaterialsDirName, ManifestResourceName));
            if (err != Error.Ok) throw new IOException($"Saving {ManifestResourceName} failed: {err}");
        }

        var entityList = entities != null ? entities.ToList() : new List<MapEntity>();
        var keyframe = KeyframePackage.Capture(octa, entityList, environment);
        var package = new MapPackage
        {
            FormatVersion = 1,
            MapName = Path.GetFileName(dir.TrimEnd('/', '\\')),
            Author = "Revival",
            Compression = CompressionCodec.None,
            Keyframes = new List<KeyframePackage> { keyframe },
            Deltas = history != null && !compact ? history.ToList() : new List<Command>(),
            Entities = entityList.Select(e => e.ToEntityCmd()).ToList(),
            Environment = environment,
        };
        SaveMapPackage(mapDir, package);

        WriteLevelScene(dir);
    }

    /// <summary>Convenience: snapshot a live octree and save it, compacted.</summary>
    public static void SaveCompacted(
        string mapDir,
        Octa octa,
        IEnumerable<MapEntity>? entities = null,
        ManifestData? manifest = null,
        SetEnvironmentCmd? environment = null) =>
        Save(mapDir, octa, entities, history: null, compact: true, manifest: manifest, environment: environment);

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
    /// Content hash of a map directory — hashes keyframes, deltas, entities, and materials.
    /// </summary>
    public static string ContentHash(string mapDir)
    {
        string dir = Globalize(mapDir);
        using var sha = SHA256.Create();

        var package = LoadMapPackage(mapDir);
        if (package != null)
        {
            if (package.Keyframes != null)
            {
                foreach (var kf in package.Keyframes)
                {
                    Mix(sha, KeyframePackage.ComputeHash(kf));
                }
            }
            if (package.Deltas != null)
            {
                foreach (var cmd in package.Deltas)
                {
                    Mix(sha, cmd.Sequence, NetMessage.Encode(cmd));
                }
            }
            if (package.Entities != null)
            {
                foreach (var e in package.Entities)
                {
                    Mix(sha, e.EntityId);
                    Mix(sha, (ulong)e.Type);
                    if (e.Pos != null)
                    {
                        Mix(sha, (ulong)e.Pos.X);
                        Mix(sha, (ulong)e.Pos.Y);
                        Mix(sha, (ulong)e.Pos.Z);
                    }
                    Mix(sha, (ulong)e.Attr1);
                    Mix(sha, (ulong)e.Attr2);
                    Mix(sha, (ulong)e.Attr3);
                    Mix(sha, (ulong)e.Attr4);
                    Mix(sha, (ulong)e.Attr5);
                }
            }
        }

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

    private static void Mix(SHA256 sha, byte[] data)
    {
        var len = BitConverter.GetBytes(data.Length);
        sha.TransformBlock(len, 0, len.Length, null, 0);
        sha.TransformBlock(data, 0, data.Length, null, 0);
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

    // ---- Level.tscn ----

    private const string LevelScript = "res://World/Runtime/Formats/Save/MapLevel.cs";

    public static void WriteLevelScene(string dir)
    {
        string text = BuildLevelSceneText();
        File.WriteAllText(Path.Combine(dir, LevelSceneName), text);
    }

    public static string BuildLevelSceneText()
    {
        return "[gd_scene load_steps=1 format=3]\n\n" +
               $"[ext_resource type=\"Script\" path=\"{LevelScript}\" id=\"1_level\"]\n\n" +
               "[node name=\"Level\" type=\"Node3D\"]\n" +
               "script = ExtResource(\"1_level\")\n";
    }

    private static string Globalize(string path) =>
        path.StartsWith("res://") || path.StartsWith("user://") ? ProjectSettings.GlobalizePath(path) : path;
}
