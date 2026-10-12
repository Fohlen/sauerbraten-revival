using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// Controller for the L1 SH irradiance probe volume (docs/adr/0002-radiance-hints.md).
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

    /// <summary>Quality and performance settings for this radiance volume.</summary>
    public RadianceSettings Settings { get; set; }

    /// <summary>Global quality mode (forwarded to <see cref="Settings"/>).</summary>
    public RadianceQualityMode Mode
    {
        get => Settings.Mode;
        set => Settings.Mode = value;
    }

    /// <summary>Number of hemisphere rays cast per probe for the sky pass (forwarded to <see cref="Settings"/>).</summary>
    public int SkyRays
    {
        get => Settings.SkyRays;
        set => Settings.SkyRays = value;
    }

    /// <summary>Maximum probes rebaked per frame during incremental updates (forwarded to <see cref="Settings"/>).</summary>
    public int BudgetPerFrame
    {
        get => Settings.BudgetPerFrame;
        set => Settings.BudgetPerFrame = value;
    }

    /// <summary>Maximum raycast travel distance (forwarded to <see cref="Settings"/>).</summary>
    public float? MaxRayDistance
    {
        get => Settings.MaxRayDistance;
        set => Settings.MaxRayDistance = value;
    }

    /// <summary>The generated 3D texture resource.</summary>
    public ImageTexture3D? Texture { get; private set; }

    public RadianceVolume(RadianceGrid grid, RadianceSettings? settings = null)
    {
        Grid = grid ?? throw new ArgumentNullException(nameof(grid));
        Settings = settings ?? new RadianceSettings();
    }

    /// <summary>
    /// Factory creating a volume sized from non-empty geometry in <paramref name="octa"/>.
    /// </summary>
    public static RadianceVolume CreateForOcta(Octa octa, RadianceSettings? settings = null)
    {
        var s = settings ?? new RadianceSettings();
        var grid = RadianceGrid.FromOcta(octa, s.Resolution);
        return new RadianceVolume(grid, s);
    }

    /// <summary>
    /// Factory creating a volume sized from non-empty geometry in <paramref name="octa"/> with custom resolution.
    /// </summary>
    public static RadianceVolume CreateForOcta(Octa octa, int resolution)
    {
        return CreateForOcta(octa, new RadianceSettings { Resolution = resolution });
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
    /// Matches docs/adr/0002-radiance-hints.md §4.1 and RadianceBakeProbe.
    /// </summary>
    public static Vector3 CosineHemisphereSauer(int i, int n)
    {
        float r = Mathf.Sqrt((i + 0.5f) / n);
        float phi = i * 2.39996323f; // golden angle
        return new Vector3(r * Mathf.Cos(phi), r * Mathf.Sin(phi), Mathf.Sqrt(Mathf.Max(0f, 1f - r * r)));
    }


    /// <summary>Number of dirty probes currently queued for rebaking.</summary>
    public int PendingCount => _dirtyQueue.Count;

    /// <summary>Total probes rebaked so far across all incremental updates (diagnostics/tests).</summary>
    public int RebakeCount { get; private set; }

    /// <summary>Internal context caching baking inputs for incremental updates.</summary>
    private sealed class BakeContext
    {
        public Octa Octa { get; }
        public MapConfig Config { get; }
        public RadianceSettings Settings { get; }
        public LightSpatialIndex? SpatialIndex { get; }
        public MetadataCatalogue? Catalogue { get; }
        public ITextureSource? TextureSource { get; }
        public Dictionary<int, float> AlbedoCache { get; }
        public List<DirectLightSource> CandidateLights { get; }
        public Vector3[] SkyDirsSauer { get; }
        public Vector3[] SkyDirsWorld { get; }
        public Vector3 SunDirSauer { get; }
        public Vector3 SunDirWorld { get; }
        public float AmbientRadiance { get; }
        public float SkyRadiance { get; }
        public float SunRadiance { get; }
        public float MaxRayDist { get; }
        public int RayCount { get; }

        public BakeContext(
            Octa octa,
            MapConfig config,
            IReadOnlyList<DirectLightSource>? directLights,
            MetadataCatalogue? catalogue,
            ITextureSource? textureSource,
            RadianceSettings settings,
            RadianceGrid grid)
        {
            Octa = octa;
            Config = config;
            Settings = settings;
            Catalogue = catalogue;
            TextureSource = textureSource;
            AlbedoCache = new Dictionary<int, float>();
            CandidateLights = new List<DirectLightSource>();

            Color ambientColor = DecodeColor(config.Ambient);
            Color skylightColor = DecodeColor(config.Skylight);
            Color sunlightColor = DecodeColor(config.Sunlight);
            float sunlightScale = config.SunlightScale;

            AmbientRadiance = (ambientColor.R + ambientColor.G + ambientColor.B) / 3.0f;

            Color effectiveSkyColor = new(
                Mathf.Max(skylightColor.R, ambientColor.R),
                Mathf.Max(skylightColor.G, ambientColor.G),
                Mathf.Max(skylightColor.B, ambientColor.B)
            );
            SkyRadiance = (effectiveSkyColor.R + effectiveSkyColor.G + effectiveSkyColor.B) / 3.0f;

            SunDirSauer = CalculateSunlightDirSauer(config.SunlightYaw, config.SunlightPitch);
            SunDirWorld = RadianceGrid.SauerToWorld(SunDirSauer);
            SunRadiance = ((sunlightColor.R + sunlightColor.G + sunlightColor.B) / 3.0f) * sunlightScale;

            RayCount = Mathf.Max(1, settings.SkyRays);
            SkyDirsSauer = new Vector3[RayCount];
            SkyDirsWorld = new Vector3[RayCount];
            for (int i = 0; i < RayCount; i++)
            {
                SkyDirsSauer[i] = CosineHemisphereSauer(i, RayCount);
                SkyDirsWorld[i] = RadianceGrid.SauerToWorld(SkyDirsSauer[i]);
            }

            if (directLights != null && directLights.Count > 0)
            {
                Aabb boundsSauer = RadianceGrid.WorldBoundsToSauer(grid.Bounds);
                SpatialIndex = new LightSpatialIndex(boundsSauer, directLights);
            }

            MaxRayDist = settings.MaxRayDistance is > 0f ? settings.MaxRayDistance.Value : grid.Extent * 1.5f;
        }
    }

    private BakeContext? _context;
    private readonly List<Vector3I> _dirtyQueue = new();
    private readonly HashSet<Vector3I> _dirtySet = new();
    private bool _textureNeedsUpdate;

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
        _context = new BakeContext(octa, config, directLights, catalogue, textureSource, Settings, Grid);
        _dirtyQueue.Clear();
        _dirtySet.Clear();

        if (Settings.Mode == RadianceQualityMode.StaticAmbient)
        {
            Grid.Fill(new RadianceProbe(Vector3.Zero, _context.AmbientRadiance));
            if (Texture == null)
                Texture = Grid.CreateTexture();
            else
                Grid.UpdateTexture(Texture);

            _textureNeedsUpdate = false;
            return;
        }

        int res = Grid.Resolution;
        for (int z = 0; z < res; z++)
        {
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    ref var probe = ref Grid[x, y, z];
                    BakeProbe(ref probe, x, y, z, _context);
                }
            }
        }

        // Create or update GPU 3D texture
        if (Texture == null)
            Texture = Grid.CreateTexture();
        else
            Grid.UpdateTexture(Texture);

        _textureNeedsUpdate = false;
    }

    /// <summary>
    /// Bakes or rebakes a single probe at grid coordinates (x, y, z).
    /// </summary>
    private void BakeProbe(ref RadianceProbe probe, int x, int y, int z, BakeContext ctx)
    {
        probe = RadianceProbe.Zero;

        if (ctx.Settings.Mode == RadianceQualityMode.StaticAmbient)
        {
            probe.AddAmbient(ctx.AmbientRadiance);
            return;
        }

        Vector3 centerSauer = Grid.CellCenterSauer(x, y, z);
        var cellCoords = new Vector3I(Mathf.FloorToInt(centerSauer.X), Mathf.FloorToInt(centerSauer.Y), Mathf.FloorToInt(centerSauer.Z));

        // Skip probes inside solid geometry (docs/adr/0002-radiance-hints.md §3)
        Cube? leaf = ctx.Octa.LeafCube(cellCoords);
        if (leaf != null && leaf.IsEntirelySolid())
            return;

        // Base ambient
        probe.AddAmbient(ctx.AmbientRadiance);

        // 1. Sky pass: cast hemisphere rays (Stage 1 unoccluded sky + Stage 3 indirect bounce)
        for (int r = 0; r < ctx.RayCount; r++)
        {
            Vector3 rayDir = ctx.SkyDirsSauer[r];
            float dist = OctaRaycast.RayCube(ctx.Octa, centerSauer, rayDir, ctx.MaxRayDist, 0, 0, out Vector3 hitSurface);
            if (dist >= ctx.MaxRayDist * 0.999f)
            {
                // Unoccluded sky: radiance divided by rayCount for Monte Carlo estimate
                probe.AddDirectional(ctx.SkyDirsWorld[r], ctx.SkyRadiance / ctx.RayCount);
            }
            else
            {
                // Stage 3: Hit geometry within maxRayDist -> single bounce indirect irradiance
                Vector3 hitPos = centerSauer + rayDir * dist;
                Vector3 hitNormal = hitSurface != Vector3.Zero ? hitSurface.Normalized() : -rayDir;

                // Estimate surface albedo
                float albedo = ResolveAlbedoAtHit(ctx.Octa, hitPos, hitNormal, ctx.Catalogue, ctx.TextureSource, ctx.AlbedoCache);

                // Incident radiance on hit surface from sky/sun:
                // Upper hemisphere factor facing +Z in Sauer space:
                float skyCos = Mathf.Max(0f, hitNormal.Z);
                float incSkyRadiance = ctx.SkyRadiance * skyCos;

                // Direct sun incident on hit surface:
                float incSunRadiance = 0f;
                if (ctx.SunRadiance > 0.001f)
                {
                    float nDotL = hitNormal.Dot(ctx.SunDirSauer);
                    if (nDotL > 0f)
                    {
                        float sunShadow = OctaRaycast.RayCube(ctx.Octa, hitPos + hitNormal * 0.1f, ctx.SunDirSauer, ctx.MaxRayDist, 0, 0, out _);
                        if (sunShadow >= ctx.MaxRayDist * 0.999f)
                        {
                            incSunRadiance = ctx.SunRadiance * nDotL;
                        }
                    }
                }

                // Reflected diffuse radiance arriving from hit surface towards probe:
                // Reflected radiance = (incident * albedo) / rayCount.
                // Arriving along skyDirsWorld[r] from hit point.
                float bounceRadiance = (incSkyRadiance + incSunRadiance) * albedo / ctx.RayCount;
                if (bounceRadiance > 1e-5f)
                {
                    probe.AddDirectional(ctx.SkyDirsWorld[r], bounceRadiance);
                }
            }
        }

        // 2. Sun pass: shadow ray toward sun (if sunlight > 0)
        if (ctx.SunRadiance > 0.001f)
        {
            float sunDist = OctaRaycast.RayCube(ctx.Octa, centerSauer, ctx.SunDirSauer, ctx.MaxRayDist, 0, 0, out _);
            if (sunDist >= ctx.MaxRayDist * 0.999f)
            {
                probe.AddDirectional(ctx.SunDirWorld, ctx.SunRadiance);
            }
        }

        // 3. Direct entity lights pass (Stage 2)
        if (ctx.SpatialIndex != null)
        {
            ctx.SpatialIndex.QueryLights(centerSauer, ctx.CandidateLights);
            for (int li = 0; li < ctx.CandidateLights.Count; li++)
            {
                var light = ctx.CandidateLights[li];
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
                float hitDist = OctaRaycast.RayCube(ctx.Octa, centerSauer, rayDir, dist, 0, 0, out _);
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

    /// <summary>
    /// Mark all probes overlapping the given region dirty.
    /// Region is in Sauerbraten coordinates (matching EditorController / WorldChunkRenderer).
    /// An optional margin (in world units) expands the region before mapping to probe cells.
    /// </summary>
    public void MarkDirty(Aabb sauerRegion, float margin = 0f)
    {
        if (margin > 0f)
            sauerRegion = sauerRegion.Grow(margin);

        // Convert Sauerbraten AABB to Godot World AABB
        Aabb worldRegion = RadianceGrid.SauerBoundsToWorld(sauerRegion);

        // Find cell coordinate bounds
        Vector3I minCell = Grid.WorldToCell(worldRegion.Position);
        Vector3I maxCell = Grid.WorldToCell(worldRegion.End);

        int res = Grid.Resolution;
        int x0 = Mathf.Clamp(Mathf.Min(minCell.X, maxCell.X), 0, res - 1);
        int y0 = Mathf.Clamp(Mathf.Min(minCell.Y, maxCell.Y), 0, res - 1);
        int z0 = Mathf.Clamp(Mathf.Min(minCell.Z, maxCell.Z), 0, res - 1);
        int x1 = Mathf.Clamp(Mathf.Max(minCell.X, maxCell.X), 0, res - 1);
        int y1 = Mathf.Clamp(Mathf.Max(minCell.Y, maxCell.Y), 0, res - 1);
        int z1 = Mathf.Clamp(Mathf.Max(minCell.Z, maxCell.Z), 0, res - 1);

        for (int z = z0; z <= z1; z++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    MarkCellDirty(new Vector3I(x, y, z));
                }
            }
        }
    }

    /// <summary>
    /// Mark a single probe cell at (x, y, z) dirty.
    /// </summary>
    public void MarkCellDirty(Vector3I cell)
    {
        if (cell.X < 0 || cell.X >= Grid.Resolution ||
            cell.Y < 0 || cell.Y >= Grid.Resolution ||
            cell.Z < 0 || cell.Z >= Grid.Resolution)
            return;

        if (_dirtySet.Add(cell))
        {
            _dirtyQueue.Add(cell);
        }
    }

    /// <summary>
    /// Rebakes up to <paramref name="budget"/> queued dirty probes.
    /// If any probes were rebaked and changed, updates the GPU 3D texture (at most once per call).
    /// </summary>
    public int RebakePending(int budget)
    {
        if (_dirtyQueue.Count == 0 || _context == null)
            return 0;

        int count = Mathf.Min(budget, _dirtyQueue.Count);
        for (int i = 0; i < count; i++)
        {
            Vector3I cell = _dirtyQueue[0];
            _dirtyQueue.RemoveAt(0);
            _dirtySet.Remove(cell);

            ref var probe = ref Grid[cell.X, cell.Y, cell.Z];
            BakeProbe(ref probe, cell.X, cell.Y, cell.Z, _context);
            RebakeCount++;
            _textureNeedsUpdate = true;
        }

        if (_textureNeedsUpdate && Texture != null)
        {
            Grid.UpdateTexture(Texture);
            _textureNeedsUpdate = false;
        }

        return count;
    }

    /// <summary>
    /// Process incremental rebake updates for one frame according to <see cref="BudgetPerFrame"/>.
    /// </summary>
    public void Process(double delta)
    {
        if (_dirtyQueue.Count == 0) return;
        RebakePending(BudgetPerFrame);
    }

    /// <summary>
    /// Immediately rebakes all remaining dirty probes in the queue and updates the GPU texture.
    /// Intended for synchronous tests, save/export, or level initialization.
    /// </summary>
    public void FlushDirty()
    {
        while (_dirtyQueue.Count > 0)
        {
            RebakePending(_dirtyQueue.Count);
        }
    }

    /// <summary>
    /// Default neutral surface albedo (docs/adr/0002-radiance-hints.md §4.3).
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
