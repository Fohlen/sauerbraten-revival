using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// A monster's first-person view weapon (<c>monstertypes[].vwepname</c>). The
/// engine attaches it to the <c>tag_weapon</c> bone of the body model in
/// <c>rendermonsters</c> (monster.cpp); here it is a child node of the
/// <see cref="Monster"/>. Representation only — the runtime attachment/anim
/// (<c>ANIM_VWEP_IDLE</c>/<c>ANIM_VWEP_SHOOT</c>) is game logic.
/// </summary>
[GlobalClass]
public partial class MonsterViewWeapon : Models.ModelInstance
{
    /// <summary>The engine model name (e.g. "monster/rat/vwep").</summary>
    [Export] public string? ModelPath { get; set; }
}
