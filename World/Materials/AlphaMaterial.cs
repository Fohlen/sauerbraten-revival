using Godot;

namespace SauerbratenRevival.World.Materials;

/// <summary>
/// Builds the transparent ("alpha") material variant for <c>MAT_ALPHA</c> cubes
/// and for vslots with non-default <c>alphafront</c>/<c>alphaback</c>.
///
/// Port of the engine's alpha path (<c>octarender.cpp</c> sortkey <c>ALPHA_*</c>,
/// <c>renderva.cpp renderalphageom</c> + <c>data/glsl.cfg worldshader</c>):
///   * transparent, two-sided (the engine draws back then front: <c>glCullFace</c>
///     flips between the two passes),
///   * premultiplied alpha (<c>glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA)</c>
///     with <c>diffuse.rgb *= diffuse.a</c>), and
///   * per-side alpha: <c>alphafront</c> for front-facing fragments,
///     <c>alphaback</c> for back-facing ones, with the engine's fallbacks
///     (a zero side means that pass has no triangles, so the other value is used
///     for the whole face).
///
/// The engine uses a two-pass trick (back pass with <c>alphaback</c> then front
/// pass with <c>alphafront</c>) to avoid sorting artifacts under a depth prepass.
/// Godot already sorts transparent surfaces back-to-front against the opaque
/// world, so a single two-sided material with <c>FRONT_FACING</c> selection is the
/// equivalent and avoids the extra draw calls.
/// </summary>
public static class AlphaMaterial
{
    private const string AlphaShader = @"
shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_opaque, diffuse_burley;
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform sampler2D glow_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform vec4 tint : source_color = vec4(1.0);
uniform vec3 glow_color : source_color = vec3(1.0);
uniform float alphafront = 0.5;
uniform float alphaback = 0.0;
uniform float alpha_scissor = 0.0;
uniform bool has_normal = false;
uniform bool has_glow = false;

void fragment() {
    vec4 c = texture(albedo_tex, UV);
    if (alpha_scissor > 0.0 && c.a <= alpha_scissor) {
        discard;
    }
    // Per-side alpha: a zero side means the engine's pass for that side has no
    // triangles, so the other value covers the whole (two-sided) face.
    float af = alphafront;
    float ab = alphaback;
    float a;
    if (ab == 0.0) a = af;
    else if (af == 0.0) a = ab;
    else a = FRONT_FACING ? af : ab;
    ALBEDO = c.rgb * tint.rgb;
    // Premultiplied alpha (the engine blends GL_ONE, GL_ONE_MINUS_SRC_ALPHA).
    ALPHA = c.a * a;
    if (has_normal) NORMAL_MAP = texture(normal_tex, UV).rgb;
    if (has_glow) EMISSION = texture(glow_tex, UV).rgb * glow_color;
}
";

    /// <summary>
    /// Build an alpha material for a vslot's textures. <paramref name="glow"/> is
    /// the vslot glow tint (used only when a glow texture exists).
    /// </summary>
    public static ShaderMaterial Create(Texture2D? albedo, Texture2D? normal, Texture2D? glow,
        Color tint, Color glowColor, float alphaFront, float alphaBack, float alphaScissor = 0.0f)
    {
        var material = new ShaderMaterial { Shader = new Shader { Code = AlphaShader } };
        // A missing diffuse still needs a sampler bound; the engine falls back to
        // notexture, and Godot's default white texture is the equivalent.
        material.SetShaderParameter("albedo_tex", albedo ?? WhiteTexture());
        material.SetShaderParameter("tint", tint);
        material.SetShaderParameter("alphafront", alphaFront);
        material.SetShaderParameter("alphaback", alphaBack);
        if (alphaScissor > 0.0f) material.SetShaderParameter("alpha_scissor", alphaScissor);
        if (normal != null)
        {
            material.SetShaderParameter("normal_tex", normal);
            material.SetShaderParameter("has_normal", true);
        }
        if (glow != null)
        {
            material.SetShaderParameter("glow_tex", glow);
            material.SetShaderParameter("glow_color", new Vector3(glowColor.R, glowColor.G, glowColor.B));
            material.SetShaderParameter("has_glow", true);
        }
        return material;
    }

    /// <summary>Builds a cutout (alpha-scissor) variant of the alpha material.</summary>
    public static ShaderMaterial CreateCutout(Texture2D? albedo, Texture2D? normal, Texture2D? glow,
        Color tint, Color glowColor, float scissorThreshold = 0.5f) =>
        Create(albedo, normal, glow, tint, glowColor, 1.0f, 1.0f, scissorThreshold);

    private static Texture2D? _white;

    private static Texture2D WhiteTexture()
    {
        if (_white != null) return _white;
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        return _white = ImageTexture.CreateFromImage(image);
    }
}
