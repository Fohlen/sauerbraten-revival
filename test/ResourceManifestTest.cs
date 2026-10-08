using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Save;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the resource manifest (docs/ogz-import.md, docs/editor-and-commands.md §7):
/// building it from a metadata catalogue, saving/loading it in the map directory,
/// the self-describing full-path requirement, and that the converter emits it.
/// </summary>
public class ResourceManifestTest : GoTestBase
{
    public ResourceManifestTest(Node testScene) : base(testScene) { }

    private static string TempDir(string name)
    {
        string dir = $"user://test_maps/{name}_{System.Guid.NewGuid():N}";
        string abs = ProjectSettings.GlobalizePath(dir);
        if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
        return dir;
    }

    [Test]
    public void BuildResolvesFullTexturePaths()
    {
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePathFor("church51"));
        var data = ManifestBuilder.Build(catalogue);

        TestAssert.Equal(catalogue.VSlots.Count, data.Materials.Count, "one material per vslot");

        // Every entry must carry the full engine path for its diffuse texture — not
        // a bare index — so the manifest is self-describing.
        bool sawFullPath = false;
        foreach (var m in data.Materials)
        {
            if (m.DiffusePath.Length == 0) continue;
            sawFullPath = true;
            TestAssert.True(m.DiffusePath.Contains('/'),
                $"material {m.Index} diffuse path '{m.DiffusePath}' is a full engine path");
        }
        TestAssert.True(sawFullPath, "at least one material carries a diffuse texture path");
    }

    [Test]
    public void ManifestRoundTripsThroughMapDirectory()
    {
        string dir = TempDir("manifest");
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePathFor("bt_falls"));
        var data = ManifestBuilder.Build(catalogue);

        var octa = new Octa(8);
        octa.EmptyMap(8);
        MapStore.SaveCompacted(dir, octa, manifest: data);

        var loaded = MapStore.LoadManifest(dir);
        TestAssert.True(loaded != null, "manifest reloaded");
        TestAssert.Equal(data.Materials.Count, loaded!.Materials.Count, "material count preserved");
        TestAssert.Equal(data.Models.Count, loaded.Models.Count, "model count preserved");
        TestAssert.Equal(data.Sounds.Count, loaded.Sounds.Count, "sound count preserved");

        // Full paths survive the round-trip untouched.
        string path = data.Materials[0].DiffusePath;
        TestAssert.Equal(path, loaded.Materials[0].DiffusePath, "diffuse path preserved");
        foreach (var kv in data.Sounds)
            TestAssert.Equal(kv.Value, loaded.Sounds[kv.Key], $"sound {kv.Key} path preserved");
    }

    [Test]
    public void LoadManifestReturnsNullWhenAbsent()
    {
        string dir = TempDir("nomanifest");
        var octa = new Octa(8);
        octa.EmptyMap(8);
        MapStore.SaveCompacted(dir, octa);
        TestAssert.True(MapStore.LoadManifest(dir) == null, "no manifest -> null");
    }

    [Test]
    public void ContentHashChangesWhenManifestChanges()
    {
        var octa = new Octa(8);
        octa.EmptyMap(8);

        string a = TempDir("hash_manifest_a");
        string b = TempDir("hash_manifest_b");
        MapStore.SaveCompacted(a, octa);
        MapStore.SaveCompacted(b, octa, manifest: ManifestBuilder.Build(
            MetadataCatalogue.Load(TestData.CataloguePathFor("bt_falls"))));

        TestAssert.True(MapStore.ContentHash(a) != MapStore.ContentHash(b),
            "adding a manifest changes the archive hash");
    }

    [Test]
    public void ConverterEmitsManifestForKnownMap()
    {
        string dir = TempDir("convert_manifest");
        var map = TestData.ReadMap();
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePathFor("church51"));

        OgzConverter.Convert(map, dir, catalogue);

        var manifest = MapStore.LoadManifest(dir);
        TestAssert.True(manifest != null, "converter emitted a manifest");
        TestAssert.Equal(catalogue.VSlots.Count, manifest!.Materials.Count, "vslots became materials");
        TestAssert.Equal(catalogue.Sounds.Count, manifest.Sounds.Count, "sounds carried through");
    }
}
