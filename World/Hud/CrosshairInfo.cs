using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Hud;

/// <summary>
/// One crosshair definition (ports <c>crosshairs[]</c> and <c>loadcrosshair</c> in <c>engine/rendergl.cpp:2083</c>
/// and <c>defaultcrosshair</c> in <c>fpsgame/fps.cpp:1110</c>).
///
/// Follows ADR 0010 as a typed <see cref="Resource"/> with Godot inspector visibility,
/// 3-tier cascade modding, and pre-baked flat table runtime indexing.
/// </summary>
[Tool]
[GlobalClass]
public partial class CrosshairInfo : Resource, IResourceDefinition<CrosshairType>
{
    /// <summary>Crosshair slot enum ID (<see cref="CrosshairType"/>).</summary>
    [Export] public CrosshairType Id { get; set; } = CrosshairType.None;

    /// <summary>Descriptive canonical name (e.g. "default", "teammate", "hit").</summary>
    [Export] public string Name { get; set; } = "";

    /// <summary>Texture path relative to texture root (e.g. "crosshairs/default.png").</summary>
    [Export] public string Path { get; set; } = "";

    /// <summary>Optional direct texture reference (overrides <see cref="Path"/> when set).</summary>
    [Export] public Texture2D? Texture { get; set; }

    public CrosshairInfo() { }

    public CrosshairInfo(CrosshairType id, string name, string path, Texture2D? texture = null)
    {
        Id = id;
        Name = name;
        Path = path;
        Texture = texture;
    }
}
