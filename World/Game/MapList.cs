using Godot;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// A curated list of classic maps, mirroring the engine's <c>MapList_*</c>
/// aliases from <c>data/lists.cfg</c> (Sauerbraten SVN r6889). The engine's maps
/// GUI shows a different list per mode family (see
/// <see cref="MapListCatalog.ForMode"/>), so the front-end map picker does the
/// same.
///
/// Authored as a Godot <see cref="Resource"/> (one <c>.tres</c> per list under
/// <see cref="MapListCatalog.ResourceDir"/>), like <see cref="GameModeInfo"/>, so
/// a mod can add or replace a list without code.
/// </summary>
[Tool]
[GlobalClass]
public partial class MapList : Resource
{
    /// <summary>Stable, filename-safe key (e.g. <c>ctf</c>); also the <c>.tres</c> file stem.</summary>
    [Export] public string Id { get; set; } = "";

    /// <summary>Human-readable name (matches the engine's GUI tab, e.g. "ffa", "capture", "ctf").</summary>
    [Export] public string Name { get; set; } = "";

    /// <summary>Map names without package or extension (e.g. <c>metl3</c>), in engine list order.</summary>
    [Export] public string[] Maps { get; set; } = System.Array.Empty<string>();

    public override string ToString() => string.IsNullOrEmpty(Id) ? base.ToString() : $"{Id} ({Maps.Length} maps)";
}
