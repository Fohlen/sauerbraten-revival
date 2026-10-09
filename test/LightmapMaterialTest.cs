using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.Tests;

/// <summary>
/// The baked-lightmap material seam (docs/lighting.md §3.4): the world shader must
/// load, and a lightmap material must bind the atlas + albedo and enable sampling.
/// Binding the *real* per-face atlas is a later step; here the atlas is synthetic.
/// </summary>
public class LightmapMaterialTest : GoTestBase
{
    public LightmapMaterialTest(Node testScene) : base(testScene) { }

    private static Texture2D Atlas(Color color)
    {
        var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        img.Fill(color);
        return ImageTexture.CreateFromImage(img);
    }

    [Test]
    public void LightmapShaderLoads()
    {
        var shader = ResourceLoader.Load<Shader>(MaterialBuilder.LightmapShaderPath);
        TestAssert.True(shader != null, "lightmap.gdshader loads");
    }

    [Test]
    public void BuildsLightmapMaterialBindingAtlasAndAlbedo()
    {
        var atlas = Atlas(new Color(0.5f, 0.5f, 0.5f));
        var albedo = Atlas(Colors.White);
        var mat = MaterialBuilder.BuildLightmapMaterial(albedo, atlas, new Color(1, 0.5f, 0.25f));

        TestAssert.True(mat.Shader != null, "shader set");
        TestAssert.True(mat.GetShaderParameter("use_lightmap").AsBool(), "lightmap sampling enabled");
        TestAssert.True(mat.GetShaderParameter("lightmap").Obj == atlas, "atlas bound to lightmap");
        TestAssert.True(mat.GetShaderParameter("albedo_tex").Obj == albedo, "albedo bound");
        var tint = mat.GetShaderParameter("albedo_tint").AsVector3();
        TestAssert.True(Mathf.Abs(tint.Y - 0.5f) < 1e-5f, "tint carried");
    }

    [Test]
    public void MaterialSetPrefersLightmappedSurface()
    {
        var set = new MaterialSet();
        var plain = new StandardMaterial3D();
        set.Add(7, plain, new[] { new TexGen(), new TexGen(), new TexGen() });

        var atlas = Atlas(Colors.White);
        var lm = MaterialBuilder.BuildLightmapMaterial(null, atlas, Colors.White);
        var key = new SurfaceKey(7, 2);
        set.AddLightmapped(key, lm);

        TestAssert.True(set.MaterialFor(key) == lm, "lightmapped surface resolves to its atlas material");
        TestAssert.True(set.MaterialFor(SurfaceKey.Unlit(7)) == plain, "unlit surface uses the vslot material");
        TestAssert.True(set.MaterialFor(7) == plain, "vslot lookup ignores the atlas");
    }
}
