using System.Globalization;
using System.IO;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// A1 water/spectator-float differential (docs/movement-verification.md). Replays
/// the reference harness scenarios (<c>tools/refharness/golden/*.csv</c>, emitted by
/// the ORIGINAL r6889 movement functions) through the ported <see cref="Movement"/>
/// model + the harness's flat-floor stand-in, and diffs the state to float
/// precision.
///
/// Crouch is deliberately absent: Cube 2 / Sauerbraten r6889 has no crouch — no key,
/// no variable player height. The player volume is fixed (radius 4.1, eyeheight 14,
/// aboveeye 1). See docs/movement-physics.md.
/// </summary>
public class WaterPhysicsTest : TestClass
{
    public WaterPhysicsTest(Node testScene) : base(testScene) { }

    private const float Oeps = 1e-3f, Veps = 1e-3f, Feps = 1e-3f;

    [Test] public void WaterForwardMatchesReference() => Replay("water_forward.csv");
    [Test] public void WaterDiagonalMatchesReference() => Replay("water_diag.csv");
    [Test] public void WaterJumpMatchesReference() => Replay("water_jump.csv");
    [Test] public void SpectatorFloatMatchesReference() => Replay("float_forward.csv");

    /// <summary>Replays one golden scenario through the port and diffs every row.</summary>
    private static void Replay(string file)
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath("res://"),
            "tools/refharness/golden", file);
        TestAssert.True(File.Exists(path), $"{file} golden fixture exists at {path}");

        var rows = File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')).ToList();
        bool water = file.StartsWith("water_");
        bool floating = file.StartsWith("float_");

        var pl = NewPlayer(floating);
        float maxO = 0, maxV = 0, maxF = 0;

        int step = 0;
        foreach (var r in rows)
        {
            // Inputs mirror tools/refharness/main.cpp's scenario lambdas EXACTLY:
            // the CSV's jumping column is post-step (already consumed), so it cannot
            // be replayed; jump is asserted only on the first step.
            if (file.StartsWith("water_")) { pl.Move = 1; pl.Strafe = file.Contains("diag") ? 1 : 0; }
            if (file == "water_jump.csv") pl.Jumping = step == 0;
            if (floating) { pl.Move = 1; pl.Pitch = 20f; }
            pl.Yaw = F(r[4]);

            Step(pl, 5, water, floating);
            step++;

            var expO = new Vector3(F(r[5]), F(r[6]), F(r[7]));
            var expV = new Vector3(F(r[8]), F(r[9]), F(r[10]));
            var expF = new Vector3(F(r[11]), F(r[12]), F(r[13]));
            maxO = Mathf.Max(maxO, (pl.O - expO).Length());
            maxV = Mathf.Max(maxV, (pl.Vel - expV).Length());
            maxF = Mathf.Max(maxF, (pl.Falling - expF).Length());
        }

        GD.Print($"[water] {file}: {rows.Count} steps  max dO={maxO:E2} dV={maxV:E2} dF={maxF:E2}");
        TestAssert.True(maxV < Veps, $"{file} velocity matches original (max {maxV})");
        TestAssert.True(maxF < Feps, $"{file} falling matches original (max {maxF})");
        TestAssert.True(maxO < Oeps, $"{file} position matches original (max {maxO})");
    }

    private static PhysEnt NewPlayer(bool floating) => new()
    {
        O = new Vector3(0, 0, 14f),
        PhysState = floating ? Phys.PhysFloat : Phys.PhysFloor,
        Floor = new Vector3(0, 0, 1),
    };

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Mirrors <c>tools/refharness/main.cpp</c> <c>step()</c> (flat floor).</summary>
    private static void Step(PhysEnt pl, int curtime, bool water, bool floating)
    {
        float secs = curtime / 1000f;
        if (!floating) Movement.ModifyGravity(pl, water, curtime);
        Movement.ModifyVelocity(pl, water, floating, curtime);

        Vector3 d = pl.Vel;
        if (!floating && water) d *= 0.5f;
        d += pl.Falling;
        d *= secs;
        pl.Blocked = false;

        if (floating)
        {
            if (pl.PhysState != Phys.PhysFloat) { pl.PhysState = Phys.PhysFloat; pl.TimeInAir = 0; pl.Falling = Vector3.Zero; }
            pl.O += d;
            pl.InWater = Phys.MatAir;
            return;
        }

        const int moveres = 10;
        d *= 1.0f / moveres;
        for (int i = 0; i < moveres; i++) MoveFlat(pl, d);

        // moveplayer() end-of-step water bookkeeping (main.cpp mirrors this).
        pl.InWater = water ? Phys.MatWater : Phys.MatAir;
    }

    /// <summary>Mirrors <c>tools/refharness/main.cpp</c> <c>move_flat()</c>.</summary>
    private static void MoveFlat(PhysEnt pl, Vector3 dir)
    {
        pl.O += dir;
        const float groundz = 0f;
        float feet = pl.O.Z - pl.EyeHeight;
        if (feet < groundz)
        {
            pl.O = pl.O with { Z = groundz + pl.EyeHeight };
            if (pl.Vel.Z < 0) pl.Vel = pl.Vel with { Z = 0 };
            pl.Falling = Vector3.Zero;
            pl.PhysState = Phys.PhysFloor;
            pl.Floor = new Vector3(0, 0, 1);
            pl.TimeInAir = 0;
            pl.Blocked = true;
        }
        else if (pl.PhysState >= Phys.PhysSlope && feet > groundz + 0.1f)
        {
            pl.PhysState = Phys.PhysFall;
        }
    }
}
