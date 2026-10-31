using System;
using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Models;

/// <summary>
/// One playable model's variants (render.cpp <c>playermodelinfo</c>). The engine
/// keeps selectable player models; each names its FFA body, the blue/red team skins,
/// its first-person <c>hudguns</c>, an optional body view-weapon (<c>vwep</c>),
/// quad/powerup attachment and the three armour skins.
///
/// Follows ADR 0010 as a typed <see cref="Resource"/> with inspector visibility
/// and modding overrides.
/// </summary>
[Tool]
[GlobalClass]
public partial class PlayerModelInfo : Resource, IResourceDefinition<PlayerModelId>
{
    [Export] public PlayerModelId Id { get; set; } = PlayerModelId.None;
    [Export] public string Name { get; set; } = "";
    [Export] public string Ffa { get; set; } = "";
    [Export] public string BlueTeam { get; set; } = "";
    [Export] public string RedTeam { get; set; } = "";
    [Export] public string Hudguns { get; set; } = "";
    [Export] public string? Vwep { get; set; }
    [Export] public string? Quad { get; set; }
    [Export] public string[] Armour { get; set; } = Array.Empty<string>();
    [Export] public string FfaIcon { get; set; } = "";
    [Export] public string BlueIcon { get; set; } = "";
    [Export] public string RedIcon { get; set; } = "";
    [Export] public bool Ragdoll { get; set; } = true;

    /// <summary>The body model for a team (0 = neutral, 1 = blue, 2 = red).</summary>
    public string Body(int team) => team switch
    {
        1 => BlueTeam,
        2 => RedTeam,
        _ => Ffa,
    };

    /// <summary>The icon for a team (0 = neutral, 1 = blue, 2 = red).</summary>
    public string Icon(int team) => team switch
    {
        1 => BlueIcon,
        2 => RedIcon,
        _ => FfaIcon,
    };

    /// <summary>Armour model/skin variant (0 = blue, 1 = green, 2 = yellow).</summary>
    public string? ArmourSkin(int armourType) =>
        armourType >= 0 && armourType < Armour.Length ? Armour[armourType] : null;
}
