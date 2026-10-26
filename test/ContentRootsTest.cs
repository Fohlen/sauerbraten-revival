using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Content;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.Tests;

/// <summary>
/// The shared asset resolver: project-owned converted content
/// (<c>res://assets/…</c>) takes priority over the read-only reference dump
/// (<c>$SAUER_REF/packages/…</c>) for every content kind, and the metadata
/// catalogue resolves through the same <c>res://</c> VFS. See
/// <see cref="ContentRoots"/> and docs/adr/0006-ogz-import.md.
/// </summary>
public class ContentRootsTest : GoTestBase
{
    public ContentRootsTest(Node testScene) : base(testScene) { }

    [Test]
    public void NormalizeMapAddsExtensionAndBasePackage()
    {
        TestAssert.Equal("base/church51.ogz", ContentRoots.NormalizeMap("church51"), "bare name");
        TestAssert.Equal("base/church51.ogz", ContentRoots.NormalizeMap("church51.ogz"), "bare name + ext");
        TestAssert.Equal("base/church51.ogz", ContentRoots.NormalizeMap("base/church51"), "qualified, no ext");
        TestAssert.Equal("base/church51.ogz", ContentRoots.NormalizeMap("/base/church51.ogz"), "leading slash");
    }

    [Test]
    public void AbsolutePathIsReturnedUnchanged()
    {
        const string abs = "/tmp/some/absolute/map.ogz";
        TestAssert.Equal(abs, ContentRoots.ResolveMap(abs), "absolute passthrough");
    }

    [Test]
    public void MapCandidatesAreAssetFirstThenDump()
    {
        WithDump("/fake/dump", () =>
        {
            var candidates = ContentRoots.CandidatePaths(ContentKind.Map, "base/church51.ogz");
            TestAssert.Equal(2, candidates.Length, "two candidates");
            TestAssert.Equal($"{ContentRoots.ProjectPackages}/base/church51.ogz",
                candidates[0], "project assets first");
            TestAssert.Equal(Path.Combine("/fake/dump", "packages", "base", "church51.ogz"),
                candidates[1], "reference dump second");
        });
    }

    [Test]
    public void NoDumpConfiguredYieldsTheProjectCandidateOnly()
    {
        WithDump(null, () =>
        {
            var candidates = ContentRoots.CandidatePaths(ContentKind.Map, "base/church51.ogz");
            TestAssert.Equal(1, candidates.Length, "project candidate only");
            TestAssert.Equal($"{ContentRoots.ProjectPackages}/base/church51.ogz", candidates[0], "project path");
            TestAssert.True(ContentRoots.ReferencePackages == null, "no dump packages tree");
            TestAssert.True(ContentRoots.ReferenceTree(ContentKind.Sound) == null, "no dump tree");
            TestAssert.True(!ContentRoots.HasReferenceDump, "no dump reported");
        });
    }

    [Test]
    public void TextureCandidatesArePackageRelative()
    {
        WithDump("/fake/dump", () =>
        {
            var candidates = ContentRoots.CandidatePaths(ContentKind.Texture, "base/foo.jpg");
            TestAssert.Equal("res://assets/textures/base/foo.jpg", candidates[0], "project textures");
            TestAssert.Equal(Path.Combine("/fake/dump", "packages", "base", "foo.jpg"),
                candidates[1], "dump keeps the package-relative path");
        });
    }

    [Test]
    public void SoundCandidatesUseTheSoundsSubdirectory()
    {
        WithDump("/fake/dump", () =>
        {
            var candidates = ContentRoots.CandidatePaths(ContentKind.Sound, "q009/rifle.ogg");
            TestAssert.Equal("res://assets/sounds/q009/rifle.ogg", candidates[0], "project sounds");
            TestAssert.Equal(Path.Combine("/fake/dump", "packages", "sounds", "q009", "rifle.ogg"),
                candidates[1], "dump nests sounds/");
        });
    }

    [Test]
    public void ModelCandidatesUseTheProjectModelTree()
    {
        var candidates = ContentRoots.CandidatePaths(ContentKind.Model, "teleporter.glb");
        TestAssert.Equal("res://assets/models/teleporter.glb", candidates[0], "project models");
    }

