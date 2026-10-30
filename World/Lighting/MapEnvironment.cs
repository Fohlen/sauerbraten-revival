using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// Materializes a map's <see cref="SetEnvironmentCmd"/> into the live scene tree
/// (docs/adr/0009-reference-based-maps.md §6).
///
/// The command is the single source of truth for sun, sky, ambient and fog. It is
/// persisted in <c>KeyframePackage.environment</c>, replayed from trailing
/// <c>WorldOp.SetEnvironment</c> deltas, and streamed to peers — no
/// scene file is synced. To render it, this helper maps the command
/// onto two Godot nodes owned by the map node:
///
/// <list type="bullet">
///   <item><description>
///     a <see cref="DirectionalLight3D"/> (<c>Sun</c>) whose direction comes from
///     the engine's <c>sunlightyaw</c>/<c>sunlightpitch</c> (lightmap.cpp
///     <c>setsunlightdir</c>), and
///   </description></item>
///   <item><description>
///     a <see cref="WorldEnvironment"/> with the cubemap skybox (when the command
///     names one) or a procedural sky tinted by <c>skylight</c>/<c>ambient</c>,
///     plus distance fog.
///   </description></item>
/// </list>
///
/// Both live at runtime only (<see cref="Node.Owner"/> is <c>null</c>), exactly
/// like the entity-light projections built by <see cref="LightBuilder"/>.
/// </summary>
public static class MapEnvironment
{
    /// <summary>Direct-child name of the environment the map node owns/updates.</summary>
    public const string EnvironmentNodeName = "WorldEnvironment";

    /// <summary>Direct-child name of the sun the map node owns/updates.</summary>
    public const string SunNodeName = "Sun";

    /// <summary>Default sun colour used by the canonical empty map (engine <c>sunlight</c>).</summary>
    public const uint DefaultSunColor = 0xE8E8E8;

    /// <summary>Default sun yaw in degrees (engine <c>sunlightyaw</c>).</summary>
    public const float DefaultSunYaw = 315f;

    /// <summary>Default sun pitch in degrees (engine <c>sunlightpitch</c>).</summary>
    public const float DefaultSunPitch = 45f;

    /// <summary>Default ambient colour (engine <c>ambient</c>).</summary>
    public const uint DefaultAmbientColor = 0x191919;

    /// <summary>
    /// The environment used when a map carries no <see cref="SetEnvironmentCmd"/>.
    /// The engine's own defaults have <c>sunlight = 0</c> (no sun), which would leave
    /// a freshly created map unlit; the canonical empty map instead ships a visible
    /// single sun, matching the programmatic <see cref="Editor.EmptyMap"/> defaults.
    /// </summary>
    public static SetEnvironmentCmd CreateDefault() => new()
    {
        SunColor = DefaultSunColor,
        SunScale = 1.0f,
        SunYaw = DefaultSunYaw,
        SunPitch = DefaultSunPitch,
        // Engine skylight defaults to 0 (the classic skybox supplies the colour);
        // native maps without a cubemap need a visible procedural sky instead.
        SkyColor = 0x8099B3,
        AmbientColor = DefaultAmbientColor,
        Skybox = "",
        FogDistance = 0f,
        FogColor = 0x8099B3,
    };

    /// <summary>
    /// Projects the command onto the radiance-bake configuration used by
    /// <see cref="RadianceVolume"/> (same fields, engine colour encoding).
    /// </summary>
    public static MapConfig ToConfig(SetEnvironmentCmd? env)
    {
        env ??= CreateDefault();
        return new MapConfig
        {
            Sunlight = (int)env.SunColor,
            SunlightScale = env.SunScale,
            SunlightYaw = (int)env.SunYaw,
            SunlightPitch = (int)env.SunPitch,
            Skylight = (int)env.SkyColor,
            Ambient = (int)env.AmbientColor,
            SkyboxColour = (int)env.SkyColor,
            Skybox = env.Skybox ?? "",
        };
    }

    /// <summary>
    /// Apply <paramref name="env"/> to the scene tree rooted at <paramref name="host"/>
    /// (the map node, in Sauerbraten Z-up local space). Reuses an existing
    /// <see cref="WorldEnvironment"/> from the host or one of its ancestors so a
    /// hand-authored scene environment is updated rather than duplicated.
    /// Idempotent; call again whenever the command changes.
    /// </summary>
    public static void Apply(Node3D host, SetEnvironmentCmd? env, ITextureSource? textures)
    {
        env ??= CreateDefault();

        WorldEnvironment worldEnvironment = FindOrCreateWorldEnvironment(host);
        ApplySky(worldEnvironment, env, textures);
        ApplySun(host, env);
    }

