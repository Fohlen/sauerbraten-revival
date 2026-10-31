using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Hud;

/// <summary>
/// A collection of crosshair definitions, loadable from a single <c>.tres</c>
/// (e.g. bundled crosshair pack or mod override catalog).
/// </summary>
[Tool]
[GlobalClass]
public partial class CrosshairCatalogResource : Resource, IResourceCatalog<CrosshairInfo>
{
    [Export] public Godot.Collections.Array<CrosshairInfo> Crosshairs { get; set; } = new();

    public IEnumerable<CrosshairInfo> Definitions => Crosshairs;

    public static CrosshairCatalogResource CreateFromDefaults(CrosshairInfo[] defaults)
    {
        var catalog = new CrosshairCatalogResource();
        foreach (var def in defaults)
            catalog.Crosshairs.Add(def);
        return catalog;
    }
}
