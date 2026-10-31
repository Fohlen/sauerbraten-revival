using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Game.Items;

/// <summary>
/// A collection of pickup item definitions, loadable from a single <c>.tres</c>
/// (e.g. bundled item pack or mod override catalog).
/// </summary>
[Tool]
[GlobalClass]
public partial class ItemCatalogResource : Resource, IResourceCatalog<ItemInfo>
{
    [Export] public Godot.Collections.Array<ItemInfo> Items { get; set; } = new();

    public IEnumerable<ItemInfo> Definitions => Items;

    public static ItemCatalogResource CreateFromDefaults(ItemInfo[] defaults)
    {
        var catalog = new ItemCatalogResource();
        foreach (var def in defaults)
            catalog.Items.Add(def);
        return catalog;
    }
}
