using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// A collection of weapon definitions, loaded from a single <c>.tres</c> (e.g. core weapons catalog or a mod pack).
/// </summary>
[Tool]
[GlobalClass]
public partial class WeaponCatalogResource : Resource, IResourceCatalog<WeaponInfo>
{
    [Export] public Godot.Collections.Array<WeaponInfo> Weapons { get; set; } = new();

    public IEnumerable<WeaponInfo> Definitions => Weapons;

    public static WeaponCatalogResource CreateFromDefaults(WeaponInfo[] defaults)
    {
        var catalog = new WeaponCatalogResource();
        foreach (var def in defaults)
            catalog.Weapons.Add(def);
        return catalog;
    }
}
