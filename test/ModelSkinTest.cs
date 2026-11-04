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

        public void Add(string path)
        {
            var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            _textures[path] = ImageTexture.CreateFromImage(img);
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

    [Test]
    public void TeamSkinResolutionWorks()
    {
        var textures = new DummyTextureSource();
        textures.Add("hands_blue.png");
        textures.Add("hands_red.png");
        textures.Add("hands.png");

        var info = new ModelInfo
        {
            Skin = "hands.png",
            Skins =
            {
                new ModelSkinInfo { Part = "hands", Diffuse = "hands_blue.png" },
                new ModelSkinInfo { Part = "hands", Diffuse = "hands_red.png" },
                new ModelSkinInfo { Part = "hands", Diffuse = "hands.png" },
            },
        };

        TestAssert.Equal("hands_blue.png", info.SkinForTeam(1), "team 1 resolves blue skin");
        TestAssert.Equal("hands_red.png", info.SkinForTeam(2), "team 2 resolves red skin");
        TestAssert.Equal("hands.png", info.SkinForTeam(0), "team 0 resolves neutral skin");

        var cache = new Dictionary<string, Material>();
        var blueMat = ModelSkin.Build(info, textures, cache, team: 1) as StandardMaterial3D;
        var redMat = ModelSkin.Build(info, textures, cache, team: 2) as StandardMaterial3D;
        var neutralMat = ModelSkin.Build(info, textures, cache, team: 0) as StandardMaterial3D;

        TestAssert.True(blueMat != null && blueMat.AlbedoTexture == textures.Get("hands_blue.png"), "blue material built");
        TestAssert.True(redMat != null && redMat.AlbedoTexture == textures.Get("hands_red.png"), "red material built");
        TestAssert.True(neutralMat != null && neutralMat.AlbedoTexture == textures.Get("hands.png"), "neutral material built");
    }

    [Test]
    public void MultiSurfaceSkinsBuildInOrder()
    {
        var textures = new DummyTextureSource();
        textures.Add("lower.png");
        textures.Add("upper.png");
        textures.Add("lower_norm.png");
        textures.Add("upper_norm.png");

        var info = new ModelInfo
        {
            Skin = "upper.png",
            Skins =
            {
                new ModelSkinInfo { Part = "Lower", Diffuse = "lower.png", Bump = "lower_norm.png" },
                new ModelSkinInfo { Part = "Upper", Diffuse = "upper.png", Bump = "upper_norm.png" },
            },
        };

        var cache = new Dictionary<string, Material>();
        var skins = ModelSkin.BuildSkins(info, textures, cache);

        TestAssert.Equal(2, skins.Count, "builds exactly 2 surface materials");
        var mat0 = skins[0] as StandardMaterial3D;
        var mat1 = skins[1] as StandardMaterial3D;

        TestAssert.True(mat0 != null && mat0.AlbedoTexture == textures.Get("lower.png"), "surface 0 gets Lower texture");
        TestAssert.True(mat0 != null && mat0.NormalTexture == textures.Get("lower_norm.png"), "surface 0 gets Lower normal");
        TestAssert.True(mat1 != null && mat1.AlbedoTexture == textures.Get("upper.png"), "surface 1 gets Upper texture");
        TestAssert.True(mat1 != null && mat1.NormalTexture == textures.Get("upper_norm.png"), "surface 1 gets Upper normal");
    }

    [Test]
    public void ApplyMaterialsAdvancesAcrossMultipleChildMeshes()
    {
        var mat0 = new StandardMaterial3D { AlbedoColor = Colors.Red };
        var mat1 = new StandardMaterial3D { AlbedoColor = Colors.Green };

        var instance = new ModelInstance();
        var holder = new Node3D { Name = "Model" };
        var mesh0 = new MeshInstance3D { Name = "bark", Mesh = new BoxMesh() };
        var mesh1 = new MeshInstance3D { Name = "leaf", Mesh = new QuadMesh() };
        holder.AddChild(mesh0);
        holder.AddChild(mesh1);
        instance.AddChild(holder);

        instance.ApplyMaterials(new Material?[] { mat0, mat1 });

        TestAssert.Equal(mat0, mesh0.GetSurfaceOverrideMaterial(0), "first child mesh gets material 0 (bark)");
        TestAssert.Equal(mat1, mesh1.GetSurfaceOverrideMaterial(0), "second child mesh gets material 1 (leaf)");

        instance.Free();
    }
}
