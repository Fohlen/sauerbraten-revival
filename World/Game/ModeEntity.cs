using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// A Cube 2 game-mode entity: <c>FLAG</c>, <c>BASE</c>, <c>BOX</c>,
/// <c>BARREL</c>, <c>PLATFORM</c>, <c>ELEVATOR</c>, <c>RESPAWNPOINT</c> or
/// <c>CARROT</c>. Representation only — the gameplay that drives them
/// (<c>ctf.h</c>/<c>capture.h</c>/<c>movable.cpp</c>) is not ported yet, and the
/// movable physics needs <c>bounce()</c> first (features.md roadmap item 3).
///
/// The raw attributes are kept verbatim, alongside the resolved, shifted values
/// the engine works with (see <see cref="ModeEntityBuilder"/> and
/// <c>entities::fixentity</c>, which moves <c>attr4→attr5</c>/<c>attr3→attr4</c>).
/// </summary>
[GlobalClass]
public partial class ModeEntity : Models.ModelInstance
{
    /// <summary>shared/ents.h + fpsgame/game.h type id (see <see cref="EntityType"/>).</summary>
    [Export] public int ModeType { get; set; }

    [Export] public string ModeName { get; set; } = "";

    // Raw entity attributes, exactly as loaded from the map.
    [Export] public int Attr1 { get; set; }
    [Export] public int Attr2 { get; set; }
    [Export] public int Attr3 { get; set; }
    [Export] public int Attr4 { get; set; }
    [Export] public int Attr5 { get; set; }

    /// <summary>attr1: facing yaw in degrees (FLAG/BASE/BOX/BARREL/PLATFORM/ELEVATOR/RESPAWNPOINT).</summary>
    [Export] public float Yaw { get; set; }

    /// <summary>Engine model name (mapmodel name for movables, built-in for flag/base/carrot/checkpoint); null if unresolved.</summary>
    [Export] public string? ModelPath { get; set; }

    // ---- Resolved, engine-semantic fields (post-fixentity) ----

    /// <summary>FLAG: team (1 = blue/good, 2 = red/evil). 0 for other types.</summary>
    [Export] public int Team { get; set; }

    /// <summary>BASE: ammo type; PLATFORM/ELEVATOR: tag. 0 for other types.</summary>
    [Export] public int Tag { get; set; }

    /// <summary>BASE: tag (attr2). 0 for other types.</summary>
    [Export] public int BaseIndex { get; set; }

    /// <summary>BOX/BARREL/PLATFORM/ELEVATOR: resolver weight (attr3 pre-shift).</summary>
    [Export] public int Weight { get; set; }

    /// <summary>BARREL: health (attr4 pre-shift; engine default 50).</summary>
    [Export] public int Health { get; set; }

    /// <summary>PLATFORM/ELEVATOR: max speed (attr4 pre-shift; 0 → engine default 8).</summary>
    [Export] public float Speed { get; set; }

    /// <summary>PLATFORM/ELEVATOR: movement sign from attr4 (negative = reversed).</summary>
    [Export] public int Direction { get; set; } = 1;
}
