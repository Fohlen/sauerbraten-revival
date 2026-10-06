using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Entities;

/// <summary>Builds a Godot skin material (diffuse + optional normal) for a converted model.</summary>
public static class ModelSkin
{
    public static Material? Build(ModelInfo? info, ITextureSource? textures, Dictionary<string, Material> cache)
    {
        if (info?.Skin == null || textures == null) return null;
        if (cache.TryGetValue(info.Skin, out var cached)) return cached;

        var material = new StandardMaterial3D
        {
            AlbedoTexture = textures.Get(info.Skin),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        if (info.Bump != null)
        {
            var bump = textures.Get(info.Bump);
            if (bump != null) { material.NormalEnabled = true; material.NormalTexture = bump; }
        }

        cache[info.Skin] = material;
        return material;
    }
}
