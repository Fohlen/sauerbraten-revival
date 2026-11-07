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
    public void ModelCatalogReadsCollisionParams()
    {
        const string json = """
        {
          "models": {
            "objects/lantern01": {
              "collide": true,
              "ellipsecollide": false,
              "collidecenter": [0.0, 0.0, 12.4687],
              "collideradius": [2.0, 2.0, 13.2408],
              "rejectradius": 25.8647
            },
            "objects/lamp01": {
              "collide": false,
              "ellipsecollide": false,
              "collidecenter": [-0.1173, 0.0199, -5.5892],
              "collideradius": [7.1952, 2.2301, 10.6876],
              "rejectradius": 17.9852
            },
            "vegetation/tree05": {
              "collide": true,
              "ellipsecollide": true,
              "collidecenter": [0.0, 0.0, 25.957],
              "collideradius": [4.0, 4.0, 28.5059],
              "rejectradius": 54.7559
            }
          }
        }
        """;

        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);
        try
        {
            var catalog = ModelCatalog.Load(path, "/tmp", "res://models")!;
            var lantern = catalog.Get("objects/lantern01")!;
            TestAssert.True(lantern.Collide, "lantern is collidable");
            TestAssert.False(lantern.EllipseCollide, "lantern uses box collision");
            TestAssert.Equal(new Vector3(0f, 0f, 12.4687f), lantern.CollideCenter, "lantern CollideCenter");
            TestAssert.Equal(new Vector3(2f, 2f, 13.2408f), lantern.CollideRadius, "lantern CollideRadius");
            TestAssert.True(Mathf.IsEqualApprox(25.8647f, lantern.RejectRadius), "lantern RejectRadius");

            var lamp = catalog.Get("objects/lamp01")!;
            TestAssert.False(lamp.Collide, "lamp01 is walk-through");
            TestAssert.False(lamp.EllipseCollide, "lamp01 ellipsecollide is false");

            var tree = catalog.Get("vegetation/tree05")!;
            TestAssert.True(tree.Collide, "tree05 is collidable");
            TestAssert.True(tree.EllipseCollide, "tree05 uses ellipse collision");
            TestAssert.Equal(new Vector3(0f, 0f, 25.957f), tree.CollideCenter, "tree CollideCenter");
            TestAssert.Equal(new Vector3(4f, 4f, 28.5059f), tree.CollideRadius, "tree CollideRadius");
            TestAssert.True(Mathf.IsEqualApprox(54.7559f, tree.RejectRadius), "tree RejectRadius");
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
