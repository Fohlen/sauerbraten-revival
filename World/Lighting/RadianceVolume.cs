using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// Controller for the L1 SH irradiance probe volume (docs/radiance-hints.md).
/// Manages the underlying <see cref="RadianceGrid"/> and coordinates bake passes.
///
/// Stage 1: Sky + Sun bake pass.
/// Direct sunlight is cast as a shadow ray; unoccluded probes receive sunlight.
/// Sky radiance is estimated via cosine-weighted hemisphere rays against the octree.
/// </summary>
public class RadianceVolume
{
    /// <summary>The underlying world-space probe grid.</summary>
    public RadianceGrid Grid { get; }

    /// <summary>Number of hemisphere rays cast per probe for the sky pass (default 16).</summary>
    public int SkyRays { get; set; } = 16;

    /// <summary>The generated 3D texture resource.</summary>
    public ImageTexture3D? Texture { get; private set; }

    public RadianceVolume(RadianceGrid grid)
    {
        Grid = grid ?? throw new ArgumentNullException(nameof(grid));
    }

    /// <summary>
    /// Factory creating a volume sized from non-empty geometry in <paramref name="octa"/>.
    /// </summary>
    public static RadianceVolume CreateForOcta(Octa octa, int resolution = RadianceGrid.DefaultResolution)
    {
        var grid = RadianceGrid.FromOcta(octa, resolution);
        return new RadianceVolume(grid);
    }

    /// <summary>
    /// Decode a 24-bit RGB packed integer (e.g. 0xRRGGBB) into a normalized Godot Color.
    /// Handles the engine convention: if <= 255, it is a grayscale byte expanded to (c, c, c).
    /// </summary>
    public static Color DecodeColor(int val)
    {
        if (val <= 255)
            val |= (val << 8) | (val << 16);
        float r = ((val >> 16) & 0xFF) / 255.0f;
        float g = ((val >> 8) & 0xFF) / 255.0f;
        float b = (val & 0xFF) / 255.0f;
        return new Color(r, g, b);
    }

    /// <summary>
    /// Compute the Sauerbraten direction vector pointing toward the sun from yaw and pitch (in degrees).
    /// Matches r6889 lightmap.cpp:
    ///   sunlightdir = vec(sunlightyaw * RAD, sunlightpitch * RAD)
    /// where vec(yaw, pitch) = (-sin(yaw)*cos(pitch), cos(yaw)*cos(pitch), sin(pitch)).
    /// </summary>
    public static Vector3 CalculateSunlightDirSauer(float yawDeg, float pitchDeg)
    {
        float yawRad = Mathf.DegToRad(yawDeg);
        float pitchRad = Mathf.DegToRad(pitchDeg);
        float cosPitch = Mathf.Cos(pitchRad);
        var dir = new Vector3(
            -Mathf.Sin(yawRad) * cosPitch,
            Mathf.Cos(yawRad) * cosPitch,
            Mathf.Sin(pitchRad)
        );
        return dir.Normalized();
    }

    /// <summary>
    /// Cosine-weighted hemisphere direction around +Z (Sauerbraten up), using a golden-angle spiral.
    /// Matches docs/radiance-hints.md §4.1 and RadianceBakeProbe.
    /// </summary>
    public static Vector3 CosineHemisphereSauer(int i, int n)
    {
        float r = Mathf.Sqrt((i + 0.5f) / n);
        float phi = i * 2.39996323f; // golden angle
        return new Vector3(r * Mathf.Cos(phi), r * Mathf.Sin(phi), Mathf.Sqrt(Mathf.Max(0f, 1f - r * r)));
    }

