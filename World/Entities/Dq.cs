using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Dual quaternion, ported from <c>shared/geom.h</c> <c>struct dualquat</c> (r6889).
/// Only the operations the MD5 animation compose needs are implemented
/// (<c>mul</c>, <c>invert</c>, <c>fixantipodal</c>, translation recovery), matching
/// the engine's arithmetic exactly. <see cref="Real"/> is the rotation,
/// <see cref="Dual"/> the translation part.
/// </summary>
public readonly struct Dq
{
    public readonly Quat Real, Dual;

    public Dq(Quat real, Quat dual) { Real = real; Dual = dual; }

    /// <summary>geom.h <c>dualquat(const quat &amp;q, const vec &amp;p)</c>.</summary>
    public Dq(Quat q, Vector3 p) : this(q,
        new Quat(
            0.5f * (p.X * q.W + p.Y * q.Z - p.Z * q.Y),
            0.5f * (-p.X * q.Z + p.Y * q.W + p.Z * q.X),
            0.5f * (p.X * q.Y - p.Y * q.X + p.Z * q.W),
            -0.5f * (p.X * q.X + p.Y * q.Y + p.Z * q.Z)))
    { }

    /// <summary>geom.h <c>dualquat::invert()</c>.</summary>
    public Dq Inverse()
    {
        Quat real = Real.Invert();
        Quat dual = Dual.Invert();
        dual = new Quat(dual.X - Real.X * (2f * Real.Dot(Dual)),
                        dual.Y - Real.Y * (2f * Real.Dot(Dual)),
                        dual.Z - Real.Z * (2f * Real.Dot(Dual)),
                        dual.W - Real.W * (2f * Real.Dot(Dual)));
        return new Dq(real, dual);
    }

    /// <summary>geom.h <c>dualquat::mul(const dualquat &amp;p, const dualquat &amp;o)</c>.</summary>
    public static Dq Mul(Dq p, Dq o)
    {
        Quat real = Quat.Mul(p.Real, o.Real);
        Quat od = Quat.Mul(p.Real, o.Dual);
        Quat pd = Quat.Mul(p.Dual, o.Real);
        Quat dual = new(od.X + pd.X, od.Y + pd.Y, od.Z + pd.Z, od.W + pd.W);
        return new Dq(real, dual);
    }

    public Dq Mul(Dq o) => Mul(this, o);

    /// <summary>geom.h <c>dualquat::fixantipodal</c> — negate when the rotations are opposed.</summary>
    public Dq FixAntipodal(Dq d)
    {
        if (Real.Dot(d.Real) < 0f)
            return new Dq(new Quat(-Real.X, -Real.Y, -Real.Z, -Real.W),
                          new Quat(-Dual.X, -Dual.Y, -Dual.Z, -Dual.W));
        return this;
    }

    /// <summary>Recover the translation: <c>t = 2 * dual * conj(real)</c>.</summary>
    public Vector3 Translation()
    {
        Quat rc = Real.Invert();
        Quat t = Quat.Mul(Dual, rc);
        return new Vector3(2f * t.X, 2f * t.Y, 2f * t.Z);
    }
}
