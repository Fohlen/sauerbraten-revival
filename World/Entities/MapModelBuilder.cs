using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Builds <see cref="MapModel"/> nodes from a map's <c>ET_MAPMODEL</c> entities.
/// attr1 is the facing yaw, attr2 the model index (resolved via the catalogue).
/// If a <see cref="ModelCatalog"/> and texture source are supplied, the converted
/// glTF and its skin material are attached.
/// </summary>
public static class MapModelBuilder
{
    public static Node3D Build(OgzMap map, MetadataCatalogue? catalogue,
                               ModelCatalog? models = null, ITextureSource? textures = null)
    {
        var root = new Node3D { Name = "MapModels" };
        var skinCache = new Dictionary<string, Material>();

        for (int i = 0; i < map.Entities.Count; i++)
        {
            var e = map.Entities[i];
            if (e.Type != EntityType.MapModel) continue;

            string name = catalogue?.MapModelPath(e.Attr2) ?? $"mapmodel#{e.Attr2}";
            var info = models?.Get(name);

            var model = new MapModel
            {
                Name = $"MapModel_{i}",
                Position = e.Position,
                Yaw = e.Attr1,
                ModelPath = name,
                GlbPath = info?.GlbPath,
                GlbOsPath = info?.GlbOsPath,
                ModelScale = info?.Scale ?? 1.0f,
                ModelTranslation = info?.Translation ?? Vector3.Zero,
                SkinMaterial = BuildSkin(info, textures, skinCache),
            };

            Vector3 forward = PlayerStartBuilder.Forward(e.Attr1);
            if (forward.LengthSquared() > 1e-6f)
                model.Basis = Basis.LookingAt(forward, new Vector3(0, 0, 1));

            root.AddChild(model);
        }
        return root;
    }

    private static Material? BuildSkin(ModelInfo? info, ITextureSource? textures, Dictionary<string, Material> cache)
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
