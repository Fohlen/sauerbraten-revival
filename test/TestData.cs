using System.IO;
using Godot;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locates the test fixtures. The OGZ maps are not committed — they are read
/// from the read-only reference dump (same <c>SAUER_REF</c> the refharness uses).
/// </summary>
public static class TestData
{
    public static readonly string ReferenceRoot =
        System.Environment.GetEnvironmentVariable("SAUER_REF")
        ?? "/Users/fohlen/Downloads/sauerbraten-code-r6889";

    public static readonly string OgzPath =
        Path.Combine(ReferenceRoot, "packages", "base", "church51.ogz");

    public static readonly string CataloguePath =
        ProjectSettings.GlobalizePath("res://metadata-catalogue/church51.json");

    public static string OgzPathFor(string map) =>
        Path.Combine(ReferenceRoot, "packages", "base", map + ".ogz");

    public static string CataloguePathFor(string map) =>
        ProjectSettings.GlobalizePath($"res://metadata-catalogue/{map}.json");

    public static OgzMap ReadMap() => ReadMapAt(OgzPath);

    public static OgzMap ReadMap(string map) => ReadMapAt(OgzPathFor(map));

    private static OgzMap ReadMapAt(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"OGZ test map not found at '{path}'. Set SAUER_REF to the reference dump root.", path);
        return OgzReader.Read(path);
    }
}
