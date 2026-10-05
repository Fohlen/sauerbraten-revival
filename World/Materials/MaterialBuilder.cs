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

    public static MaterialSet Build(MetadataCatalogue catalogue, ITextureSource textures)
    {
        var set = new MaterialSet();
        foreach (var vs in catalogue.VSlots.Values)
        {
            catalogue.Slots.TryGetValue(vs.Slot, out var slot);
            Texture2D? albedo = slot?.DiffusePath != null ? textures.Get(slot.DiffusePath) : null;
            Texture2D? normal = slot?.NormalPath != null ? textures.Get(slot.NormalPath) : null;
            Texture2D? glow = slot?.GlowPath != null ? textures.Get(slot.GlowPath) : null;
            Vector2I texSize = slot?.DiffusePath != null ? textures.Size(slot.DiffusePath) : new Vector2I(64, 64);

            var material = new StandardMaterial3D
            {
                AlbedoTexture = albedo,
                AlbedoColor = new Color(vs.Color.X, vs.Color.Y, vs.Color.Z),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, // winding not verified yet
            };

            if (normal != null)
            {
                material.NormalEnabled = true;
                material.NormalTexture = normal;
            }
            if (glow != null)
            {
                material.EmissionEnabled = true;
                material.EmissionTexture = glow;
                material.Emission = new Color(vs.Glow.X, vs.Glow.Y, vs.Glow.Z);
                material.EmissionEnergyMultiplier = 1.0f;
            }

            var uv = new TexGen[3];
            for (int dim = 0; dim < 3; dim++) uv[dim] = CalcTexGen(vs, dim, texSize);
            set.Add(vs.Index, material, uv);
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
}
