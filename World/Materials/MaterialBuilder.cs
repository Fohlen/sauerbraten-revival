using Godot;

namespace SauerbratenRevival.World.Materials;

/// <summary>
/// Builds a <see cref="MaterialSet"/> from a <see cref="MetadataCatalogue"/>.
/// Stage C1 is diffuse-only; normal/glow/spec/alpha and blending come later.
/// </summary>
public static class MaterialBuilder
{
    /// <summary>octarender.cpp: <c>TEX_SCALE</c>.</summary>
    public const float TexScale = 8.0f;

    // texture.cpp: texrotations[8] { flipx, flipy, swapxy }.
    private static readonly (bool FlipX, bool FlipY, bool SwapXy)[] TexRotations =
    {
        (false, false, false), // 0: default
        (false, true, true),   // 1: 90
        (true, true, false),   // 2: 180
        (true, false, true),   // 3: 270
        (true, false, false),  // 4: flip X
        (false, true, false),  // 5: flip Y
        (false, false, true),  // 6: transpose
        (true, true, true),    // 7: flipped transpose
    };

    public static MaterialSet Build(
        MetadataCatalogue catalogue,
        ITextureSource textures,
        Lighting.RadianceVolume? radiance = null)
    {
        var set = new MaterialSet();
        Color skyTint = radiance != null ? Lighting.RadianceVolume.DecodeColor(catalogue.Config.SkyboxColour) : Colors.White;
        Color ambientColor = Lighting.RadianceVolume.DecodeColor(catalogue.Config.Ambient);

        foreach (var vs in catalogue.VSlots.Values)
        {
            catalogue.Slots.TryGetValue(vs.Slot, out var slot);
            Texture2D? albedo = slot?.DiffusePath != null ? textures.Get(slot.DiffusePath) : null;
            Texture2D? normal = slot?.NormalPath != null ? textures.Get(slot.NormalPath) : null;
            Texture2D? glow = slot?.GlowPath != null ? textures.Get(slot.GlowPath) : null;
            Vector2I texSize = slot?.DiffusePath != null ? textures.Size(slot.DiffusePath) : new Vector2I(64, 64);
            var tint = new Color(vs.Color.X, vs.Color.Y, vs.Color.Z);
            var glowColor = new Color(vs.Glow.X, vs.Glow.Y, vs.Glow.Z);

            Material material;
            if (radiance != null)
            {
                bool useRadiance = radiance.Settings.Mode == Lighting.RadianceQualityMode.RadianceHints;
                bool useEnvmap = slot != null && slot.IsEnvMap;
                Vector3? envScale = useEnvmap ? slot!.ResolveEnvScale(vs) : null;
                material = BuildRadianceMaterial(
                    albedo,
                    tint,
                    normal,
                    glow,
                    glowColor,
                    radiance.Texture,
                    radiance.Grid.Origin,
                    radiance.Grid.Extent,
                    skyTint,
                    ambientColor,
                    useRadiance,
                    radianceAmbTex: radiance.AmbientTexture,
                    useEnvmap: useEnvmap,
                    envScale: envScale);
            }
            else
            {
                var standard = new StandardMaterial3D
                {
                    AlbedoTexture = albedo,
                    AlbedoColor = tint,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,
                    CullMode = BaseMaterial3D.CullModeEnum.Back,
                };
                if (normal != null)
                {
                    standard.NormalEnabled = true;
                    standard.NormalTexture = normal;
                }
                if (glow != null)
                {
                    standard.EmissionEnabled = true;
                    standard.EmissionTexture = glow;
                    standard.Emission = glowColor;
                    standard.EmissionEnergyMultiplier = 1.0f;
                }
                if (slot != null && slot.Shader.Contains("alpha"))
                {
                    standard.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    standard.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
                }
                material = standard;
            }

            var uv = new TexGen[3];
            for (int dim = 0; dim < 3; dim++) uv[dim] = CalcTexGen(vs, dim, texSize);
            set.Add(vs.Index, material, uv);
            set.SetLayer(vs.Index, vs.Layer);

            // Transparent variant, used for MAT_ALPHA cube faces (see AlphaMaterial).
            set.SetAlphaMaterial(vs.Index, AlphaMaterial.Create(
                albedo, normal, glow, tint, glowColor, vs.AlphaFront, vs.AlphaBack));
        }
        return set;
    }

