using System;
using Godot;

namespace SauerbratenRevival.World.Network.Interpolation;

/// <summary>
/// Faithful port of Sauerbraten r6889 network movement smoothing and prediction
/// (<c>src/fpsgame/client.cpp:1189-1207</c> and <c>src/fpsgame/fps.cpp:169-193</c>).
///
/// Smooths remote entity position and orientation updates over a temporal window
/// (<see cref="SmoothMove"/>, default 75 ms) by maintaining an authoritative target
/// (<see cref="NewPos"/>, <see cref="NewYaw"/>, <see cref="NewPitch"/>) while decaying
/// the visual offset residual (<see cref="DeltaPos"/>, <see cref="DeltaYaw"/>, etc.)
/// smoothly towards zero. Teleports or updates exceeding <see cref="SmoothDist"/>
/// (default 32 Sauerbraten units) bypass smoothing and snap immediately.
/// </summary>
public sealed class PlayerInterpolation
{
    /// <summary>Default smoothing window in milliseconds (fpsgame/fps.cpp VARP smoothmove 75).</summary>
    public const int DefaultSmoothMove = 75;

    /// <summary>Default maximum distance threshold for smoothing in Sauerbraten units (fpsgame/fps.cpp VARP smoothdist 32).</summary>
    public const float DefaultSmoothDist = 32.0f;

    /// <summary>Smoothing duration in milliseconds (0 disables smoothing, max 100).</summary>
    public int SmoothMove { get; set; } = DefaultSmoothMove;

    /// <summary>Maximum position difference across which smoothing is permitted (max 64).</summary>
    public float SmoothDist { get; set; } = DefaultSmoothDist;

    /// <summary>Timestamp (lastmillis) when the current smoothing window began. -1 indicates initial uninitialized state.</summary>
    public int SmoothMillis { get; set; } = -1;

    /// <summary>Authoritative target position received from packet / dead reckoning.</summary>
    public Vector3 NewPos { get; set; }

    /// <summary>Authoritative target yaw received from packet / dead reckoning.</summary>
    public float NewYaw { get; set; }

    /// <summary>Authoritative target pitch received from packet / dead reckoning.</summary>
    public float NewPitch { get; set; }

    /// <summary>Authoritative target roll received from packet / dead reckoning.</summary>
    public float NewRoll { get; set; }

    /// <summary>Residual position offset (oldpos - newpos) being decayed to zero.</summary>
    public Vector3 DeltaPos { get; set; }

    /// <summary>Residual yaw offset with shortest-arc wrapping being decayed to zero.</summary>
    public float DeltaYaw { get; set; }

    /// <summary>Residual pitch offset being decayed to zero.</summary>
    public float DeltaPitch { get; set; }

    /// <summary>Residual roll offset being decayed to zero.</summary>
    public float DeltaRoll { get; set; }

    public PlayerInterpolation()
    {
        Reset();
    }

    /// <summary>
    /// Resets interpolation state to initial defaults (smoothmillis = -1, zero deltas).
    /// Used on entity creation or respawn.
    /// </summary>
    public void Reset()
    {
        SmoothMillis = -1;
        NewPos = Vector3.Zero;
        NewYaw = 0f;
        NewPitch = 0f;
        NewRoll = 0f;
        DeltaPos = Vector3.Zero;
        DeltaYaw = 0f;
        DeltaPitch = 0f;
        DeltaRoll = 0f;
    }

    /// <summary>
    /// Resets interpolation target to the given position and orientation without smoothing
    /// (fpsgame/fps.cpp:449-450 resetinterp). Sets smoothmillis = 0.
    /// </summary>
    public void ResetInterp(Vector3 currentPos, float currentYaw, float currentPitch, float currentRoll = 0f)
    {
        SmoothMillis = 0;
        NewPos = currentPos;
        NewYaw = currentYaw;
        NewPitch = currentPitch;
        NewRoll = currentRoll;
        DeltaPos = Vector3.Zero;
        DeltaYaw = 0f;
        DeltaPitch = 0f;
        DeltaRoll = 0f;
    }

