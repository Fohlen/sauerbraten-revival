using Godot;

namespace SauerbratenRevival.World.Physics;

/// <summary>
/// The velocity model from src/engine/physics.cpp (SVN r6889): direction from
/// yaw/pitch, friction lerp, gravity. Ported as pure functions so both the live
/// <c>Player</c> and the A3 trace replayer share one implementation.
/// </summary>
public static class Movement
{
    /// <summary>physics.cpp:1548 <c>vecfromyawpitch</c> (Z-up, yaw=0 faces +Y).</summary>
    public static Vector3 VecFromYawPitch(float yaw, float pitch, int move, int strafe)
    {
        Vector3 m = Vector3.Zero;
        if (move != 0)
        {
            m.X = move * -Mathf.Sin(Mathf.DegToRad(yaw));
            m.Y = move * Mathf.Cos(Mathf.DegToRad(yaw));
        }
        if (pitch != 0)
        {
            m.X *= Mathf.Cos(Mathf.DegToRad(pitch));
            m.Y *= Mathf.Cos(Mathf.DegToRad(pitch));
            m.Z = move * Mathf.Sin(Mathf.DegToRad(pitch));
        }
        if (strafe != 0)
        {
            m.X += strafe * Mathf.Cos(Mathf.DegToRad(yaw));
            m.Y += strafe * Mathf.Sin(Mathf.DegToRad(yaw));
        }
        return m;
    }

    /// <summary>physics.cpp:1589 <c>modifyvelocity</c> (allowmove() assumed true).</summary>
    public static void ModifyVelocity(PhysEnt pl, bool water, bool floating, int curtime)
    {
        if (floating)
        {
            if (pl.Jumping) { pl.Jumping = false; pl.Vel = pl.Vel with { Z = Mathf.Max(pl.Vel.Z, Phys.JumpVel) }; }
        }
        else if (pl.PhysState >= Phys.PhysSlope || water)
        {
            if (water) pl.Vel /= 8f;
            if (pl.Jumping)
            {
                pl.Jumping = false;
                pl.Vel = pl.Vel with { Z = Mathf.Max(pl.Vel.Z, Phys.JumpVel) };
                if (water) pl.Vel = pl.Vel with { X = pl.Vel.X / 8f, Y = pl.Vel.Y / 8f };
            }
        }
        if (!floating && pl.PhysState == Phys.PhysFall) pl.TimeInAir = Mathf.Min(pl.TimeInAir + curtime, 1000);

        Vector3 m = Vector3.Zero;
        if (pl.Move != 0 || pl.Strafe != 0)
        {
            m = VecFromYawPitch(pl.Yaw, floating || water || pl.Type == Phys.EntCamera ? 0f : 0f, pl.Move, pl.Strafe);
            if (!floating && pl.PhysState >= Phys.PhysSlope)
            {
                float dz = -(m.X * pl.Floor.X + m.Y * pl.Floor.Y) / pl.Floor.Z;
                m = m with { Z = water ? Mathf.Max(m.Z, dz) : dz };
            }
            if (m.LengthSquared() > 1e-12f) m = m.Normalized();
        }

        Vector3 d = m * pl.MaxSpeed;
        if (pl.Type == Phys.EntPlayer)
        {
            if (floating) { /* floatspeed/100 for the local player; not used here */ }
            else if (!water) d *= (pl.Move != 0 && pl.Strafe == 0 ? 1.3f : 1.0f) * (pl.PhysState < Phys.PhysSlope ? 1.3f : 1.0f);
        }
        float fric = water && !floating ? 20.0f : (pl.PhysState >= Phys.PhysSlope || floating ? 6.0f : 30.0f);
        float t = Mathf.Pow(1 - 1 / fric, curtime / 20.0f);
        pl.Vel = d + (pl.Vel - d) * t;
    }

    /// <summary>physics.cpp:1650 <c>modifygravity</c> (allowmove() assumed true).</summary>
    public static void ModifyGravity(PhysEnt pl, bool water, int curtime)
    {
        float secs = curtime / 1000.0f;
        Vector3 g = Vector3.Zero;
        if (pl.PhysState == Phys.PhysFall) g.Z -= Phys.Gravity * secs;
        else if (pl.Floor.Z > 0 && pl.Floor.Z < Phys.FloorZ)
        {
            g.Z = -1;
            g = Project(g, pl.Floor);
            g = g.Normalized() * (Phys.Gravity * secs);
        }
        if (!water || (pl.Move == 0 && pl.Strafe == 0)) pl.Falling += g;

        if (water || pl.PhysState >= Phys.PhysSlope)
        {
            float fric = water ? 2.0f : 6.0f;
            float c = water ? 1.0f : Mathf.Clamp((pl.Floor.Z - Phys.SlopeZ) / (Phys.FloorZ - Phys.SlopeZ), 0.0f, 1.0f);
            pl.Falling *= Mathf.Pow(1 - c / fric, curtime / 20.0f);
        }
    }

    private static Vector3 Project(Vector3 v, Vector3 n) => v - n * v.Dot(n);
}
