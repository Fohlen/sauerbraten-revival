using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// A collection of map list definitions, loadable from a single <c>.tres</c>
/// (e.g. bundled maplist pack or mod override catalog).
/// </summary>
[Tool]
[GlobalClass]
public partial class MapListCatalogResource : Resource, IResourceCatalog<MapList>
{
    [Export] public Godot.Collections.Array<MapList> MapLists { get; set; } = new();

    public IEnumerable<MapList> Definitions => MapLists;

    public static MapListCatalogResource CreateFromDefaults(MapList[] defaults)
    {
        var catalog = new MapListCatalogResource();
        foreach (var def in defaults)
            catalog.MapLists.Add(def);
        return catalog;
    }
}
