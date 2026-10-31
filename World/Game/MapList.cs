using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// A curated list of classic maps, mirroring the engine's <c>MapList_*</c>
/// aliases from <c>data/lists.cfg</c> (Sauerbraten SVN r6889). The engine's maps
/// GUI shows a different list per mode family (see
/// <see cref="MapListCatalog.ForMode"/>), so the front-end map picker does the
/// same.
///
/// Follows ADR 0010:
/// - Core catalog: <c>res://resources/maplists/&lt;name&gt;.tres</c>
/// - Mod overrides: <c>res://resources/maplists/overrides/</c>
/// - User overrides: <c>user://maplists/overrides/</c>
/// - Hot-path lookup: O(1) direct integer indexing via <see cref="MapListCatalog.All"/> or <see cref="MapListCatalog.Get(MapListId)"/>.
/// </summary>
[Tool]
[GlobalClass]
public partial class MapList : Resource, IResourceDefinition<MapListId>
{
    /// <summary>Map list enum ID (<see cref="MapListId"/>).</summary>
    [Export] public MapListId Id { get; set; } = MapListId.None;

    /// <summary>Human-readable name (matches the engine's GUI tab, e.g. "ffa", "capture", "ctf").</summary>
    [Export] public string Name { get; set; } = "";

    /// <summary>Map names without package or extension (e.g. <c>metl3</c>), in engine list order.</summary>
    [Export] public string[] Maps { get; set; } = System.Array.Empty<string>();

    public MapList() { }

    public MapList(MapListId id, string name, string[] maps)
    {
        Id = id;
        Name = name;
        Maps = maps;
    }

    public override string ToString() => string.IsNullOrEmpty(Name)
        ? (Id != MapListId.None ? $"{Id} ({Maps.Length} maps)" : base.ToString())
        : $"{Name} ({Maps.Length} maps)";
}
