// Radiance hints world shader (docs/adr/0002-radiance-hints.md §2, §5, §7, §8).
// Evaluates L1 SH irradiance stored in 3D textures:
//   radiance_tex:     rgb = c1 (directional irradiance vector, signed), a = c0 (scalar ambient)
//   radiance_amb_tex: rgb = c0_rgb (colored ambient irradiance), a = sky visibility
// Supports world-space normal map perturbation against c1 and SHADER_ENVMAP specular reflections
// via Godot's ReflectionProbe (EnvMapProbe with AmbientMode = Disabled).
shader_type spatial;
render_mode ambient_light_disabled, cull_back;

uniform sampler2D albedo_tex : source_color, filter_nearest_mipmap, repeat_enable;
uniform vec3 albedo_tint : source_color = vec3(1.0);

uniform sampler3D radiance_tex : filter_linear, repeat_disable;
uniform sampler3D radiance_amb_tex : filter_linear, repeat_disable;
uniform vec3 grid_origin;
uniform float grid_extent = 1.0;
uniform vec3 sky_tint : source_color = vec3(1.0, 1.0, 1.0);
uniform bool use_radiance = false;
uniform bool use_radiance_amb = false;
uniform vec3 ambient_color : source_color = vec3(1.0, 1.0, 1.0);

// Optional normal map and glow (retained from original materials)
uniform sampler2D normal_tex : hint_normal, filter_nearest_mipmap, repeat_enable;
uniform bool use_normal = false;
uniform sampler2D glow_tex : source_color, filter_nearest_mipmap, repeat_enable;
uniform vec3 glow_tint : source_color = vec3(1.0);
uniform bool use_glow = false;

// Optional SHADER_ENVMAP specular reflection parameters (docs/adr/0002-radiance-hints.md §8.2)
uniform bool use_envmap = false;
uniform vec3 env_tint : source_color = vec3(0.2, 0.2, 0.2);
uniform float env_intensity : hint_range(0.0, 1.0) = 1.0;
uniform float env_roughness : hint_range(0.0, 1.0) = 0.05;

varying vec3 v_world_pos;
varying vec3 v_world_normal;

void vertex() {
    v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

// Transform tangent-space normal map sample into world space using a screen-space cotangent frame.
vec3 perturb_normal_world(vec3 world_normal, vec3 world_pos, vec2 uv, vec3 map_normal_rgb) {
    vec3 ts_normal = map_normal_rgb * 2.0 - 1.0;
    ts_normal.z = sqrt(max(0.0, 1.0 - dot(ts_normal.xy, ts_normal.xy)));

    vec3 dp1 = dFdx(world_pos);
    vec3 dp2 = dFdy(world_pos);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);

    vec3 dp2perp = cross(dp2, world_normal);
    vec3 dp1perp = cross(world_normal, dp1);
    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;

    float max_len2 = max(dot(T, T), dot(B, B));
    if (max_len2 < 1e-12) {
        return world_normal;
    }
    float invmax = inversesqrt(max_len2);
    mat3 tbn = mat3(T * invmax, B * invmax, world_normal);
    return normalize(tbn * ts_normal);
}

void fragment() {
    vec3 base_col = texture(albedo_tex, UV).rgb * albedo_tint;
    vec3 norm = normalize(v_world_normal);

    if (use_normal) {
        vec3 n_sample = texture(normal_tex, UV).rgb;
        norm = perturb_normal_world(norm, v_world_pos, UV, n_sample);
        NORMAL = normalize((VIEW_MATRIX * vec4(norm, 0.0)).xyz);
    }

    vec3 out_col;
    if (use_radiance && grid_extent > 0.0) {
        vec3 uvw = (v_world_pos - grid_origin) / grid_extent;
        vec4 probe = texture(radiance_tex, uvw);
        vec3 c1 = probe.rgb;
        float c0 = probe.a;

        float dir_term = dot(c1, norm);
        vec3 irr;
        if (use_radiance_amb) {
            vec3 c0_rgb = texture(radiance_amb_tex, uvw).rgb;
            vec3 chroma = (c0 > 1e-4) ? (c0_rgb / c0) : sky_tint;
            irr = max(c0_rgb + chroma * dir_term, vec3(0.0));
        } else {
            irr = max(sky_tint * c0 + vec3(dir_term), vec3(0.0));
        }
        out_col = base_col * irr;
    } else {
        out_col = base_col * ambient_color;
    }

    if (use_glow) {
        vec3 glow = texture(glow_tex, UV).rgb * glow_tint;
        out_col += glow;
    }

    if (use_envmap) {
        ALBEDO = env_tint;
        METALLIC = 1.0;
        SPECULAR = env_intensity;
        ROUGHNESS = env_roughness;
        EMISSION = out_col;
    } else {
        ALBEDO = vec3(0.0);
        METALLIC = 0.0;
        SPECULAR = 0.0;
        ROUGHNESS = 1.0;
        EMISSION = out_col;
    }
}

void light() {
    // World geometry ignores Godot's dynamic lights; all diffuse irradiance is baked into
    // the Radiance Hints 3D volume (output via EMISSION).
}
