using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies the assimp-converted mapmodels parse as glTF. Skips if the
/// model-import tool hasn't been run (output dir is git-ignored).
/// </summary>
public class MapModelLoadTest : TestClass
{
    public MapModelLoadTest(Node testScene) : base(testScene) { }

    [Test]
    public void LoadsConvertedModels()
    {
        string dir = ProjectSettings.GlobalizePath("res://tools/model-import/out");
        string manifestPath = Path.Combine(dir, "models.json");
        if (!File.Exists(manifestPath))
        {
            GD.Print("[models] no converted models (run tools/model-import/import_models.py); skipping");
            return;
        }

        var catalog = ModelCatalog.Load(manifestPath, dir);
        TestAssert.True(catalog != null, "manifest parsed");

        int checkedModels = 0;
        foreach (var info in catalog!.Models.Values)
        {
            if (info.GlbPath == null) continue;
            TestAssert.True(File.Exists(info.GlbPath), $"{info.Name} glb exists");

            var document = new GltfDocument();
            var state = new GltfState();
            var err = document.AppendFromFile(info.GlbPath, state);
            TestAssert.Equal(Error.Ok, err, $"{info.Name} glb parses");
            TestAssert.True(document.GenerateScene(state) != null, $"{info.Name} scene generated");
            checkedModels++;
        }
        TestAssert.True(checkedModels > 0, "at least one converted model");
        GD.Print($"[models] parsed {checkedModels} converted glTF models");
    }
}
