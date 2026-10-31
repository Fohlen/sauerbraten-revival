using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Models;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.World.Game.Items;

/// <summary>
/// Builds <see cref="Item"/> nodes from the item entities
/// (<c>ET_SHELLS</c>…<c>ET_QUAD</c>). The model names come from the engine's
/// <c>entmdlname</c> table; their built-in models are converted by
/// <c>tools/model-import</c>.
/// </summary>
public static class ItemBuilder
{
    public static ItemManager Build(OgzMap map, IModelCatalog? models = null, ITextureSource? textures = null) =>
        Build(map.Entities, models, textures);

    public static ItemManager Build(IReadOnlyList<MapEntity> entities, IModelCatalog? models = null, ITextureSource? textures = null)
    {
        var root = new ItemManager { Name = "Items" };
        var skinCache = new Dictionary<string, Material>();

        foreach (var (i, e) in entities.WhereIndexed(e => ModelName(e.Type) != null))
        {
            string modelName = ModelName(e.Type)!;

            var info = models?.Get(modelName);
            var item = new Item
            {
                Name = $"Item_{i}",
                Position = e.Position,
                BasePosition = e.Position,
                ItemType = e.Type,
                ItemIndex = i,
                ItemName = EntityType.Name(e.Type),
                PickupRadius = ItemCatalog.TryGetByEntityType(e.Type)?.PickupRadius ?? 12.0f,
                GlbPath = info?.GlbPath,
                GlbOsPath = info?.GlbOsPath,
                ModelScale = info?.Scale ?? 1.0f,
                ModelTranslation = info?.Translation ?? Vector3.Zero,
                SkinMaterial = ModelSkin.Build(info, textures, skinCache),
            };
            EntityFacing.ApplyYaw(item, 0);
            root.AddItem(item);
            EntityMarker.Add(item, textures);
        }
        return root;
    }

    /// <summary>fpsgame/entities.cpp <c>entmdlname</c> for the item types.</summary>
    public static string? ModelName(byte type)
    {
        string? model = ItemCatalog.TryGetByEntityType(type)?.Model;
        return string.IsNullOrEmpty(model) ? null : model;
    }
}
