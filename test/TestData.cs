using System.Collections.Generic;
using System.IO;
using Godot;
using SauerbratenRevival.World.Content;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locates the test fixtures. The OGZ maps are not committed — they are read
/// from the read-only reference dump (same <c>SAUER_REF</c> the refharness uses),
/// or from the converted copies under <c>res://assets</c> when those exist.
/// </summary>
public static class TestData
{
    /// <summary>
    /// The configured reference dump root. Tests that read original content need
    /// one, so failing loudly here beats silently asserting against nothing.
    /// </summary>
    public static string ReferenceRoot => ContentRoots.ReferenceRoot
        ?? throw new System.InvalidOperationException(
            "No reference dump configured: set SAUER_REF (or sauerbraten/reference_path) — see docs/dependencies.md.");

    public static readonly string OgzPath = ContentRoots.ResolveMap("base/church51");

    public static readonly string CataloguePath = "res://metadata-catalogue/church51.json";

    public static string OgzPathFor(string map) => ContentRoots.ResolveMap(map);

    public static string CataloguePathFor(string map) => $"res://metadata-catalogue/{map}.json";

    public static OgzMap ReadMap() => ReadMapAt(OgzPath);

    public static OgzMap ReadMap(string map) => ReadMapAt(OgzPathFor(map));

    // Parsing church51 costs ~115 ms (decompress + octree decode) and the suite
    // calls ReadMap ~36 times. Parse each map once and hand out deep clones
    // (~28 ms) instead — a fresh, independently-mutable map per call, no re-read
    // and no shared state.
    private static readonly Dictionary<string, OgzMap> Cache = new();

    private static OgzMap ReadMapAt(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached.Clone();
        var map = ParseAt(path);
        Cache[path] = map;
        return map.Clone();
    }

    private static OgzMap ParseAt(string path)
    {
        if (!Godot.FileAccess.FileExists(path))
            throw new FileNotFoundException(
                $"OGZ test map not found at '{path}'. Run tools/ogztool/convert-all-maps.sh, or set SAUER_REF to the reference dump root.", path);
        return OgzReader.Read(path);
    }
}
