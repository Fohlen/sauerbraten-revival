using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Models;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies the assimp-converted mapmodels parse as glTF. Skips if the
/// model-import tool hasn't been run (output dir is git-ignored).
/// </summary>
public class MapModelLoadTest : GoTestBase
{
    public MapModelLoadTest(Node testScene) : base(testScene) { }

    [Test]
    public void LoadsConvertedModels()
    {
        const string resDir = "res://assets/models";
        string dir = ProjectSettings.GlobalizePath(resDir);
        string manifestPath = Path.Combine(dir, "models.json");
        if (!File.Exists(manifestPath))
        {
            GD.Print("[models] no converted models (run tools/model-import/import_models.py); skipping");
            return;
        }

        var catalog = ModelCatalog.Load(manifestPath, dir, resDir);
        TestAssert.True(catalog != null, "manifest parsed");

        int checkedModels = 0;
        foreach (var info in catalog!.Models.Values)
        {
            if (info.GlbOsPath == null) continue;
            TestAssert.True(File.Exists(info.GlbOsPath), $"{info.Name} glb exists");

            var document = new GltfDocument();
            var state = new GltfState();
            var err = document.AppendFromFile(info.GlbOsPath, state);
            TestAssert.Equal(Error.Ok, err, $"{info.Name} glb parses");
            var scene = document.GenerateScene(state);
            TestAssert.True(scene != null, $"{info.Name} scene generated");
            TestAssert.True(HasMesh(scene!), $"{info.Name} scene has a mesh");
            scene!.Free();
            checkedModels++;
        }
        TestAssert.True(checkedModels > 0, "at least one converted model");
        GD.Print($"[models] parsed {checkedModels} converted glTF models");
    }

    private static bool HasMesh(Node node)
    {
        if (node is MeshInstance3D { Mesh: not null }) return true;
        foreach (var child in node.GetChildren())
            if (HasMesh(child)) return true;
        return false;
    }
}
