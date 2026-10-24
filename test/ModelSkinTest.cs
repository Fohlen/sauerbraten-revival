using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Models;

namespace SauerbratenRevival.Tests;

public class ModelSkinTest : GoTestBase
{
    public ModelSkinTest(Node testScene) : base(testScene) { }

    private sealed class DummyTextureSource : ITextureSource
    {
        private readonly Dictionary<string, Texture2D> _textures = new();

        public DummyTextureSource()
        {
            var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            _textures["skin.png"] = ImageTexture.CreateFromImage(img);
        }

        public Texture2D? Get(string path) => _textures.GetValueOrDefault(path);
        public Vector2I Size(string path) => new(4, 4);
        public Image? GetImage(string path) => null;
    }

    [Test]
    public void ModelCatalogReadsAlphaAndCullParams()
    {
        const string json = """
        {
          "models": {
            "tree": {
              "skin": "skin.png",
              "alphatest": 0.7,
              "alphablend": false,
              "cullface": false
            },
            "waterfall": {
              "skin": "skin.png",
              "alphatest": 0.1,
              "alphablend": true,
              "cullface": false
            },
            "box": {
              "skin": "skin.png",
              "cullface": true
            }
          }
        }
        """;

        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        try
        {
            var catalog = ModelCatalog.Load(path, "/tmp", "res://models")!;
            var tree = catalog.Get("tree")!;
            TestAssert.True(tree.AlphaTest.HasValue && Mathf.IsEqualApprox(0.7f, tree.AlphaTest.Value), "tree alphatest");
            TestAssert.True(tree.AlphaBlend == false, "tree alphablend");
            TestAssert.True(tree.CullFace == false, "tree cullface");

            var waterfall = catalog.Get("waterfall")!;
            TestAssert.True(waterfall.AlphaTest.HasValue && Mathf.IsEqualApprox(0.1f, waterfall.AlphaTest.Value), "waterfall alphatest");
            TestAssert.True(waterfall.AlphaBlend == true, "waterfall alphablend");

            var box = catalog.Get("box")!;
            TestAssert.True(box.CullFace == true, "box cullface");
            TestAssert.True(box.AlphaTest == null, "box alphatest is null");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void CutoutModelUsesAlphaScissor()
    {
        var textures = new DummyTextureSource();
        var cache = new Dictionary<string, Material>();

        var info = new ModelInfo
        {
            Skin = "skin.png",
            AlphaTest = 0.7f,
            AlphaBlend = false,
            CullFace = false,
        };

        var mat = ModelSkin.Build(info, textures, cache) as StandardMaterial3D;
        TestAssert.True(mat != null, "material created");
        TestAssert.Equal(BaseMaterial3D.TransparencyEnum.AlphaScissor, mat!.Transparency, "transparency is AlphaScissor");
        TestAssert.True(Mathf.IsEqualApprox(0.7f, mat.AlphaScissorThreshold), "scissor threshold matches AlphaTest");
        TestAssert.Equal(BaseMaterial3D.CullModeEnum.Disabled, mat.CullMode, "cull mode disabled for 2-sided geometry");
    }

    [Test]
    public void TransparentBlendedModelUsesAlphaBlend()
    {
        var textures = new DummyTextureSource();
        var cache = new Dictionary<string, Material>();

        var info = new ModelInfo
        {
            Skin = "skin.png",
            AlphaTest = 0.1f,
            AlphaBlend = true,
            CullFace = true,
        };

        var mat = ModelSkin.Build(info, textures, cache) as StandardMaterial3D;
        TestAssert.True(mat != null, "material created");
        TestAssert.Equal(BaseMaterial3D.TransparencyEnum.Alpha, mat!.Transparency, "transparency is Alpha");
        TestAssert.Equal(BaseMaterial3D.CullModeEnum.Back, mat.CullMode, "cull mode is backface culling");
    }

    [Test]
    public void OpaqueModelDefaults()
    {
        var textures = new DummyTextureSource();
        var cache = new Dictionary<string, Material>();

        var info = new ModelInfo
        {
            Skin = "skin.png",
        };

        var mat = ModelSkin.Build(info, textures, cache) as StandardMaterial3D;
        TestAssert.True(mat != null, "material created");
        TestAssert.Equal(BaseMaterial3D.TransparencyEnum.Disabled, mat!.Transparency, "opaque default transparency is Disabled");
        TestAssert.Equal(BaseMaterial3D.CullModeEnum.Disabled, mat.CullMode, "default cull mode is Disabled");
    }
}
