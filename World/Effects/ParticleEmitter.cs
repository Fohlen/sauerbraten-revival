using Godot;

namespace SauerbratenRevival.World.Effects;

/// <summary>
/// An <c>ET_PARTICLES</c> entity. The effect id is <see cref="Effect"/> (attr1);
/// the meaning of attr2..5 depends on it (`makeparticles`). <see cref="NeedsCustomRenderer"/>
/// marks effects a billboard can't represent. Representation only for now — the
/// actual effect rendering is a later slice.
/// </summary>
[GlobalClass]
public partial class ParticleEmitter : Node3D
{
    [Export] public int Effect { get; set; }
    [Export] public string EffectName { get; set; } = "";
    [Export] public int Attr2 { get; set; }
    [Export] public int Attr3 { get; set; }
    [Export] public int Attr4 { get; set; }
    [Export] public int Attr5 { get; set; }
    [Export] public bool NeedsCustomRenderer { get; set; }
}
