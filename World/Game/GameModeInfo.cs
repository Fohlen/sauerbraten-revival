using Godot;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// A game mode definition: a stable <see cref="Id"/> and its <see cref="ModeFlags"/>.
/// Authored as a Godot <see cref="Resource"/> (one <c>.tres</c> per mode under
/// <see cref="GameModeCatalog.ResourceDir"/>), so mods can add or override modes
/// without code and without an engine index.
///
/// The engine's numeric <c>gamemode</c> index is deliberately not represented — it
/// is an ordering detail of <c>gamemodes[]</c>, not an identity. The long
/// localized description strings from the original table are not ported either.
///
/// <c>[Tool]</c> is required for the editor to instantiate the managed type when a
/// <c>.tres</c> is loaded from a tool/editor context (e.g. the viewer's <c>_Ready</c>);
/// without it <see cref="Godot.ResourceLoader"/> hands back a plain
/// <c>Godot.Resource</c> and the typed load throws.
/// </summary>
[Tool]
[GlobalClass]
public partial class GameModeInfo : Resource
{
    /// <summary>Stable, filename-safe key (e.g. <c>insta_ctf</c>); also the <c>.tres</c> file stem.</summary>
    [Export] public string Id { get; set; } = "";

    /// <summary>Human-readable / engine name (e.g. "insta ctf").</summary>
    [Export] public string Name { get; set; } = "";

    /// <summary>Engine <c>gamemodes[].flags</c> (<c>M_*</c>). Behaviour is keyed on these.</summary>
    [Export] public ModeFlags Flags { get; set; }

    /// <summary>Engine <c>m_check(gamemode, flag)</c> for this mode.</summary>
    public bool Has(ModeFlags flag) => (Flags & flag) != 0;

    public override string ToString() => string.IsNullOrEmpty(Id) ? base.ToString() : Id;
}
