// Baked-lightmap world shader (sauerbraten-revival, docs/lighting.md).
//
// Reproduces the original world shader's `gl_FragColor = diffuse * lm` — modulate
// albedo by the baked atlas sample. The imported atlas is bound as `lightmap`,
// sampled at UV2 (the per-vertex atlas coordinate the mesher emits), and the
// material's diffuse texture at UV (the world-space calctexgen coordinate).
//
// When `use_lightmap` is false the shader falls back to unlit albedo (maps with no
// baked lightmaps, or faces with no surface link), so one shader serves both.
//
// This is deliberately the *simple direct* form: Sauerbraten lightmaps are direct
// per-lumel irradiance with no GI bounce, so there is no ambient/indirect term to
// add. Godot's own lights still accumulate on top (leave `LightBuilder` off for a
// fully baked map — see docs/lighting.md).
shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D albedo_tex : source_color, filter_nearest_mipmap, repeat_enable;
// The atlas is a page: clamp so a stray UV cannot wrap into a neighbouring atlas.
uniform sampler2D lightmap : source_color, filter_linear, repeat_disable;
uniform vec3 albedo_tint : source_color = vec3(1.0);
uniform bool use_lightmap = false;

void fragment() {
    vec3 albedo = texture(albedo_tex, UV).rgb * albedo_tint;
    if (use_lightmap) {
        vec3 lm = texture(lightmap, UV2).rgb;
        ALBEDO = albedo * lm;
    } else {
        ALBEDO = albedo;
    }
}