    /// <summary>octarender.cpp:741 <c>calctexgen(vslot, dim)</c>.</summary>
    public static TexGen CalcTexGen(CatalogueVSlot vs, int dim, Vector2I textureSize)
    {
        var r = TexRotations[vs.Rotation & 7];
        float k = TexScale / (vs.Scale <= 0 ? 1.0f : vs.Scale);
        float xs = r.FlipX ? -textureSize.X : textureSize.X;
        float ys = r.FlipY ? -textureSize.Y : textureSize.Y;
        float sk = k / xs, tk = k / ys;
        float soff = -(r.SwapXy ? vs.OffsetY : vs.OffsetX) / xs;
        float toff = -(r.SwapXy ? vs.OffsetX : vs.OffsetY) / ys;

        int[] si = { 1, 0, 0 }, ti = { 2, 2, 1 };
        int sdim = si[dim], tdim = ti[dim];

        var s = new Vector4(0, 0, 0, soff);
        var t = new Vector4(0, 0, 0, toff);
        if (r.SwapXy)
        {
            s[tdim] = dim <= 1 ? -sk : sk;
            t[sdim] = tk;
        }
        else
        {
            s[sdim] = sk;
            t[tdim] = dim <= 1 ? -tk : tk;
        }
        return new TexGen(s, t);
    }

    /// <summary>Resource path of the world radiance hints shader (docs/adr/0002-radiance-hints.md §5).</summary>
    public const string RadianceShaderPath = "res://World/Materials/radiance.gdshader";

    /// <summary>
    /// Build a radiance hints shader material for a vslot.
    /// </summary>
    public static ShaderMaterial BuildRadianceMaterial(
        Texture2D? albedo,
        Color tint,
        Texture2D? normal = null,
        Texture2D? glow = null,
        Color? glowColor = null,
        Texture3D? radianceTex = null,
        Vector3? gridOrigin = null,
        float gridExtent = 1f,
        Color? skyTint = null,
        Color? ambientColor = null,
        bool useRadiance = true,
        Texture3D? radianceAmbTex = null,
        bool useEnvmap = false,
        Vector3? envScale = null,
        float envRoughness = 0.05f)
    {
        var shader = ResourceLoader.Load<Shader>(RadianceShaderPath)
            ?? throw new System.InvalidOperationException($"missing shader {RadianceShaderPath}");
        var material = new ShaderMaterial { Shader = shader };
        if (albedo != null) material.SetShaderParameter("albedo_tex", albedo);
        material.SetShaderParameter("albedo_tint", new Vector3(tint.R, tint.G, tint.B));

        if (normal != null)
        {
            material.SetShaderParameter("normal_tex", normal);
            material.SetShaderParameter("use_normal", true);
        }

        if (glow != null)
        {
            material.SetShaderParameter("glow_tex", glow);
            Color gc = glowColor ?? Colors.White;
            material.SetShaderParameter("glow_tint", new Vector3(gc.R, gc.G, gc.B));
            material.SetShaderParameter("use_glow", true);
        }

        Color amb = ambientColor ?? Colors.White;
        material.SetShaderParameter("ambient_color", new Vector3(amb.R, amb.G, amb.B));

        if (radianceTex != null && gridOrigin.HasValue)
        {
            material.SetShaderParameter("radiance_tex", radianceTex);
            material.SetShaderParameter("grid_origin", gridOrigin.Value);
            material.SetShaderParameter("grid_extent", gridExtent);
            Color st = skyTint ?? Colors.White;
            material.SetShaderParameter("sky_tint", new Vector3(st.R, st.G, st.B));
            material.SetShaderParameter("use_radiance", useRadiance);

            if (radianceAmbTex != null)
            {
                material.SetShaderParameter("radiance_amb_tex", radianceAmbTex);
                material.SetShaderParameter("use_radiance_amb", useRadiance);
            }
            else
            {
                material.SetShaderParameter("use_radiance_amb", false);
            }
        }
        else
        {
            material.SetShaderParameter("use_radiance", false);
            material.SetShaderParameter("use_radiance_amb", false);
        }

        if (useEnvmap)
        {
            Vector3 es = envScale ?? CatalogueSlot.DefaultEnvScale;
            Vector3 clampedTint = new(
                Mathf.Clamp(es.X, 0f, 1f),
                Mathf.Clamp(es.Y, 0f, 1f),
                Mathf.Clamp(es.Z, 0f, 1f));
            float intensity = Mathf.Clamp(Mathf.Max(es.X, Mathf.Max(es.Y, es.Z)), 0f, 1f);

            material.SetShaderParameter("use_envmap", true);
            material.SetShaderParameter("env_tint", clampedTint);
            material.SetShaderParameter("env_intensity", intensity);
            material.SetShaderParameter("env_roughness", Mathf.Clamp(envRoughness, 0f, 1f));
        }
        else
        {
            material.SetShaderParameter("use_envmap", false);
        }

        return material;
    }
}
