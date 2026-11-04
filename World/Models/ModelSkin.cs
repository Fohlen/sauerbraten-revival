using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Models;

/// <summary>Builds Godot skin materials (diffuse + optional normal) for a converted model.</summary>
public static class ModelSkin
{
    public static Material? Build(ModelInfo? info, ITextureSource? textures, IDictionary<string, Material> cache, int team = 0)
    {
        if (info == null || textures == null) return null;
        string? skin = info.SkinForTeam(team);
        if (skin == null) return null;
        return BuildMaterial(skin, info.Bump, info, textures, cache);
    }

    /// <summary>Builds a single material for a specific model part skin definition.</summary>
    public static Material? BuildSkin(ModelSkinInfo? skin, ModelInfo? info, ITextureSource? textures, IDictionary<string, Material> cache)
    {
        if (skin == null || string.IsNullOrEmpty(skin.Diffuse) || textures == null) return null;
        string? bump = skin.Bump ?? info?.Bump;
        return BuildMaterial(skin.Diffuse, bump, info, textures, cache);
    }

    /// <summary>
    /// Builds per-surface materials for all parts of a model in surface order.
    /// Returns a list of materials index-aligned to the model's mesh surfaces.
    /// </summary>
    public static List<Material?> BuildSkins(ModelInfo? info, ITextureSource? textures, IDictionary<string, Material> cache, int team = 0)
    {
        var result = new List<Material?>();
        if (info == null || textures == null) return result;

        if (info.Skins.Count > 0)
        {
            // If all skins share the same part name, they are team variants of a single-part model.
            bool allSamePart = true;
            string firstPart = info.Skins[0].Part;
            for (int i = 1; i < info.Skins.Count; i++)
            {
                if (!string.Equals(info.Skins[i].Part, firstPart, System.StringComparison.OrdinalIgnoreCase))
                {
                    allSamePart = false;
                    break;
                }
            }

            if (allSamePart)
            {
                var singleMat = Build(info, textures, cache, team);
                if (singleMat != null) result.Add(singleMat);
                return result;
            }

            foreach (var s in info.Skins)
            {
                result.Add(BuildSkin(s, info, textures, cache));
            }
            return result;
        }

        var defaultMat = Build(info, textures, cache, team);
        if (defaultMat != null) result.Add(defaultMat);
        return result;
    }

    private static Material? BuildMaterial(string skin, string? bump, ModelInfo? info, ITextureSource textures, IDictionary<string, Material> cache)
    {
        string key = $"{skin}|{bump}|{info?.AlphaTest}|{info?.AlphaBlend}|{info?.CullFace}";
        if (cache.TryGetValue(key, out var cached)) return cached;

        var material = new StandardMaterial3D
        {
            AlbedoTexture = textures.Get(skin),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,
            CullMode = info?.CullFace == true
                ? BaseMaterial3D.CullModeEnum.Back
                : BaseMaterial3D.CullModeEnum.Disabled,
        };

        if (info?.AlphaBlend == false)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = info?.AlphaTest ?? 0.5f;
        }
        else if (info?.AlphaTest.HasValue == true && info.AlphaTest.Value > 0f)
        {
            if (info.AlphaBlend == true || info.AlphaTest.Value < 0.2f)
            {
                material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
            }
            else
            {
                material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                material.AlphaScissorThreshold = info.AlphaTest.Value;
            }
        }
        else if (info?.AlphaBlend == true)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
        }

        if (bump != null)
        {
            var bumpTex = textures.Get(bump);
            if (bumpTex != null)
            {
                material.NormalEnabled = true;
                material.NormalTexture = bumpTex;
            }
        }

        cache[key] = material;
        return material;
    }
}
