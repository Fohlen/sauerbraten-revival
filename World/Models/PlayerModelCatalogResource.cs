using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Models;

/// <summary>
/// A collection of player model definitions, loaded from a single <c>.tres</c> (e.g. core models catalog or a mod pack).
/// </summary>
[Tool]
[GlobalClass]
public partial class PlayerModelCatalogResource : Resource, IResourceCatalog<PlayerModelInfo>
{
    [Export] public Godot.Collections.Array<PlayerModelInfo> Models { get; set; } = new();

    public IEnumerable<PlayerModelInfo> Definitions => Models;

    public static PlayerModelCatalogResource CreateFromDefaults(PlayerModelInfo[] defaults)
    {
        var catalog = new PlayerModelCatalogResource();
        foreach (var def in defaults)
            catalog.Models.Add(def);
        return catalog;
    }
}
