using Godot;
using SauerbratenRevival.World.Audio;

namespace SauerbratenRevival.World.Weapons;

/// <summary>
/// One row of the engine's <c>guns[NUMGUNS]</c> table (fpsgame/game.h:348),
/// covering both the seven player weapons and the monster/projectile pseudo-guns.
/// Static definition data only — the firing/hitscan code that consumes it
/// (<c>weapon.cpp</c>, <c>server.cpp</c>) is game logic and is not ported yet.
///
/// A Godot <see cref="Resource"/> so weapon defs are typed, inspector-visible and,
/// later, overridable by a mod <c>.tres</c>. The built-in defaults are constructed
/// by <see cref="WeaponCatalog"/>; the authored <c>.tres</c> rows are loaded from
/// <see cref="WeaponCatalog.ResourceDir"/>.
///
/// <c>[Tool]</c> is required for the editor to instantiate the managed type when a
/// <c>.tres</c> is loaded from a tool/editor context (e.g. the viewer's <c>_Ready</c>);
/// without it <see cref="Godot.ResourceLoader"/> hands back a plain
/// <c>Godot.Resource</c> and the typed load throws.
/// </summary>
[Tool]
[GlobalClass]
public partial class WeaponInfo : Resource
{
    /// <summary>Engine <c>GUN_*</c> id (also the index into <see cref="WeaponCatalog.All"/>).</summary>
    [Export] public Gun Id { get; set; }

    /// <summary>Sound played on attack (engine <c>S_*</c>), or <see cref="GameSound.None"/>.</summary>
    [Export] public GameSound Sound { get; set; }

    /// <summary>Milliseconds between attacks (<c>guns[].attackdelay</c>, copied to <c>gunwait</c>).</summary>
    [Export] public int AttackDelay { get; set; }

    [Export] public int Damage { get; set; }

    /// <summary>Spread in engine units per 1024 units of range (0 = perfectly accurate).</summary>
    [Export] public int Spread { get; set; }

    /// <summary>Projectile speed (0 = hitscan).</summary>
    [Export] public int ProjSpeed { get; set; }

    /// <summary>View kick per shot (<c>kickamount</c>).</summary>
    [Export] public int KickAmount { get; set; }

    /// <summary>Maximum hitscan range (<c>raycubepos</c>); 0 = unbounded/unused.</summary>
    [Export] public int Range { get; set; }

    /// <summary>Rays cast per shot (<c>createrays</c>, capped by <c>MAXRAYS</c> = 20).</summary>
    [Export] public int Rays { get; set; }

    /// <summary>Push applied to the victim (<c>hitpush</c>).</summary>
    [Export] public int HitPush { get; set; }

    /// <summary>Explosion radius (0 = none).</summary>
    [Export] public int ExpRad { get; set; }

    /// <summary>Projectile time-to-live in ms (0 = none).</summary>
    [Export] public int Ttl { get; set; }

    /// <summary>Display name (e.g. "rifle"); used by the HUD/particles.</summary>
    [Export] public string Name { get; set; } = "";

    /// <summary>
    /// First-person hudgun submodel directory (e.g. "shotg"), or empty for the
    /// monster/projectile pseudo-guns that have no player model.
    /// </summary>
    [Export] public string File { get; set; } = "";

    /// <summary>Projectile particle type (<c>PART_*</c>); 0 = none.</summary>
    [Export] public int Part { get; set; }

    /// <summary>True for the seven player weapons (<c>GUN_FIST</c>..<c>GUN_PISTOL</c>).</summary>
    public bool PlayerSelectable => Id >= Gun.Fist && Id <= Gun.Pistol;

    /// <summary>True when the weapon is hitscan (no projectile speed).</summary>
    public bool Hitscan => ProjSpeed == 0;

    /// <summary>True when the weapon has a first-person hudgun model.</summary>
    public bool HasModel => !string.IsNullOrEmpty(File);
}
