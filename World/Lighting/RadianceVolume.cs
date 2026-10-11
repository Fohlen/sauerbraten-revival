using System;
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
    /// Run the synchronous Sky + Sun bake pass over the entire volume.
    /// </summary>
    public void BakeSkyAndSun(Octa octa, MapConfig config)
    {
        // Decode lighting parameters from config
        Color ambientColor = DecodeColor(config.Ambient);
        Color skylightColor = DecodeColor(config.Skylight);
        Color sunlightColor = DecodeColor(config.Sunlight);
        float sunlightScale = config.SunlightScale;

        // Base ambient irradiance (minimum diffuse floor)
        // Original engine: ambient is the base floor.
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

                    // Skip probes inside solid geometry (§3)
                    Cube? leaf = octa.LeafCube(cellCoords);
                    if (leaf != null && leaf.IsEntirelySolid())
                        continue;

                    // Base ambient
                    probe.AddAmbient(ambientRadiance);

                    // 1. Sky pass: cast hemisphere rays
                    int unoccludedSkyRays = 0;
                    for (int r = 0; r < rayCount; r++)
                    {
                        float dist = OctaRaycast.RayCube(octa, centerSauer, skyDirsSauer[r], maxRayDist, 0, 0, out _);
                        if (dist >= maxRayDist * 0.999f)
                        {
                            unoccludedSkyRays++;
                            // Add directional sample from this hemisphere direction
                            // Radiance divided by rayCount for Monte Carlo estimate
                            probe.AddDirectional(skyDirsWorld[r], skyRadiance / rayCount);
                        }
                    }

                    // 2. Sun pass: shadow ray toward sun (if sunlight > 0)
                    if (sunRadiance > 0.001f)
                    {
                        float sunDist = OctaRaycast.RayCube(octa, centerSauer, sunDirSauer, maxRayDist, 0, 0, out _);
                        if (sunDist >= maxRayDist * 0.999f)
                        {
                            // Sun is visible: accumulate sun directional radiance
                            probe.AddDirectional(sunDirWorld, sunRadiance);
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
}
