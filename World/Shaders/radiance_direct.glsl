#[compute]
#version 450

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(set = 0, binding = 0) uniform sampler3D voxel_grid;

struct GpuLight {
    vec4 position_range; // xyz = Godot world pos, w = range (attr1)
    vec4 color_spotcos;  // rgb = linear color, w = cos(spot_angle) (-1.0 for omni)
    vec4 spot_dir;       // xyz = normalized spot direction, w = spot attenuation
};

layout(set = 0, binding = 1, std430) restrict readonly buffer LightBuffer {
    uint light_count;
    uint pad0, pad1, pad2;
    GpuLight lights[];
} light_buf;

layout(set = 0, binding = 2, rgba16f) uniform restrict writeonly image3D probe_direct_tex;

layout(push_constant, std430) uniform PushConstants {
    vec3 grid_origin;
    float grid_extent;

    vec3 sun_dir;
    float sun_scale;

    vec3 sun_color;
    uint probe_res;

    vec3 sky_color;
    uint sky_rays;

    ivec3 dirty_min;
    float max_ray_dist;

    ivec3 dirty_max;
    uint voxel_res;

    vec3 ambient_color;
    uint pad_end;
} pc;

#define MAX_WG_LIGHTS 128
shared uint s_light_indices[MAX_WG_LIGHTS];
shared uint s_light_count;

// Golden-angle cosine hemisphere direction around +Y (Godot Up)
vec3 cosine_hemisphere_world(uint i, uint n) {
    float r = sqrt((float(i) + 0.5) / float(n));
    float phi = float(i) * 2.39996323; // golden angle
    float y = sqrt(max(0.0, 1.0 - r * r));
    return vec3(r * cos(phi), y, r * sin(phi));
}

// 3D DDA Raymarch through voxel_grid in UVW space [0, 1]
// Returns true if unoccluded until max_dist or exit, false if hit solid/partial voxel
bool trace_dda_unoccluded(vec3 start_uvw, vec3 dir_uvw, float max_dist_uvw) {
    vec3 dir = normalize(dir_uvw);
    float vres = float(pc.voxel_res);
    vec3 pos = start_uvw * vres;
    ivec3 map_pos = clamp(ivec3(floor(pos)), ivec3(0), ivec3(pc.voxel_res - 1));

    vec3 delta_dist = abs(vec3(length(dir)) / max(abs(dir), vec3(1e-6)));
    ivec3 step_dir = ivec3(sign(dir));

    vec3 side_dist;
    side_dist.x = (dir.x > 0.0) ? (float(map_pos.x + 1) - pos.x) * delta_dist.x : (pos.x - float(map_pos.x)) * delta_dist.x;
    side_dist.y = (dir.y > 0.0) ? (float(map_pos.y + 1) - pos.y) * delta_dist.y : (pos.y - float(map_pos.y)) * delta_dist.y;
    side_dist.z = (dir.z > 0.0) ? (float(map_pos.z + 1) - pos.z) * delta_dist.z : (pos.z - float(map_pos.z)) * delta_dist.z;

    float travelled = 0.0;
    float max_voxel_dist = max_dist_uvw * vres;

    // Step a small amount forward to exit the starting voxel
    for (int step = 0; step < 256; step++) {
        if (side_dist.x < side_dist.y) {
            if (side_dist.x < side_dist.z) {
                travelled = side_dist.x;
                side_dist.x += delta_dist.x;
                map_pos.x += step_dir.x;
            } else {
                travelled = side_dist.z;
                side_dist.z += delta_dist.z;
                map_pos.z += step_dir.z;
            }
        } else {
            if (side_dist.y < side_dist.z) {
                travelled = side_dist.y;
                side_dist.y += delta_dist.y;
                map_pos.y += step_dir.y;
            } else {
                travelled = side_dist.z;
                side_dist.z += delta_dist.z;
                map_pos.z += step_dir.z;
            }
        }

        if (travelled >= max_voxel_dist) return true;
        if (map_pos.x < 0 || map_pos.x >= int(pc.voxel_res) ||
            map_pos.y < 0 || map_pos.y >= int(pc.voxel_res) ||
            map_pos.z < 0 || map_pos.z >= int(pc.voxel_res)) {
            return true; // Exited grid unoccluded
        }

        vec3 sample_uvw = (vec3(map_pos) + vec3(0.5)) / vres;
        float occ = textureLod(voxel_grid, sample_uvw, 0.0).a;
        if (occ > 0.1) {
            return false; // Hit solid or partial geometry
        }
    }

    return true;
}

