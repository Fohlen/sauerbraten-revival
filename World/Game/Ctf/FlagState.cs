using System;
using Godot;

namespace SauerbratenRevival.World.Game.Ctf;

/// <summary>
/// State of a single flag entity in CTF, Protect, or Hold modes.
/// Direct port of <c>ctfclientmode::flag</c> and <c>ctfservmode::flag</c> (fpsgame/ctf.h:25, r6889).
/// </summary>
public sealed class FlagState
{
    public int Id { get; set; } = -1;
    public int Version { get; set; }
    public int SpawnIndex { get; set; } = -1;

    /// <summary>Spawn location in Sauerbraten world coordinates.</summary>
    public Vector3 SpawnLoc { get; set; } = Vector3.Zero;

    /// <summary>Dropped location in Sauerbraten world coordinates.</summary>
    public Vector3 DropLoc { get; set; } = Vector3.Zero;

    /// <summary>Team owning/associated with this flag (1 = good/blue, 2 = evil/red, 0 = neutral).</summary>
    public int Team { get; set; }

    /// <summary>Timestamp (ms) when the flag was dropped; 0 if not dropped.</summary>
    public int DropTime { get; set; }

    /// <summary>Timestamp (ms) when current owner took the flag.</summary>
    public int OwnTime { get; set; }

    /// <summary>Timestamp (ms) baseline for hold mode countdown.</summary>
    public int HoldTime { get; set; }

    /// <summary>
    /// Player ID currently carrying this flag, or null if unowned. Mutated by
    /// <see cref="FlagManager"/> through its carrier index so both stay in sync.
    /// </summary>
    public int? Owner { get; internal set; }

    /// <summary>Previous dropper player ID (for pickup throttling).</summary>
    public int Dropper { get; set; } = -1;

    /// <summary>Consecutive drop count by the same player.</summary>
    public int DropCount { get; set; }

    /// <summary>Timestamp (ms) until which flag is invisible/unscoreable (protect mode score cooldown).</summary>
    public int InvisTime { get; set; }

    /// <summary>Facing angle in degrees at spawn location (ctf.h:34 spawnangle).</summary>
    public float SpawnAngle { get; set; }

    /// <summary>Facing angle in degrees at drop location (ctf.h:34 dropangle).</summary>
    public float DropAngle { get; set; }

    public bool HasOwner => Owner.HasValue;

    public void Reset()
    {
        Version = 0;
        SpawnIndex = -1;
        DropLoc = SpawnLoc = Vector3.Zero;
        DropAngle = SpawnAngle = 0;
        DropCount = 0;
        Owner = null;
        Dropper = -1;
        InvisTime = 0;
        Team = 0;
        DropTime = OwnTime = HoldTime = 0;
    }

    /// <summary>
    /// Current position of the flag in world coordinates.
    /// If carried, caller typically queries the carrier's position.
    /// </summary>
    public Vector3 Pos(Func<int, Vector3>? getOwnerPos = null)
    {
        if (Owner.HasValue && getOwnerPos != null)
            return getOwnerPos(Owner.Value);
        if (DropTime > 0)
            return DropLoc;
        return SpawnLoc;
    }

    /// <summary>
    /// Elapsed hold time in milliseconds (ctf.h:83 <c>holdcounter()</c>).
    /// </summary>
    public int HoldCounter(int lastMillis)
    {
        if (HoldTime == 0) return 0;
        if (!HasOwner && DropTime > 0 && DropTime >= HoldTime)
            return Math.Max((DropTime - HoldTime) - (lastMillis - DropTime), 0);
        return lastMillis - HoldTime;
    }

    /// <summary>
    /// Calculates visual interpolated flag position and facing angle in world space.
    /// Port of <c>ctf.h:637: interpflagpos</c>.
    /// When carried, positioned above carrier's head (+1 Z above AboveHead).
    /// </summary>
    public Vector3 InterpFlagPos(
        out float angle,
        bool isHold,
        Vector3? cameraPos = null,
        Func<int, (Vector3 pos, float yaw, float aboveEye)>? getOwnerInfo = null)
    {
        Vector3 pos;
        if (Owner.HasValue && getOwnerInfo != null)
        {
            var info = getOwnerInfo(Owner.Value);
            // abovehead() = o.z + aboveeye + 4; then + 1
            pos = new Vector3(info.pos.X, info.pos.Y, info.pos.Z + info.aboveEye + 4f + 1f);
            angle = info.yaw;
        }
        else
        {
            pos = DropTime > 0 ? DropLoc : SpawnLoc;
            if (isHold && cameraPos.HasValue)
            {
                Physics.Movement.VecToYawPitch(pos - cameraPos.Value, out float yaw, out _);
                angle = yaw + 180f;
            }
            else
            {
                angle = DropTime > 0 ? DropAngle : SpawnAngle;
            }
        }
        return pos;
    }
}
