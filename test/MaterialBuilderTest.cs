using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the material/UV pipeline: <c>calctexgen</c> must match octarender.cpp,
/// and the catalogue must resolve to Godot materials with diffuse textures.
/// </summary>
public class MaterialBuilderTest : GoTestBase
{
    public MaterialBuilderTest(Node testScene) : base(testScene) { }

    [Test]
    public void CalcTexGenMatchesEngine()
    {
        // scale 1/4, no rotation/offset: k = 8 / 0.25 = 32.
        var vs = new CatalogueVSlot { Scale = 0.25f, Rotation = 0, OffsetX = 0, OffsetY = 0 };
        var size = new Vector2I(512, 256);
        float sk = 32.0f / 512.0f, tk = 32.0f / 256.0f;

        var g0 = MaterialBuilder.CalcTexGen(vs, 0, size);
        TestAssert.True(Mathf.Abs(g0.S.Y - sk) < 1e-6f, "dim0 s->y");
        TestAssert.True(Mathf.Abs(g0.T.Z + tk) < 1e-6f, "dim0 t->-z");
        TestAssert.True(Mathf.Abs(g0.Uv(new Vector3(0, 8, 4)).X - sk * 8) < 1e-5f, "dim0 uv.s");
        TestAssert.True(Mathf.Abs(g0.Uv(new Vector3(0, 8, 4)).Y + tk * 4) < 1e-5f, "dim0 uv.t");

        var g1 = MaterialBuilder.CalcTexGen(vs, 1, size);
        TestAssert.True(Mathf.Abs(g1.S.X - sk) < 1e-6f, "dim1 s->x");
        TestAssert.True(Mathf.Abs(g1.T.Z + tk) < 1e-6f, "dim1 t->-z");

        var g2 = MaterialBuilder.CalcTexGen(vs, 2, size);
        TestAssert.True(Mathf.Abs(g2.S.X - sk) < 1e-6f, "dim2 s->x");
        TestAssert.True(Mathf.Abs(g2.T.Y - tk) < 1e-6f, "dim2 t->+y");

        // 90 degrees: swapxy, and rotation 1 flips Y so tk = 32 / -256 = -0.125.
        var rot = new CatalogueVSlot { Scale = 0.25f, Rotation = 1 };
        var g90 = MaterialBuilder.CalcTexGen(rot, 0, size);
        TestAssert.True(Mathf.Abs(g90.S.Z + sk) < 1e-6f, "90 s->-z");
        TestAssert.True(Mathf.Abs(g90.T.Y + 0.125f) < 1e-6f, "90 t->-y");
    }

    [Test]
    public void LoadsImportedTextures()
    {
        if (!ResourceLoader.Exists("res://assets/textures/textures/waterfall.jpg"))
        {
            GD.Print("[textures] not imported (run tools/texture-import); skipping");
            return;
        }

        var source = new ResourceTextureSource();
        var texture = source.Get("textures/waterfall.jpg");
        TestAssert.True(texture != null, "imported waterfall texture loads");
        TestAssert.True(source.Size("textures/waterfall.jpg").X > 0, "imported texture has a size");
        TestAssert.True(source.GetImage("textures/waterfall.jpg") != null, "imported texture exposes an image");
    }

    [Test]
    public void BuildsMaterialsFromCatalogue()
    {
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePath);
        TestAssert.Equal(44, catalogue.Slots.Count, "catalogue slots");
        TestAssert.Equal(149, catalogue.VSlots.Count, "catalogue vslots");

        var textures = new DumpTextureSource(TestData.ReferenceRoot);
        var set = MaterialBuilder.Build(catalogue, textures);

        int count = 0, withTexture = 0, withNormal = 0, withGlow = 0;
        foreach (var pair in set.Materials)
        {
            count++;
            if (pair.Value is not StandardMaterial3D m) continue;
            if (m.AlbedoTexture != null) withTexture++;
            if (m.NormalEnabled && m.NormalTexture != null) withNormal++;
            if (m.EmissionEnabled && m.EmissionTexture != null) withGlow++;
        }
        TestAssert.Equal(149, count, "materials built");
        TestAssert.True(withTexture > 0, "materials carry albedo textures");
        TestAssert.True(withNormal > 0, "materials carry normal maps");
        TestAssert.True(withGlow > 0, "materials carry glow maps");

