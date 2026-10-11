// Radiance hints world shader (docs/radiance-hints.md §2, §5).
// Evaluates L1 SH irradiance stored in a 3D texture:
//   rgb = c1 (directional irradiance vector, signed)
//   a   = c0 (scalar ambient irradiance)
// Diffuse irradiance:
//   irr = max(sky_tint * c0 + dot(c1, normal), vec3(0.0))
//
// render_mode unshaded: Godot's built-in lights do not light world geometry.
shader_type spatial;
render_mode unshaded, cull_back;

uniform sampler2D albedo_tex : source_color, filter_nearest_mipmap, repeat_enable;
uniform vec3 albedo_tint : source_color = vec3(1.0);

uniform sampler3D radiance_tex : filter_linear, repeat_disable;
uniform vec3 grid_origin;
uniform float grid_extent = 1.0;
uniform vec3 sky_tint : source_color = vec3(1.0, 1.0, 1.0);
uniform bool use_radiance = false;

// Optional normal map and glow (retained from original materials)
uniform sampler2D normal_tex : hint_normal, filter_nearest_mipmap, repeat_enable;
uniform bool use_normal = false;
uniform sampler2D glow_tex : source_color, filter_nearest_mipmap, repeat_enable;
uniform vec3 glow_tint : source_color = vec3(1.0);
uniform bool use_glow = false;

varying vec3 v_world_pos;
varying vec3 v_world_normal;

void vertex() {
    v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
    vec3 base_col = texture(albedo_tex, UV).rgb * albedo_tint;

    if (use_radiance && grid_extent > 0.0) {
        vec3 uvw = (v_world_pos - grid_origin) / grid_extent;
        vec4 probe = texture(radiance_tex, uvw);
        vec3 c1 = probe.rgb;
        float c0 = probe.a;

        vec3 norm = normalize(v_world_normal);
        // Note: when normal mapping is enabled in future passes, norm can be perturbed.
        vec3 irr = max(sky_tint * c0 + c1 * norm, vec3(0.0));
        vec3 out_col = base_col * irr;

        if (use_glow) {
            vec3 glow = texture(glow_tex, UV).rgb * glow_tint;
            out_col += glow;
        }

        ALBEDO = out_col;
    } else {
        vec3 out_col = base_col;
        if (use_glow) {
            vec3 glow = texture(glow_tex, UV).rgb * glow_tint;
            out_col += glow;
        }
        ALBEDO = out_col;
    }
}
