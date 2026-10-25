#[compute]
#version 450

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(set = 0, binding = 0) uniform sampler3D voxel_grid;
layout(set = 0, binding = 1) uniform sampler3D probe_direct_sampler;
layout(set = 0, binding = 2, rgba16f) uniform restrict writeonly image3D probe_final_tex;

layout(push_constant, std430) uniform PushConstants {
    vec3 grid_origin;
    float grid_extent;

    vec3 sky_tint;
    uint probe_res;

    uint sky_rays;
    float max_ray_dist;
    uint voxel_res;
    uint pad0;

    ivec3 dirty_min;
    int pad1;

    ivec3 dirty_max;
    int pad2;
} pc;

vec3 cosine_hemisphere_world(uint i, uint n) {
    float r = sqrt((float(i) + 0.5) / float(n));
    float phi = float(i) * 2.39996323;
    float y = sqrt(max(0.0, 1.0 - r * r));
    return vec3(r * cos(phi), y, r * sin(phi));
}

// 3D DDA Raymarch recording hit UVW, normal, and albedo
bool trace_dda_hit(vec3 start_uvw, vec3 dir_uvw, float max_dist_uvw, out vec3 hit_uvw, out vec3 hit_normal, out vec3 hit_albedo) {
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
    ivec3 step_axis = ivec3(0);

    for (int step = 0; step < 128; step++) {
        if (side_dist.x < side_dist.y) {
            if (side_dist.x < side_dist.z) {
                travelled = side_dist.x;
                side_dist.x += delta_dist.x;
                map_pos.x += step_dir.x;
                step_axis = ivec3(-step_dir.x, 0, 0);
            } else {
                travelled = side_dist.z;
                side_dist.z += delta_dist.z;
                map_pos.z += step_dir.z;
                step_axis = ivec3(0, 0, -step_dir.z);
            }
        } else {
            if (side_dist.y < side_dist.z) {
                travelled = side_dist.y;
                side_dist.y += delta_dist.y;
                map_pos.y += step_dir.y;
                step_axis = ivec3(0, -step_dir.y, 0);
            } else {
                travelled = side_dist.z;
                side_dist.z += delta_dist.z;
                map_pos.z += step_dir.z;
                step_axis = ivec3(0, 0, -step_dir.z);
            }
        }

        if (travelled >= max_voxel_dist) return false;
        if (map_pos.x < 0 || map_pos.x >= int(pc.voxel_res) ||
            map_pos.y < 0 || map_pos.y >= int(pc.voxel_res) ||
            map_pos.z < 0 || map_pos.z >= int(pc.voxel_res)) {
            return false;
        }

        vec3 sample_uvw = (vec3(map_pos) + vec3(0.5)) / vres;
        vec4 vox = textureLod(voxel_grid, sample_uvw, 0.0);
        if (vox.a > 0.1) {
            hit_uvw = sample_uvw;
            hit_normal = vec3(step_axis);
            hit_albedo = vox.rgb;
            return true;
        }
    }

    return false;
}

void main() {
    ivec3 cell = pc.dirty_min + ivec3(gl_GlobalInvocationID.xyz);

    if (cell.x > pc.dirty_max.x || cell.y > pc.dirty_max.y || cell.z > pc.dirty_max.z ||
        cell.x >= int(pc.probe_res) || cell.y >= int(pc.probe_res) || cell.z >= int(pc.probe_res)) {
        return;
    }

    vec3 probe_uvw = (vec3(cell) + vec3(0.5)) / float(pc.probe_res);

    // Read direct irradiance from Pass 1
    vec4 direct_sample = textureLod(probe_direct_sampler, probe_uvw, 0.0);
    vec3 c1 = direct_sample.rgb;
    float c0 = direct_sample.a;

    // Solid rejection check
    float center_occ = textureLod(voxel_grid, probe_uvw, 0.0).a;
    if (center_occ >= 0.99) {
        imageStore(probe_final_tex, cell, vec4(0.0));
        return;
    }

    uint ray_count = max(1u, pc.sky_rays);
    float max_uvw_dist = pc.max_ray_dist / pc.grid_extent;
    float probe_texel = 1.0 / float(pc.probe_res);

    vec3 bounce_c1 = vec3(0.0);
    float bounce_c0 = 0.0;

    for (uint r = 0; r < ray_count; r++) {
        vec3 ray_dir = cosine_hemisphere_world(r, ray_count);
        vec3 hit_uvw, hit_normal, hit_albedo;
        if (trace_dda_hit(probe_uvw, ray_dir, max_uvw_dist, hit_uvw, hit_normal, hit_albedo)) {
            // Sample direct probe volume slightly in front of hit surface
            vec3 sample_pos = hit_uvw + hit_normal * (probe_texel * 0.75);
            vec4 surf_direct = textureLod(probe_direct_sampler, sample_pos, 0.0);

            // Outgoing diffuse irradiance E(n) = max(sky_tint * amb + dot(c1, normal), 0.0)
            float n_dot_c1 = dot(surf_direct.rgb, hit_normal);
            float surf_irr = max(surf_direct.a * ((pc.sky_tint.r + pc.sky_tint.g + pc.sky_tint.b) / 3.0) + n_dot_c1, 0.0);

            // Reflected bounce luminance
            float albedo_lum = (hit_albedo.r + hit_albedo.g + hit_albedo.b) / 3.0;
            float bounce_lum = (surf_irr * albedo_lum) / float(ray_count);

            bounce_c0 += 0.25 * bounce_lum;
            bounce_c1 += 0.5 * ray_dir * bounce_lum;
        }
    }

    imageStore(probe_final_tex, cell, vec4(c1 + bounce_c1, c0 + bounce_c0));
}
