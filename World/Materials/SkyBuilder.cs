using Godot;

namespace SauerbratenRevival.World.Materials;

/// <summary>
/// Builds a Godot <see cref="Sky"/> from the map's cubemap skybox (the
/// <c>skybox</c> mapvar, e.g. <c>meister/uzze</c>). Faces live under the
/// reference dump's <c>packages/</c> as <c>&lt;skybox&gt;_{lf,rt,up,dn,ft,bk}.jpg</c>.
/// </summary>
public static class SkyBuilder
{
    // texture.cpp cubemapsides target the OpenGL cube (Sauer is Z-up):
    //   rt=+X, lf=-X, up=+Z, dn=-Z, bk=+Y, ft=-Y.
    // Godot's Cubemap::create_from_images order is +X, -X, +Y, -Y, +Z, -Z in a
    // Y-up world, so remap: +Y<-up, -Y<-dn, +Z<-bk, -Z<-ft.
    private static readonly string[] GodotFaceOrder = { "rt", "lf", "up", "dn", "bk", "ft" };

    private static readonly Shader SkyShader = new()
    {
        Code = "shader_type sky;\nuniform samplerCube sky_cube;\nvoid sky() { COLOR = texture(sky_cube, EYEDIR).rgb; }\n",
    };

    public static Sky? Build(ITextureSource textures, string? skybox)
    {
        if (string.IsNullOrEmpty(skybox)) return null;

        var images = new Godot.Collections.Array<Image>();
        foreach (var face in GodotFaceOrder)
        {
            var image = textures.GetImage($"{skybox}_{face}.jpg");
            if (image == null) return null;
            images.Add(image);
        }

        var cubemap = new Cubemap();
        cubemap.CreateFromImages(images);

        var material = new ShaderMaterial { Shader = SkyShader };
        material.SetShaderParameter("sky_cube", cubemap);
        return new Sky { SkyMaterial = material };
    }
}