void main() {
    ivec3 cell = pc.dirty_min + ivec3(gl_GlobalInvocationID.xyz);

    // Cooperative Workgroup Light Culling
    if (gl_LocalInvocationIndex == 0) {
        s_light_count = 0;
    }
    barrier();

    // Compute bounding box for this 4x4x4 workgroup in world space
    ivec3 wg_min_cell = pc.dirty_min + ivec3(gl_WorkGroupID.xyz * gl_WorkGroupSize.xyz);
    ivec3 wg_max_cell = wg_min_cell + ivec3(gl_WorkGroupSize.xyz);
    float probe_cell_size = pc.grid_extent / float(pc.probe_res);
    vec3 wg_min_pos = pc.grid_origin + vec3(wg_min_cell) * probe_cell_size;
    vec3 wg_max_pos = pc.grid_origin + vec3(wg_max_cell) * probe_cell_size;

    uint total_lights = light_buf.light_count;
    uint thread_idx = gl_LocalInvocationIndex;
    uint stride = gl_WorkGroupSize.x * gl_WorkGroupSize.y * gl_WorkGroupSize.z;

    for (uint i = thread_idx; i < total_lights; i += stride) {
        vec4 pos_range = light_buf.lights[i].position_range;
        vec3 light_pos = pos_range.xyz;
        float radius = pos_range.w;

        // Distance squared from light_pos to AABB [wg_min_pos, wg_max_pos]
        vec3 clamped = clamp(light_pos, wg_min_pos, wg_max_pos);
        float d2 = dot(light_pos - clamped, light_pos - clamped);
        if (d2 <= radius * radius) {
            uint slot = atomicAdd(s_light_count, 1);
            if (slot < MAX_WG_LIGHTS) {
                s_light_indices[slot] = i;
            }
        }
    }
    barrier();

    // Bounds check for the invocation cell
    if (cell.x > pc.dirty_max.x || cell.y > pc.dirty_max.y || cell.z > pc.dirty_max.z ||
        cell.x >= int(pc.probe_res) || cell.y >= int(pc.probe_res) || cell.z >= int(pc.probe_res)) {
        return;
    }

    vec3 probe_uvw = (vec3(cell) + vec3(0.5)) / float(pc.probe_res);
    vec3 probe_world = pc.grid_origin + probe_uvw * pc.grid_extent;

    // Check if probe is entirely inside solid geometry
    float center_occ = textureLod(voxel_grid, probe_uvw, 0.0).a;
    if (center_occ >= 0.99) {
        imageStore(probe_direct_tex, cell, vec4(0.0));
        return;
    }

    vec3 c1 = vec3(0.0);
    float c0 = (pc.ambient_color.r + pc.ambient_color.g + pc.ambient_color.b) / 3.0;

    // 1. Sun shadow ray
    float sun_lum = (pc.sun_color.r + pc.sun_color.g + pc.sun_color.b) / 3.0 * pc.sun_scale;
    if (sun_lum > 0.001) {
        float max_uvw_dist = pc.max_ray_dist / pc.grid_extent;
        if (trace_dda_unoccluded(probe_uvw, pc.sun_dir, max_uvw_dist)) {
            c0 += 0.25 * sun_lum;
            c1 += 0.5 * pc.sun_dir * sun_lum;
        }
    }

    // 2. Sky hemisphere rays
    float sky_lum = (pc.sky_color.r + pc.sky_color.g + pc.sky_color.b) / 3.0;
    uint ray_count = max(1u, pc.sky_rays);
    float sky_per_ray = sky_lum / float(ray_count);
    float max_uvw_dist = pc.max_ray_dist / pc.grid_extent;

    for (uint r = 0; r < ray_count; r++) {
        vec3 ray_dir = cosine_hemisphere_world(r, ray_count);
        if (trace_dda_unoccluded(probe_uvw, ray_dir, max_uvw_dist)) {
            c0 += 0.25 * sky_per_ray;
            c1 += 0.5 * ray_dir * sky_per_ray;
        }
    }

    // 3. Direct entity lights from shared workgroup list
    uint active_lights = min(s_light_count, uint(MAX_WG_LIGHTS));
    for (uint li = 0; li < active_lights; li++) {
        uint light_idx = s_light_indices[li];
        GpuLight light = light_buf.lights[light_idx];

        vec3 light_pos = light.position_range.xyz;
        float radius = light.position_range.w;
        vec3 to_light = light_pos - probe_world;
        float dist = length(to_light);

        if (dist >= radius || dist < 1e-4) continue;
        vec3 ray_dir = to_light / dist;

        // Spotlight attenuation
        float spot_cos = light.color_spotcos.w;
        float spot_atten = 1.0;
        if (spot_cos > -1.0) {
            vec3 spot_forward = light.spot_dir.xyz;
            float cos_angle = dot(-ray_dir, spot_forward);
            if (cos_angle < spot_cos) continue;
            spot_atten = (cos_angle - spot_cos) / (1.0 - spot_cos);
        }

        float intensity = (1.0 - dist / radius) * spot_atten;
        if (intensity <= 0.0) continue;

        float dist_uvw = dist / pc.grid_extent;
        if (trace_dda_unoccluded(probe_uvw, ray_dir, dist_uvw)) {
            vec3 col = light.color_spotcos.rgb;
            float lum = (col.r + col.g + col.b) / 3.0 * intensity;
            c0 += 0.25 * lum;
            c1 += 0.5 * ray_dir * lum;
        }
    }

    imageStore(probe_direct_tex, cell, vec4(c1, c0));
}
