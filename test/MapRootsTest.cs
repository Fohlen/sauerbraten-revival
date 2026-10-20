using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// The shipped-resource path resolver: project-owned converted maps
/// (<c>res://assets/packages/</c>) take priority over the read-only reference dump
/// (<c>$SAUER_REF/packages/</c>), and the metadata catalogue resolves through the
/// same <c>res://</c> VFS. See <see cref="MapRoots"/> and docs/adr/0006-ogz-import.md.
/// </summary>
public class MapRootsTest : GoTestBase
{
    public MapRootsTest(Node testScene) : base(testScene) { }

    [Test]
    public void NormalizeAddsExtensionAndBasePackage()
    {
        TestAssert.Equal("base/church51.ogz", MapRoots.Normalize("church51"), "bare name");
        TestAssert.Equal("base/church51.ogz", MapRoots.Normalize("church51.ogz"), "bare name + ext");
        TestAssert.Equal("base/church51.ogz", MapRoots.Normalize("base/church51"), "qualified, no ext");
        TestAssert.Equal("base/church51.ogz", MapRoots.Normalize("/base/church51.ogz"), "leading slash");
    }

    [Test]
    public void AbsolutePathIsReturnedUnchanged()
    {
        const string abs = "/tmp/some/absolute/map.ogz";
        TestAssert.Equal(abs, MapRoots.Resolve(abs), "absolute passthrough");
    }

    [Test]
    public void CandidatesAreAssetFirstThenDump()
    {
        var candidates = MapRoots.CandidatePaths("base/church51.ogz");
        TestAssert.Equal(2, candidates.Length, "two candidates");
        TestAssert.Equal($"{MapRoots.ProjectPackages}/base/church51.ogz",
            candidates[0], "project assets first");
        TestAssert.Equal(Path.Combine(MapRoots.ReferencePackages, "base", "church51.ogz"),
            candidates[1], "reference dump second");
    }

    [Test]
    public void ResolvePrefersProjectCopyWhenPresent()
    {
        // church51 exists in the dump. If a converted copy has been placed under
        // assets/packages (tools/ogztool/convert-all-maps.sh), the resolver must
        // prefer it. Skip the assertion when no converted copy exists so the test
        // passes on a clean checkout.
        string converted = $"{MapRoots.ProjectPackages}/base/church51.ogz";
        string resolved = MapRoots.Resolve("base/church51");
        if (Godot.FileAccess.FileExists(converted))
            TestAssert.Equal(converted, resolved, "converted copy wins");
        else
            TestAssert.Equal(Path.Combine(MapRoots.ReferencePackages, "base", "church51.ogz"),
                resolved, "falls back to the dump");
    }

    [Test]
    public void ResolveFallsBackToDumpWhenNotConverted()
    {
        // A map that is v32+ in the dump and (almost certainly) not converted.
        string resolved = MapRoots.Resolve("base/bt_falls");
        TestAssert.True(Godot.FileAccess.FileExists(resolved), $"expected an existing map, got '{resolved}'");
    }

    [Test]
    public void CatalogueIsResolvedThroughTheVfs()
    {
        // The metadata catalogue ships under res:// and must be read through Godot's
        // VFS so it still loads from a packed PCK (MetadataCatalogue.TryLoadForMap).
        TestAssert.True(MetadataCatalogue.TryLoadForMap("church51") != null, "church51 catalogue found");
        TestAssert.Equal("res://metadata-catalogue/church51.json", TestData.CataloguePath, "res path");
        TestAssert.True(MetadataCatalogue.TryLoadForMap("no_such_map_zzz") == null, "missing catalogue is null");
    }

    [Test]
    public void ReferenceRootHonoursProjectSettings()
    {
        string? originalEnv = System.Environment.GetEnvironmentVariable("SAUER_REF");
        string originalSetting = ProjectSettings.HasSetting(MapRoots.ProjectSettingKey)
            ? ProjectSettings.GetSetting(MapRoots.ProjectSettingKey).AsString()
            : "";

        try
        {
            // Clear env var to test ProjectSettings lookup
            System.Environment.SetEnvironmentVariable("SAUER_REF", null);

            ProjectSettings.SetSetting(MapRoots.ProjectSettingKey, "/test/custom/sauer_path");
            TestAssert.Equal(
                ProjectSettings.GlobalizePath("/test/custom/sauer_path"),
                MapRoots.ReferenceRoot,
                "honours ProjectSettings when env var is unset");

            // Empty setting should fall back to default
            ProjectSettings.SetSetting(MapRoots.ProjectSettingKey, "");
            TestAssert.Equal(
                MapRoots.DefaultReferenceRoot,
                MapRoots.ReferenceRoot,
                "falls back to default when setting is empty");

            // Env var takes priority over ProjectSettings
            System.Environment.SetEnvironmentVariable("SAUER_REF", "/test/env/override");
            ProjectSettings.SetSetting(MapRoots.ProjectSettingKey, "/test/custom/sauer_path");
            TestAssert.Equal(
                "/test/env/override",
                MapRoots.ReferenceRoot,
                "env var takes precedence over ProjectSettings");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SAUER_REF", originalEnv);
            if (!string.IsNullOrEmpty(originalSetting))
                ProjectSettings.SetSetting(MapRoots.ProjectSettingKey, originalSetting);
            else if (ProjectSettings.HasSetting(MapRoots.ProjectSettingKey))
                ProjectSettings.SetSetting(MapRoots.ProjectSettingKey, Variant.From<string?>(null));
        }
    }
}

