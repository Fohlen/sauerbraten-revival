using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// A spawn point for a single-player monster (<c>MONSTER</c>, type 21). The
/// engine creates one per entity in <c>clearmonsters</c> (classic SP): the entity
/// carries <c>attr1</c> = yaw, <c>attr2</c> = monster type (index into
/// <see cref="MonsterTypes.All"/>), <c>attr3</c> = tag.
///
/// Representation only — the AI that drives the monster (<c>monster.cpp</c>,
/// <c>ai.cpp</c>, <c>waypoint.cpp</c>) is game logic and not ported. The resolved
/// stats are exposed so a future runtime can build the real <c>monster</c> struct.
/// </summary>
[GlobalClass]
public partial class Monster : Models.ModelInstance
{
    /// <summary>attr2: monster type index (see <see cref="MonsterTypes"/>).</summary>
    [Export] public int MonsterType { get; set; }

    /// <summary>attr3: tag (used by <c>monster_dead_&lt;tag&gt;</c> and <c>nummonsters</c>).</summary>
    [Export] public int Tag { get; set; }

    /// <summary>attr1: facing yaw in degrees (Sauerbraten convention).</summary>
    [Export] public float Yaw { get; set; }

    /// <summary>The engine model name (e.g. "ogro", "monster/rhino").</summary>
    [Export] public string? ModelPath { get; set; }

    /// <summary>View-weapon model, if any (e.g. "monster/rat/vwep").</summary>
    [Export] public string? ViewWeapon { get; set; }

    /// <summary>Display name (e.g. "an ogro").</summary>
    [Export] public string DisplayName { get; set; } = "";

    // ---- Resolved monster-type stats (monster.cpp monstertypes) ----

    [Export] public int Gun { get; set; }
    [Export] public int Health { get; set; }
    [Export] public int Speed { get; set; }
    [Export] public int Weight { get; set; }

    /// <summary>bscale ×10. The engine scales the body: radius ×= bscale/10, eyeheight = 8·bscale/10, aboveeye = 7·bscale/10.</summary>
    [Export] public int BScale { get; set; }

    /// <summary>Entity body radius (player radius 4.1 scaled by <see cref="BScale"/>/10).</summary>
    [Export] public float Radius { get; set; }

    /// <summary>Entity eye height (8 · bscale/10).</summary>
    [Export] public float EyeHeight { get; set; }

    /// <summary>Entity above-eye clearance (7 · bscale/10).</summary>
    [Export] public float AboveEye { get; set; }
}