        var defaultTex = textures.Get("textures/default.png");
        TestAssert.True(defaultTex != null, "default.png loads from the dump");
        TestAssert.True(textures.Size("textures/default.png").X > 0, "default.png has a size");
    }

    [Test]
    public void BuildsSkyFromMapVar()
    {
        var map = TestData.ReadMap();
        var skybox = map.GetVarString("skybox");
        TestAssert.Equal("meister/uzze", skybox, "skybox mapvar");

        var textures = new DumpTextureSource(TestData.ReferenceRoot);
        var sky = SkyBuilder.Build(textures, skybox);
        TestAssert.True(sky != null, "sky built from the six cubemap faces");
    }

    [Test]
    public void MesherAssignsMaterialsAndUvs()
    {
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePath);
        var textures = new DumpTextureSource(TestData.ReferenceRoot);
        var set = MaterialBuilder.Build(catalogue, textures);

        var octa = TestData.ReadMap().ToOcta();
        var chunked = new OctaMesher(octa, set).BuildChunked(512);

        TestAssert.Equal(107356, chunked.TriangleCount, "geometry unchanged by materials");
        int surfaces = 0, withMaterial = 0, withUv = 0;
        foreach (var chunk in chunked.Chunks)
        {
            for (int s = 0; s < chunk.Mesh.GetSurfaceCount(); s++)
            {
                surfaces++;
                if (chunk.Mesh.SurfaceGetMaterial(s) != null) withMaterial++;
                var uvs = chunk.Mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                if (uvs.Length > 0 && (uvs[0] != Vector2.Zero || uvs[1] != Vector2.Zero)) withUv++;
            }
        }
        TestAssert.True(surfaces > 0, "has surfaces");
        TestAssert.Equal(surfaces, withMaterial, "every surface has a material");
        TestAssert.True(withUv > 0, "surfaces carry non-zero UVs");
    }

    [Test]
    public void CatalogueLoadsFromChurch51()
    {
        var cat = MetadataCatalogue.Load(TestData.CataloguePath);
        TestAssert.True(cat.Slots.Count > 0, "has slots");
        TestAssert.True(cat.VSlots.Count > 0, "has vslots");
        TestAssert.True(cat.Config.Ambient != 0, "has ambient config");
        TestAssert.True(cat.Config.Skybox.Length > 0, "has skybox");

        // First slot in church51 is default diffuse
        TestAssert.True(cat.Slots.ContainsKey(1), "slot 1 exists");
        TestAssert.Equal("textures/default.png", cat.Slots[1].DiffusePath, "slot 1 diffuse");

        // VSlot 1
        TestAssert.True(cat.VSlots.ContainsKey(1), "vslot 1 exists");
        TestAssert.Equal(1, cat.VSlots[1].Slot, "vslot 1 slot index");
    }

    [Test]
    public void CatalogueRoundTripsViaJsonString()
    {
        var original = MetadataCatalogue.Load(TestData.CataloguePath);
        string json = original.SerializeToJson(writeIndented: true);

        var restored = MetadataCatalogue.Deserialize(json);

        TestAssert.Equal(original.Slots.Count, restored.Slots.Count, "slot count match");
        TestAssert.Equal(original.VSlots.Count, restored.VSlots.Count, "vslot count match");
        TestAssert.Equal(original.MapModels.Count, restored.MapModels.Count, "mapmodel count match");
        TestAssert.Equal(original.Sounds.Count, restored.Sounds.Count, "sounds count match");

        TestAssert.Equal(original.Config.Ambient, restored.Config.Ambient, "ambient match");
        TestAssert.Equal(original.Config.SunlightScale, restored.Config.SunlightScale, "sunlight scale match");
        TestAssert.Equal(original.Config.Skybox, restored.Config.Skybox, "skybox match");

        foreach (var (idx, origSlot) in original.Slots)
        {
            TestAssert.True(restored.Slots.ContainsKey(idx), $"restored has slot {idx}");
            var restSlot = restored.Slots[idx];
            TestAssert.Equal(origSlot.Shader, restSlot.Shader, $"slot {idx} shader");
            TestAssert.Equal(origSlot.DiffusePath, restSlot.DiffusePath, $"slot {idx} diffuse");
            TestAssert.Equal(origSlot.NormalPath, restSlot.NormalPath, $"slot {idx} normal");
            TestAssert.Equal(origSlot.GlowPath, restSlot.GlowPath, $"slot {idx} glow");
        }

        foreach (var (idx, origVSlot) in original.VSlots)
        {
            TestAssert.True(restored.VSlots.ContainsKey(idx), $"restored has vslot {idx}");
            var restVSlot = restored.VSlots[idx];
            TestAssert.Equal(origVSlot.Slot, restVSlot.Slot, $"vslot {idx} slot");
            TestAssert.Equal(origVSlot.Scale, restVSlot.Scale, $"vslot {idx} scale");
            TestAssert.Equal(origVSlot.Rotation, restVSlot.Rotation, $"vslot {idx} rotation");
            TestAssert.Equal(origVSlot.OffsetX, restVSlot.OffsetX, $"vslot {idx} offsetX");
            TestAssert.Equal(origVSlot.OffsetY, restVSlot.OffsetY, $"vslot {idx} offsetY");
            TestAssert.Equal(origVSlot.Layer, restVSlot.Layer, $"vslot {idx} layer");
            TestAssert.Equal(origVSlot.Color, restVSlot.Color, $"vslot {idx} color");
            TestAssert.Equal(origVSlot.Glow, restVSlot.Glow, $"vslot {idx} glow");
            TestAssert.Equal(origVSlot.AlphaFront, restVSlot.AlphaFront, $"vslot {idx} alphaFront");
            TestAssert.Equal(origVSlot.AlphaBack, restVSlot.AlphaBack, $"vslot {idx} alphaBack");
        }
    }

    [Test]
    public void CatalogueRoundTripsViaUtf8Bytes()
    {
        var original = MetadataCatalogue.Load(TestData.CataloguePath);
        byte[] bytes = original.SerializeToUtf8Bytes();

        var restored = MetadataCatalogue.Deserialize(bytes);

        TestAssert.Equal(original.Slots.Count, restored.Slots.Count, "slot count match");
        TestAssert.Equal(original.VSlots.Count, restored.VSlots.Count, "vslot count match");
        TestAssert.Equal(original.Config.Ambient, restored.Config.Ambient, "ambient match");
    }

    [Test]
    public void CatalogueThrowsOnMissingFile()
    {
        TestAssert.Throws<System.IO.FileNotFoundException>(() =>
        {
            MetadataCatalogue.Load("res://does_not_exist_metadata.json");
        });
    }
}