    [Test]
    public void ResolvePrefersProjectCopyWhenPresent()
    {
        if (!ContentRoots.HasReferenceDump)
        {
            GD.Print("[content] no reference dump configured (set SAUER_REF); skipping dump fallback check");
            return;
        }

        // church51 exists in the dump. If a converted copy has been placed under
        // assets/packages (tools/ogztool/convert-all-maps.sh), the resolver must
        // prefer it. Skip the assertion when no converted copy exists so the test
        // passes on a clean checkout.
        string converted = $"{ContentRoots.ProjectPackages}/base/church51.ogz";
        string resolved = ContentRoots.ResolveMap("base/church51");
        if (Godot.FileAccess.FileExists(converted))
            TestAssert.Equal(converted, resolved, "converted copy wins");
        else
            TestAssert.Equal(Path.Combine(ContentRoots.ReferencePackages!, "base", "church51.ogz"),
                resolved, "falls back to the dump");
    }

    [Test]
    public void ResolveFallsBackToDumpWhenNotConverted()
    {
        if (!ContentRoots.HasReferenceDump)
        {
            GD.Print("[content] no reference dump configured (set SAUER_REF); skipping dump fallback check");
            return;
        }

        // A map that is v32+ in the dump and (almost certainly) not converted.
        string resolved = ContentRoots.ResolveMap("base/bt_falls");
        TestAssert.True(Godot.FileAccess.FileExists(resolved), $"expected an existing map, got '{resolved}'");
    }

    [Test]
    public void TryResolveReturnsNullWhenMissing()
    {
        TestAssert.True(ContentRoots.TryResolve(ContentKind.Map, "base/no_such_map_zzz.ogz") == null,
            "missing asset resolves to null");
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
        string originalSetting = ProjectSettings.HasSetting(ContentRoots.ProjectSettingKey)
            ? ProjectSettings.GetSetting(ContentRoots.ProjectSettingKey).AsString()
            : "";

        try
        {
            // Clear env var to test ProjectSettings lookup
            System.Environment.SetEnvironmentVariable("SAUER_REF", null);

            ProjectSettings.SetSetting(ContentRoots.ProjectSettingKey, "/test/custom/sauer_path");
            TestAssert.Equal(
                ProjectSettings.GlobalizePath("/test/custom/sauer_path"),
                ContentRoots.ReferenceRoot,
                "honours ProjectSettings when env var is unset");

            // An empty setting means no dump at all: there is no guessed default,
            // because a guessed path would make an unconfigured checkout read a
            // protected folder (macOS prompts for Downloads access on first touch).
            ProjectSettings.SetSetting(ContentRoots.ProjectSettingKey, "");
            TestAssert.True(ContentRoots.ReferenceRoot == null, "empty setting means no dump");
            TestAssert.True(!ContentRoots.HasReferenceDump, "no dump reported");

            // Env var takes priority over ProjectSettings
            System.Environment.SetEnvironmentVariable("SAUER_REF", "/test/env/override");
            ProjectSettings.SetSetting(ContentRoots.ProjectSettingKey, "/test/custom/sauer_path");
            TestAssert.Equal(
                "/test/env/override",
                ContentRoots.ReferenceRoot,
                "env var takes precedence over ProjectSettings");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SAUER_REF", originalEnv);
            ProjectSettings.SetSetting(ContentRoots.ProjectSettingKey, originalSetting);
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> with <c>SAUER_REF</c> pointing at
    /// <paramref name="root"/> (null = no dump configured) and the ProjectSetting
    /// cleared, restoring both afterwards. The dump root is never a real location
    /// here: the resolver only assembles paths, it does not read them.
    /// </summary>
    private static void WithDump(string? root, System.Action body)
    {
        string? originalEnv = System.Environment.GetEnvironmentVariable("SAUER_REF");
        string originalSetting = ProjectSettings.HasSetting(ContentRoots.ProjectSettingKey)
            ? ProjectSettings.GetSetting(ContentRoots.ProjectSettingKey).AsString()
            : "";

        try
        {
            ProjectSettings.SetSetting(ContentRoots.ProjectSettingKey, "");
            System.Environment.SetEnvironmentVariable("SAUER_REF", root);
            body();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SAUER_REF", originalEnv);
            ProjectSettings.SetSetting(ContentRoots.ProjectSettingKey, originalSetting);
        }
    }
}
