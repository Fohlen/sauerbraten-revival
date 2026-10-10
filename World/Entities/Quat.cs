using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Quaternion with the engine's component order/multiplication
/// (<c>shared/geom.h</c> <c>struct quat</c>, r6889). Kept separate from Godot's
/// <see cref="Quaternion"/> so the dual-quat composition math is a faithful,
/// sign-exact port (Godot normalises and may differ on the w sign).
/// </summary>
public readonly struct Quat
{
    public readonly float X, Y, Z, W;
    public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

    /// <summary>geom.h <c>quat::mul</c> (<c>this = p * o</c>).</summary>
    public static Quat Mul(Quat p, Quat o) => new(
        p.W * o.X + p.X * o.W + p.Y * o.Z - p.Z * o.Y,
        p.W * o.Y - p.X * o.Z + p.Y * o.W + p.Z * o.X,
        p.W * o.Z + p.X * o.Y - p.Y * o.X + p.Z * o.W,
        p.W * o.W - p.X * o.X - p.Y * o.Y - p.Z * o.Z);

    public Quat Mul(Quat o) => Mul(this, o);

    /// <summary>geom.h <c>quat::invert()</c>: negate the vector part, keep w.</summary>
    public Quat Invert() => new(-X, -Y, -Z, W);

    public float Dot(Quat o) => X * o.X + Y * o.Y + Z * o.Z + W * o.W;

    public void NegateRef(out Quat r) => r = new(-X, -Y, -Z, -W);
}
