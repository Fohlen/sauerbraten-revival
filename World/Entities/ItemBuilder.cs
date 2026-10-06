using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Builds <see cref="Item"/> nodes from the item entities
/// (<c>ET_SHELLS</c>…<c>ET_QUAD</c>). The model names come from the engine's
/// <c>entmdlname</c> table; their built-in models are converted by
/// <c>tools/model-import</c>.
/// </summary>
public static class ItemBuilder
{
    public static Node3D Build(OgzMap map, ModelCatalog? models = null, ITextureSource? textures = null)
    {
        var root = new Node3D { Name = "Items" };
        var skinCache = new Dictionary<string, Material>();

        for (int i = 0; i < map.Entities.Count; i++)
        {
            var e = map.Entities[i];
            string? modelName = ModelName(e.Type);
            if (modelName == null) continue;

            var info = models?.Get(modelName);
            var item = new Item
            {
                Name = $"Item_{i}",
                Position = e.Position,
                ItemType = e.Type,
                ItemName = EntityType.Name(e.Type),
                GlbPath = info?.GlbPath,
                GlbOsPath = info?.GlbOsPath,
                ModelScale = info?.Scale ?? 1.0f,
                ModelTranslation = info?.Translation ?? Vector3.Zero,
                SkinMaterial = ModelSkin.Build(info, textures, skinCache),
            };
            root.AddChild(item);
            EntityMarker.Add(item, textures);
        }
        return root;
    }

    /// <summary>fpsgame/entities.cpp <c>entmdlname</c> for the item types.</summary>
    public static string? ModelName(byte type) => type switch
    {
        EntityType.Shells => "ammo/shells",
        EntityType.Bullets => "ammo/bullets",
        EntityType.Rockets => "ammo/rockets",
        EntityType.RifleRounds => "ammo/rrounds",
        EntityType.Grenades => "ammo/grenades",
        EntityType.Cartridges => "ammo/cartridges",
        EntityType.Health => "health",
        EntityType.HealthBoost => "boost",
        EntityType.GreenArmour => "armor/green",
        EntityType.YellowArmour => "armor/yellow",
        EntityType.QuadDamage => "quad",
        _ => null,
    };
}
