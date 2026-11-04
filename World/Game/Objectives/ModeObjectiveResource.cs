using Godot;

namespace SauerbratenRevival.World.Game.Objectives;

/// <summary>
/// Authoring resource for a match objective (<c>res://resources/objectives/&lt;id&gt;.tres</c>).
/// Managed by <see cref="ObjectiveCatalog"/> with 3-tier cascade override support.
/// </summary>
[Tool]
[GlobalClass]
public abstract partial class ModeObjectiveResource : Resource
{
    [Export] public string Id { get; set; } = "";
    [Export] public string Name { get; set; } = "";

    /// <summary>Whether this objective uses teams by default (can be overridden by GameModeInfo.TeamPlay).</summary>
    [Export] public bool DefaultTeamPlay { get; set; } = false;

    /// <summary>Whether this objective boots into the interactive octree editor.</summary>
    [Export] public bool IsEditMode { get; set; } = false;

    /// <summary>Default map list pool used when no MapList explicitly lists the mode's Id.</summary>
    [Export] public MapListId DefaultMapPool { get; set; } = MapListId.Dm;

    /// <summary>Default starting armour for this objective when no mutator overrides it (25 in DM, 50 in CTF/Hold/Protect).</summary>
    [Export] public int DefaultSpawnArmour { get; set; } = 25;

    /// <summary>Instantiates the runtime state machine for a match.</summary>
    public abstract IModeObjective CreateInstance(GameModeInfo mode);

    public override string ToString() => string.IsNullOrEmpty(Id) ? base.ToString() : Id;
}
