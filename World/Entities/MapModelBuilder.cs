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
                               IModelCatalog? models = null, ITextureSource? textures = null)
    {
        var root = new Node3D { Name = "MapModels" };
        var skinCache = new Dictionary<string, Material>();

        foreach (var (i, e) in map.OfType(EntityType.MapModel))
        {
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
                SkinMaterial = ModelSkin.Build(info, textures, skinCache),
            };

            EntityFacing.ApplyYaw(model, e.Attr1);

            root.AddChild(model);
        }
        return root;
    }
}
