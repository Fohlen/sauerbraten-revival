using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Models;

/// <summary>Builds a Godot skin material (diffuse + optional normal) for a converted model.</summary>
public static class ModelSkin
{
    public static Material? Build(ModelInfo? info, ITextureSource? textures, Dictionary<string, Material> cache)
    {
        if (info?.Skin == null || textures == null) return null;
        string key = $"{info.Skin}|{info.Bump}|{info.AlphaTest}|{info.AlphaBlend}|{info.CullFace}";
        if (cache.TryGetValue(key, out var cached)) return cached;

        var material = new StandardMaterial3D
        {
            AlbedoTexture = textures.Get(info.Skin),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,
            CullMode = info.CullFace == true
                ? BaseMaterial3D.CullModeEnum.Back
                : BaseMaterial3D.CullModeEnum.Disabled,
        };

        if (info.AlphaBlend == false)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = info.AlphaTest ?? 0.5f;
        }
        else if (info.AlphaTest.HasValue && info.AlphaTest.Value > 0f)
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
        else if (info.AlphaBlend == true)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
        }

        if (info.Bump != null)
        {
            var bump = textures.Get(info.Bump);
            if (bump != null) { material.NormalEnabled = true; material.NormalTexture = bump; }
        }

        cache[key] = material;
        return material;
    }
}