    private static WorldEnvironment FindOrCreateWorldEnvironment(Node3D host)
    {
        for (Node? node = host; node != null; node = node.GetParent())
        {
            var existing = node.GetNodeOrNull<WorldEnvironment>(EnvironmentNodeName);
            if (existing != null) return existing;
        }

        var created = new WorldEnvironment { Name = EnvironmentNodeName };
        host.AddChild(created);
        return created;
    }

    private static void ApplySky(WorldEnvironment worldEnvironment, SetEnvironmentCmd env, ITextureSource? textures)
    {
        Godot.Environment godotEnv = worldEnvironment.Environment ??= new Godot.Environment();
        Color skyColor = RadianceVolume.DecodeColor((int)env.SkyColor);
        Color ambientColor = RadianceVolume.DecodeColor((int)env.AmbientColor);

        Sky? cubemap = textures != null ? SkyBuilder.Build(textures, env.Skybox) : null;
        if (cubemap != null)
        {
            godotEnv.BackgroundMode = Godot.Environment.BGMode.Sky;
            godotEnv.Sky = cubemap;
        }
        else if (env.SkyColor == 0 && godotEnv.Sky != null)
        {
            // The command does not override the sky: keep the scene-authored one
            // (e.g. the dev scene's procedural sky) instead of replacing it with black.
            godotEnv.BackgroundMode = Godot.Environment.BGMode.Sky;
        }
        else
        {
            Color effectiveSky = skyColor.R + skyColor.G + skyColor.B > 0f ? skyColor : ambientColor;
            var material = new ProceduralSkyMaterial
            {
                SkyTopColor = effectiveSky,
                SkyHorizonColor = effectiveSky.Lerp(ambientColor, 0.5f),
                GroundHorizonColor = ambientColor,
                GroundBottomColor = ambientColor.Darkened(0.35f),
            };
            godotEnv.BackgroundMode = Godot.Environment.BGMode.Sky;
            godotEnv.Sky = new Sky { SkyMaterial = material };
        }

        // Sky-sourced ambient keeps the engine's ambient as its floor while the sky
        // supplies the directional (up-facing) component.
        godotEnv.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
        godotEnv.AmbientLightColor = ambientColor;
        godotEnv.AmbientLightSkyContribution = 0.75f;
        godotEnv.AmbientLightEnergy = 1.0f;

        if (env.FogDistance > 0f)
        {
            godotEnv.FogEnabled = true;
            godotEnv.FogMode = Godot.Environment.FogModeEnum.Depth;
            godotEnv.FogLightColor = RadianceVolume.DecodeColor((int)env.FogColor);
            godotEnv.FogDepthBegin = env.FogDistance * 0.2f;
            godotEnv.FogDepthEnd = env.FogDistance;
            godotEnv.FogDepthCurve = 1.0f;
        }
        else
        {
            godotEnv.FogEnabled = false;
        }
    }

    /// <summary>
    /// The sun is the engine's <c>sunlightent</c>: a directional light with
    /// <c>sunlightcolor * sunlightscale</c>. It is disabled when the command has no
    /// sun colour or scale, matching <c>lightmap.cpp</c>.
    /// </summary>
    private static void ApplySun(Node3D host, SetEnvironmentCmd env)
    {
        DirectionalLight3D sun = host.GetNodeOrNull<DirectionalLight3D>(SunNodeName)
            ?? AddSun(host);

        bool enabled = (env.SunColor & 0xFFFFFF) != 0 && env.SunScale > 0f;
        sun.Visible = enabled;
        if (!enabled) return;

        sun.LightColor = RadianceVolume.DecodeColor((int)env.SunColor);
        sun.LightEnergy = env.SunScale;
        sun.ShadowEnabled = false;

        // The node lives in the map's Sauerbraten Z-up (mirrored) local space; the
        // light shines along local −Z, so its local +Z must face the sun.
        Vector3 towardSun = RadianceVolume.CalculateSunlightDirSauer(env.SunYaw, env.SunPitch);
        sun.Basis = Basis.LookingAt(-towardSun, UpHint(towardSun));
    }

    private static DirectionalLight3D AddSun(Node3D host)
    {
        var sun = new DirectionalLight3D { Name = SunNodeName, ShadowEnabled = false };
        host.AddChild(sun);
        return sun;
    }

    // Sauerbraten is Z-up: prefer +Z as the up hint unless the direction is
    // nearly vertical (then +Y is safer for Basis.LookingAt).
    private static Vector3 UpHint(Vector3 direction) =>
        Mathf.Abs(direction.Z) > 0.9f ? new Vector3(0, 1, 0) : new Vector3(0, 0, 1);
}