    /// <summary>
    /// Called when an authoritative position packet arrives (from network N_POS or demo stream).
    /// Faithful port of <c>fpsgame/client.cpp:1189-1207</c>.
    /// </summary>
    /// <param name="pos">In/out position. On return with active smoothing, holds the retained visual position.</param>
    /// <param name="yaw">In/out yaw degrees. On return with active smoothing, holds the retained visual yaw.</param>
    /// <param name="pitch">In/out pitch degrees. On return with active smoothing, holds the retained visual pitch.</param>
    /// <param name="roll">In/out roll degrees. On return with active smoothing, holds the retained visual roll.</param>
    /// <param name="packetPos">New authoritative position from packet.</param>
    /// <param name="packetYaw">New authoritative yaw from packet.</param>
    /// <param name="packetPitch">New authoritative pitch from packet.</param>
    /// <param name="packetRoll">New authoritative roll from packet.</param>
    /// <param name="lastMillis">Current simulation timestamp in milliseconds.</param>
    public void OnRemotePosition(
        ref Vector3 pos, ref float yaw, ref float pitch, ref float roll,
        Vector3 packetPos, float packetYaw, float packetPitch, float packetRoll,
        int lastMillis)
    {
        float oldYaw = yaw, oldPitch = pitch, oldRoll = roll;
        yaw = packetYaw;
        pitch = packetPitch;
        roll = packetRoll;
        Vector3 oldPos = pos;
        pos = packetPos;

        if (SmoothMove > 0 && SmoothMillis >= 0 && oldPos.DistanceTo(pos) < SmoothDist)
        {
            NewPos = pos;
            NewYaw = yaw;
            NewPitch = pitch;
            NewRoll = roll;

            pos = oldPos;
            yaw = oldYaw;
            pitch = oldPitch;
            roll = oldRoll;

            DeltaPos = oldPos - NewPos;
            DeltaYaw = ShortestAngleDelta(oldYaw, NewYaw);
            DeltaPitch = oldPitch - NewPitch;
            DeltaRoll = oldRoll - NewRoll;
            SmoothMillis = lastMillis;
        }
        else
        {
            SmoothMillis = 0;
            NewPos = pos;
            NewYaw = yaw;
            NewPitch = pitch;
            NewRoll = roll;
            DeltaPos = Vector3.Zero;
            DeltaYaw = 0f;
            DeltaPitch = 0f;
            DeltaRoll = 0f;
        }
    }

    /// <summary>
    /// Advances prediction and computes interpolated visual coordinates.
    /// Faithful port of <c>fpsgame/fps.cpp:172-193 predictplayer</c>.
    /// </summary>
    /// <param name="pos">In/out position updated to predicted + smoothed value.</param>
    /// <param name="yaw">In/out yaw updated to predicted + smoothed value.</param>
    /// <param name="pitch">In/out pitch updated to predicted + smoothed value.</param>
    /// <param name="roll">In/out roll updated to predicted + smoothed value.</param>
    /// <param name="lastMillis">Current simulation timestamp in milliseconds.</param>
    /// <param name="deadReckon">Optional physics step to advance base position (e.g. MovePlayer).</param>
    public void Predict(
        ref Vector3 pos, ref float yaw, ref float pitch, ref float roll,
        int lastMillis, Action? deadReckon = null)
    {
        pos = NewPos;
        yaw = NewYaw;
        pitch = NewPitch;
        roll = NewRoll;

        if (deadReckon != null)
        {
            deadReckon();
            NewPos = pos;
        }

        if (SmoothMove > 0 && SmoothMillis > 0)
        {
            float k = 1.0f - (float)(lastMillis - SmoothMillis) / SmoothMove;
            if (k > 0f)
            {
                pos += DeltaPos * k;
                yaw += DeltaYaw * k;
                yaw = NormalizeYaw(yaw);
                pitch += DeltaPitch * k;
                roll += DeltaRoll * k;
            }
        }
    }

    /// <summary>
    /// Computes the shortest angular delta between two angles in degrees (accounting for 360 wrap).
    /// Faithful port of <c>fpsgame/client.cpp:1200-1202</c>.
    /// </summary>
    public static float ShortestAngleDelta(float oldAngle, float newAngle)
    {
        float delta = oldAngle - newAngle;
        if (delta > 180.0f) delta -= 360.0f;
        else if (delta < -180.0f) delta += 360.0f;
        return delta;
    }

    /// <summary>
    /// Wraps yaw in degrees into [0, 360) matching Sauerbraten conventions.
    /// Faithful port of <c>fpsgame/fps.cpp:188-189</c>.
    /// </summary>
    public static float NormalizeYaw(float yaw)
    {
        while (yaw < 0.0f) yaw += 360.0f;
        while (yaw >= 360.0f) yaw -= 360.0f;
        return yaw;
    }
}