    /// <summary>
    /// Run the synchronous bake pass over the entire volume:
    /// Sky + Sun pass (Stage 1), Direct Entity Lights pass (Stage 2),
    /// and Single Bounce Indirect Irradiance pass (Stage 3).
    /// </summary>
    public void Bake(
        Octa octa,
        MapConfig config,
        IReadOnlyList<DirectLightSource>? directLights = null,
        MetadataCatalogue? catalogue = null,
        ITextureSource? textureSource = null)
    {
        // Decode lighting parameters from config
        Color ambientColor = DecodeColor(config.Ambient);
        Color skylightColor = DecodeColor(config.Skylight);
        Color sunlightColor = DecodeColor(config.Sunlight);
        float sunlightScale = config.SunlightScale;

        // Base ambient irradiance (minimum diffuse floor)
        float ambientRadiance = (ambientColor.R + ambientColor.G + ambientColor.B) / 3.0f;

        // Effective skylight radiance above ambient
        Color effectiveSkyColor = new(
            Mathf.Max(skylightColor.R, ambientColor.R),
            Mathf.Max(skylightColor.G, ambientColor.G),
            Mathf.Max(skylightColor.B, ambientColor.B)
        );
        float skyRadiance = (effectiveSkyColor.R + effectiveSkyColor.G + effectiveSkyColor.B) / 3.0f;

        // Sun direction (in Sauerbraten space: pointing toward the sun)
        Vector3 sunDirSauer = CalculateSunlightDirSauer(config.SunlightYaw, config.SunlightPitch);
        // Sun direction in Godot world space
        Vector3 sunDirWorld = RadianceGrid.SauerToWorld(sunDirSauer);

        // Sunlight radiance (scaled)
        float sunRadiance = ((sunlightColor.R + sunlightColor.G + sunlightColor.B) / 3.0f) * sunlightScale;

        // Hemisphere ray directions in Sauerbraten and World space
        int rayCount = Mathf.Max(1, SkyRays);
        var skyDirsSauer = new Vector3[rayCount];
        var skyDirsWorld = new Vector3[rayCount];
        for (int i = 0; i < rayCount; i++)
        {
            skyDirsSauer[i] = CosineHemisphereSauer(i, rayCount);
            skyDirsWorld[i] = RadianceGrid.SauerToWorld(skyDirsSauer[i]);
        }

        // Spatial index for direct lights (Stage 2)
        LightSpatialIndex? spatialIndex = null;
        if (directLights != null && directLights.Count > 0)
        {
            // Grid bounds in Sauerbraten coordinates
            Aabb boundsSauer = RadianceGrid.WorldBoundsToSauer(Grid.Bounds);
            spatialIndex = new LightSpatialIndex(boundsSauer, directLights);
        }

        var candidateLights = new List<DirectLightSource>();

        // Cache average albedos for slots/vslots in catalogue to avoid repeated calculation
        var albedoCache = new Dictionary<int, float>();

        // Raycast max distance: extent * 1.5 reaches the world bounds
        float maxRayDist = Grid.Extent * 1.5f;

        int res = Grid.Resolution;

        for (int z = 0; z < res; z++)
        {
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    ref var probe = ref Grid[x, y, z];
                    probe = RadianceProbe.Zero;

                    Vector3 centerSauer = Grid.CellCenterSauer(x, y, z);
                    var cellCoords = new Vector3I(Mathf.FloorToInt(centerSauer.X), Mathf.FloorToInt(centerSauer.Y), Mathf.FloorToInt(centerSauer.Z));

                    // Skip probes inside solid geometry (docs/radiance-hints.md §3)
                    Cube? leaf = octa.LeafCube(cellCoords);
                    if (leaf != null && leaf.IsEntirelySolid())
                        continue;

                    // Base ambient
                    probe.AddAmbient(ambientRadiance);

                    // 1. Sky pass: cast hemisphere rays (Stage 1 unoccluded sky + Stage 3 indirect bounce)
                    for (int r = 0; r < rayCount; r++)
                    {
                        Vector3 rayDir = skyDirsSauer[r];
                        float dist = OctaRaycast.RayCube(octa, centerSauer, rayDir, maxRayDist, 0, 0, out Vector3 hitSurface);
                        if (dist >= maxRayDist * 0.999f)
                        {
                            // Unoccluded sky: radiance divided by rayCount for Monte Carlo estimate
                            probe.AddDirectional(skyDirsWorld[r], skyRadiance / rayCount);
                        }
                        else
                        {
                            // Stage 3: Hit geometry within maxRayDist -> single bounce indirect irradiance
                            Vector3 hitPos = centerSauer + rayDir * dist;
                            Vector3 hitNormal = hitSurface != Vector3.Zero ? hitSurface.Normalized() : -rayDir;

                            // Estimate surface albedo
                            float albedo = ResolveAlbedoAtHit(octa, hitPos, hitNormal, catalogue, textureSource, albedoCache);

                            // Incident radiance on hit surface from sky/sun:
                            // Upper hemisphere factor facing +Z in Sauer space:
                            float skyCos = Mathf.Max(0f, hitNormal.Z);
                            float incSkyRadiance = skyRadiance * skyCos;

                            // Direct sun incident on hit surface:
                            float incSunRadiance = 0f;
                            if (sunRadiance > 0.001f)
                            {
                                float nDotL = hitNormal.Dot(sunDirSauer);
                                if (nDotL > 0f)
                                {
                                    float sunShadow = OctaRaycast.RayCube(octa, hitPos + hitNormal * 0.1f, sunDirSauer, maxRayDist, 0, 0, out _);
                                    if (sunShadow >= maxRayDist * 0.999f)
                                    {
                                        incSunRadiance = sunRadiance * nDotL;
                                    }
                                }
                            }

                            // Reflected diffuse radiance arriving from hit surface towards probe:
                            // Reflected radiance = (incident * albedo) / rayCount.
                            // Arriving along skyDirsWorld[r] from hit point.
                            float bounceRadiance = (incSkyRadiance + incSunRadiance) * albedo / rayCount;
                            if (bounceRadiance > 1e-5f)
                            {
                                probe.AddDirectional(skyDirsWorld[r], bounceRadiance);
                            }
                        }
                    }

                    // 2. Sun pass: shadow ray toward sun (if sunlight > 0)
                    if (sunRadiance > 0.001f)
                    {
                        float sunDist = OctaRaycast.RayCube(octa, centerSauer, sunDirSauer, maxRayDist, 0, 0, out _);
                        if (sunDist >= maxRayDist * 0.999f)
                        {
                            probe.AddDirectional(sunDirWorld, sunRadiance);
                        }
                    }

                    // 3. Direct entity lights pass (Stage 2)
                    if (spatialIndex != null)
                    {
                        spatialIndex.QueryLights(centerSauer, candidateLights);
                        for (int li = 0; li < candidateLights.Count; li++)
                        {
                            var light = candidateLights[li];
                            Vector3 lightVec = light.Position - centerSauer;
                            float dist = lightVec.Length();

                            if (dist < 1e-4f || dist >= light.Radius)
                                continue;

                            Vector3 rayDir = lightVec / dist; // normalized direction toward light

                            // Spotlight cone attenuation check
                            float spotAtten = 1.0f;
                            if (light.IsSpotlight)
                            {
                                // Vector from light to probe is -rayDir
                                float cosAngle = (-rayDir).Dot(light.SpotDirection);
                                if (cosAngle < light.SpotCosAngle)
                                    continue;
                                spotAtten = (cosAngle - light.SpotCosAngle) / (1.0f - light.SpotCosAngle);
                            }

                            // Linear distance attenuation matching Sauerbraten: intensity = 1 - dist / radius
                            float intensity = (1.0f - dist / light.Radius) * spotAtten;
                            if (intensity <= 0f)
                                continue;

                            // Cast shadow ray from probe to light
                            float hitDist = OctaRaycast.RayCube(octa, centerSauer, rayDir, dist, 0, 0, out _);
                            if (hitDist >= dist * 0.999f)
                            {
                                // Unoccluded: accumulate into probe in Godot world space
                                Vector3 rayDirWorld = RadianceGrid.SauerToWorld(rayDir);
                                float lightLum = (light.Color.R + light.Color.G + light.Color.B) / 3.0f;
                                probe.AddDirectional(rayDirWorld, lightLum * intensity);
                            }
                        }
                    }
                }
            }
        }

        // Create or update GPU 3D texture
        if (Texture == null)
            Texture = Grid.CreateTexture();
        else
            Grid.UpdateTexture(Texture);
    }

    /// <summary>
    /// Default neutral surface albedo (docs/radiance-hints.md §4.3).
    /// </summary>
    public const float DefaultAlbedo = 0.5f;

    /// <summary>
    /// Resolves the diffuse albedo at a hit location from the hit cube's texture/vslot or catalogue.
    /// </summary>
    private static float ResolveAlbedoAtHit(
        Octa octa,
        Vector3 hitPos,
        Vector3 hitNormal,
        MetadataCatalogue? catalogue,
        ITextureSource? textureSource,
        Dictionary<int, float> albedoCache)
    {
        // Step slightly inside the hit surface along -hitNormal to find the leaf cube
        Vector3 inside = hitPos - hitNormal * 0.05f;
        var hitCoords = new Vector3I(Mathf.FloorToInt(inside.X), Mathf.FloorToInt(inside.Y), Mathf.FloorToInt(inside.Z));
        Cube? hitCube = octa.LeafCube(hitCoords);
        if (hitCube == null)
            return DefaultAlbedo;

        // Determine orientation from normal
        int orient = NormalToOrient(hitNormal);
        int vslotIndex = hitCube.Texture[orient];

        if (albedoCache.TryGetValue(vslotIndex, out float cached))
            return cached;

        float albedo = ComputeVSlotAlbedo(vslotIndex, catalogue, textureSource);
        albedoCache[vslotIndex] = albedo;
        return albedo;
    }

    /// <summary>
    /// Determine face orientation (0..5: Left, Right, Back, Front, Bottom, Top) from normal vector in Sauer space.
    /// </summary>
    public static int NormalToOrient(Vector3 n)
    {
        float absX = Mathf.Abs(n.X);
        float absY = Mathf.Abs(n.Y);
        float absZ = Mathf.Abs(n.Z);

        if (absX >= absY && absX >= absZ)
            return n.X < 0 ? 0 : 1;
        if (absY >= absX && absY >= absZ)
            return n.Y < 0 ? 2 : 3;
        return n.Z < 0 ? 4 : 5;
    }

    /// <summary>
    /// Computes the average scalar diffuse albedo for a vslot, taking into account
    /// catalogue vslot tint and optional average texture color from textureSource.
    /// Falls back to <see cref="DefaultAlbedo"/> (0.5).
    /// </summary>
    public static float ComputeVSlotAlbedo(int vslotIndex, MetadataCatalogue? catalogue, ITextureSource? textureSource)
    {
        if (catalogue == null)
            return DefaultAlbedo;

        if (catalogue.VSlots.TryGetValue(vslotIndex, out var vslot))
        {
            float tintLum = (vslot.Color.X + vslot.Color.Y + vslot.Color.Z) / 3.0f;
            float texLum = 1.0f;

            if (textureSource != null && catalogue.Slots.TryGetValue(vslot.Slot, out var slot) && slot?.DiffusePath != null)
            {
                texLum = ComputeAverageTextureLuminance(textureSource, slot.DiffusePath);
            }

            return Mathf.Clamp(tintLum * texLum, 0.05f, 1.0f);
        }

        return DefaultAlbedo;
    }

    private static readonly Dictionary<string, float> _texLuminanceCache = new();

    /// <summary>
    /// Sample a coarse grid (e.g. 8x8 or 4x4) of pixels from an image to estimate its average luminance.
    /// </summary>
    private static float ComputeAverageTextureLuminance(ITextureSource textureSource, string diffusePath)
    {
        if (_texLuminanceCache.TryGetValue(diffusePath, out float cached))
            return cached;

        Image? img = textureSource.GetImage(diffusePath);
        if (img == null || img.IsEmpty())
        {
            _texLuminanceCache[diffusePath] = 1.0f;
            return 1.0f;
        }

        int w = img.GetWidth();
        int h = img.GetHeight();
        if (w <= 0 || h <= 0)
        {
            _texLuminanceCache[diffusePath] = 1.0f;
            return 1.0f;
        }

        int stepX = Mathf.Max(1, w / 8);
        int stepY = Mathf.Max(1, h / 8);
        float sum = 0f;
        int count = 0;

        for (int y = 0; y < h; y += stepY)
        {
            for (int x = 0; x < w; x += stepX)
            {
                Color c = img.GetPixel(x, y);
                sum += (c.R + c.G + c.B) / 3.0f;
                count++;
            }
        }

        float avg = count > 0 ? sum / count : 1.0f;
        _texLuminanceCache[diffusePath] = avg;
        return avg;
    }

    /// <summary>
    /// Backward-compatible alias for <see cref="Bake"/> without entity lights.
    /// </summary>
    public void BakeSkyAndSun(Octa octa, MapConfig config) => Bake(octa, config, null, null, null);
}
