using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Save;

/// <summary>
/// Builds a <see cref="ManifestData"/> from an engine-exported
/// <see cref="MetadataCatalogue"/> (docs/ogz-import.md). Every entry carries the
/// **full resolved engine path** of its textures/models/sounds, so the manifest
/// does not depend on the original engine's global registries.
///
/// This is the offline counterpart of <see cref="MaterialBuilder"/>: the runtime
/// dev viewer still builds live materials directly from a catalogue, while the
/// converter bakes the resolved paths into the map directory for later loading.
/// </summary>
public static class ManifestBuilder
{
    public static ManifestData Build(MetadataCatalogue catalogue)
    {
        var data = new ManifestData();

        foreach (var vs in catalogue.VSlots.Values)
        {
            catalogue.Slots.TryGetValue(vs.Slot, out var slot);

            data.Materials.Add(new MaterialEntry
            {
                Index = vs.Index,
                Slot = vs.Slot,
                Scale = vs.Scale,
                Rotation = vs.Rotation,
                OffsetX = vs.OffsetX,
                OffsetY = vs.OffsetY,
                ColorR = vs.Color.X,
                ColorG = vs.Color.Y,
                ColorB = vs.Color.Z,
                GlowR = vs.Glow.X,
                GlowG = vs.Glow.Y,
                GlowB = vs.Glow.Z,
                AlphaFront = vs.AlphaFront,
                AlphaBack = vs.AlphaBack,
                Shader = slot?.Shader ?? "",
                DiffusePath = slot?.DiffusePath ?? "",
                NormalPath = slot?.NormalPath ?? "",
                GlowPath = slot?.GlowPath ?? "",
            });
        }

        // Keep a stable order so the manifest — and the archive hash — is canonical.
        data.Materials.Sort((a, b) => a.Index.CompareTo(b.Index));

        foreach (var kv in catalogue.MapModels) data.Models[kv.Key] = kv.Value;
        foreach (var kv in catalogue.Sounds) data.Sounds[kv.Key] = kv.Value;

        return data;
    }

    /// <summary>
    /// Rebuild a <see cref="MaterialSet"/> from a manifest (the load side). Textures
    /// are resolved through <paramref name="textures"/> using the stored full paths.
    /// </summary>
    public static MaterialSet ToMaterialSet(ManifestData data, ITextureSource textures)
    {
        var set = new MaterialSet();
        foreach (var m in data.Materials)
        {
            var albedo = m.DiffusePath.Length > 0 ? textures.Get(m.DiffusePath) : null;
            var normal = m.NormalPath.Length > 0 ? textures.Get(m.NormalPath) : null;
            var glow = m.GlowPath.Length > 0 ? textures.Get(m.GlowPath) : null;
            var texSize = m.DiffusePath.Length > 0 ? textures.Size(m.DiffusePath) : new Godot.Vector2I(64, 64);

            var material = new Godot.StandardMaterial3D
            {
                AlbedoTexture = albedo,
                AlbedoColor = new Godot.Color(m.ColorR, m.ColorG, m.ColorB),
                TextureFilter = Godot.BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps,
                CullMode = Godot.BaseMaterial3D.CullModeEnum.Back,
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
                material.Emission = new Godot.Color(m.GlowR, m.GlowG, m.GlowB);
                material.EmissionEnergyMultiplier = 1.0f;
            }

            var uv = new TexGen[3];
            var vs = new CatalogueVSlot
            {
                Index = m.Index,
                Slot = m.Slot,
                Scale = m.Scale,
                Rotation = m.Rotation,
                OffsetX = m.OffsetX,
                OffsetY = m.OffsetY,
                Color = new Godot.Vector3(m.ColorR, m.ColorG, m.ColorB),
                Glow = new Godot.Vector3(m.GlowR, m.GlowG, m.GlowB),
                AlphaFront = m.AlphaFront,
                AlphaBack = m.AlphaBack,
            };
            for (int dim = 0; dim < 3; dim++) uv[dim] = MaterialBuilder.CalcTexGen(vs, dim, texSize);
            set.Add(m.Index, material, uv);
        }
        return set;
    }
}
