using Godot;
using SauerbratenRevival.World;

namespace SauerbratenRevival.World.Materials;

/// <summary>
/// Diffuse texture + material for an empty-space material surface (water/lava/
/// glass). The textures come from <c>matwater</c>/<c>matlava</c>/<c>matglass</c>
/// (not the slot catalogue), so the engine's defaults from
/// <c>default_map_settings.cfg</c> are hardcoded here for now.
///
/// The flowing animation is a time-based UV scroll, mirroring the engine
/// (<c>water.cpp</c> / <c>material.cpp</c>: <c>scroll = lastmillis/1000</c>,
/// lava/waterfall scaled by 16).
/// </summary>
public static class MaterialTextures
{
    private const string ScrollShader = @"
shader_type spatial;
render_mode blend_mix, cull_disabled, unshaded;
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform vec2 scroll = vec2(0.0, 0.03);
uniform vec4 tint : source_color = vec4(1.0);
uniform float alpha = 0.6;
void fragment() {
    vec2 uv = UV + TIME * scroll;
    vec4 c = texture(albedo_tex, uv);
    ALBEDO = c.rgb * tint.rgb;
    ALPHA = alpha;
}
";

    public static string DiffusePath(ushort material) => (material & Octa.MATF_VOLUME) switch
    {
        Octa.MAT_WATER => "textures/waterfall.jpg",
        Octa.MAT_LAVA => "dg/floor_lava2.jpg",
        Octa.MAT_GLASS => "textures/waterfall.jpg",
        _ => "textures/default.png",
    };

    public static bool IsTranslucent(ushort material) => (material & Octa.MATF_VOLUME) != Octa.MAT_LAVA;

    public static Material Create(ushort material, ITextureSource textures)
    {
        ushort volume = (ushort)(material & Octa.MATF_VOLUME);
        var shaderMaterial = new ShaderMaterial { Shader = new Shader { Code = ScrollShader } };
        shaderMaterial.SetShaderParameter("albedo_tex", textures.Get(DiffusePath(material)));
        shaderMaterial.SetShaderParameter("tint", Tint(volume));
        shaderMaterial.SetShaderParameter("alpha", IsTranslucent(material) ? 0.65f : 1.0f);
        shaderMaterial.SetShaderParameter("scroll", Scroll(volume));
        return shaderMaterial;
    }

    private static Color Tint(ushort volume) => volume switch
    {
        Octa.MAT_WATER => new Color(0.55f, 0.75f, 0.95f),
        Octa.MAT_LAVA => new Color(1.0f, 0.55f, 0.25f),
        Octa.MAT_GLASS => new Color(0.85f, 0.95f, 1.0f),
        _ => Colors.White,
    };

    private static Vector2 Scroll(ushort volume) => volume switch
    {
        Octa.MAT_WATER => new Vector2(0.03f, 0.03f),
        Octa.MAT_LAVA => new Vector2(0.0f, 0.02f),
        _ => new Vector2(0.0f, 0.02f),
    };
}
