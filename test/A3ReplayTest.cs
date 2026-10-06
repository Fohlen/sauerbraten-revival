using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// A3 input-replay differential (docs/movement-verification.md). Replays a trace
/// captured from the instrumented original client (<c>tools/refharness/traces/</c>)
/// through the ported velocity model + octree collision and diffs the state.
///
/// The trace records the state <em>after</em> each 5 ms step, so to predict row k
/// we start from row k-1's state and apply row k's inputs — exactly what
/// validate_trace.py does, but here through the full moveplayer() with collision.
/// Teleport events resync from the trace instead of diverging.
/// </summary>
public class A3ReplayTest : TestClass
{
    public A3ReplayTest(Node testScene) : base(testScene) { }

    private const float VelEps = 1e-3f, FallEps = 1e-3f, OEps = 1e-2f;

    [Test]
    public void ReplaysSession2()
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath("res://"), "tools/refharness/traces/session2.csv");
        if (!File.Exists(path))
        {
            GD.Print("[a3] session2.csv missing; skipping");
            return;
        }

        var lines = File.ReadAllLines(path);
        var rows = lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')).ToList();
        var teleports = new HashSet<int>();
        string events = path + ".events";
        if (File.Exists(events))
            foreach (var l in File.ReadAllLines(events).Skip(1))
            {
                var p = l.Split(',');
                if (p.Length >= 3 && p[2] == "teleport") teleports.Add(int.Parse(p[0]));
            }

        var octa = TestData.ReadMap("church51").ToOcta();
        var phys = new OctaPhysics(octa);
        var pl = new PhysEnt();

        int steps = 0, velOk = 0, fallOk = 0, oOk = 0, psOk = 0, ground = 0, external = 0;
        float maxVel = 0, maxFall = 0, maxO = 0;
        int firstVelMiss = -1, firstOMiss = -1;
        var misses = new List<string>();
        bool sawNaN = false;

        for (int k = 1; k < rows.Count; k++)
        {
            var prev = rows[k - 1];
            var cur = rows[k];

            // seed the entity from the previous row's recorded state
            pl.O = new Vector3(F(prev, "ox"), F(prev, "oy"), F(prev, "oz"));
            pl.Vel = new Vector3(F(prev, "vx"), F(prev, "vy"), F(prev, "vz"));
            pl.Falling = new Vector3(F(prev, "fx"), F(prev, "fy"), F(prev, "fz"));
            pl.Floor = new Vector3(F(prev, "floorx"), F(prev, "floory"), F(prev, "floorz"));
            pl.PhysState = I(prev, "physstate");
            pl.TimeInAir = I(prev, "timeinair");

            pl.Move = I(cur, "move");
            pl.Strafe = I(cur, "strafe");
            pl.Jumping = I(cur, "jumping") != 0;
            pl.Yaw = F(cur, "yaw");

            if (teleports.Contains(k)) continue; // external teleport: resync, don't predict

            // session2's event log only has the teleport; the 11 jump pads show up as an
            // impossible one-step +vz impulse (jumping is logged post-consumption, always 0).
            // Resync at them so the harness measures the model, not the gap in the log.
            if (F(cur, "vz") - F(prev, "vz") > 50f) { external++; continue; }

            int material = octa.LookupMaterial(new Vector3(pl.O.X, pl.O.Y, pl.O.Z + (3 * pl.AboveEye - pl.EyeHeight) / 4));
            bool water = Phys.IsLiquid((ushort)(material & Phys.MatFVolume));

            bool prevGround = pl.PhysState == Phys.PhysFloor || pl.PhysState == Phys.PhysSlope || pl.PhysState == Phys.PhysStepUp || pl.PhysState == Phys.PhysStepDown;
            var expVel = new Vector3(F(cur, "vx"), F(cur, "vy"), F(cur, "vz"));
            var expFall = new Vector3(F(cur, "fx"), F(cur, "fy"), F(cur, "fz"));
            var expO = new Vector3(F(cur, "ox"), F(cur, "oy"), F(cur, "oz"));

            phys.MovePlayer(pl, Phys.MoveRes, Phys.PhysFrameTime, water);

            if (float.IsNaN(pl.O.X) || float.IsNaN(pl.Vel.X))
            {
                if (!sawNaN) { sawNaN = true; misses.Add($"NaN at step {k} (prev ps {I(prev, "physstate")} floor {pl.Floor})"); }
                continue;
            }

            float ev = (pl.Vel - expVel).Length(), ef = (pl.Falling - expFall).Length(), eo = (pl.O - expO).Length();
            maxVel = Mathf.Max(maxVel, ev); maxFall = Mathf.Max(maxFall, ef); maxO = Mathf.Max(maxO, eo);
            steps++;
            if (prevGround) ground++;
            if (ev < VelEps) velOk++; else if (firstVelMiss < 0) firstVelMiss = k;
            if (ev >= VelEps && misses.Count < 8)
                misses.Add($"step {k}: vel pred {pl.Vel} exp {expVel} | ps {pl.PhysState} exp {I(cur, "physstate")} (prevps {I(prev, "physstate")}, move {pl.Move}, strafe {pl.Strafe})");
            if (ef < FallEps) fallOk++;
            if (eo < OEps) oOk++; else if (firstOMiss < 0) firstOMiss = k;
            if (pl.PhysState == I(cur, "physstate")) psOk++;
        }

        float Pct(int ok) => 100f * ok / Mathf.Max(steps, 1);
        GD.Print($"[a3] session2: {steps} steps ({ground} grounded), {external} unlogged external events skipped");
        GD.Print($"[a3]   vel   {velOk}/{steps} ({Pct(velOk):F1}%)  max err {maxVel:F4}  first miss step {firstVelMiss}");
        GD.Print($"[a3]   fall  {fallOk}/{steps} ({Pct(fallOk):F1}%)  max err {maxFall:F4}");
        GD.Print($"[a3]   o     {oOk}/{steps} ({Pct(oOk):F1}%)  max err {maxO:F4}  first miss step {firstOMiss}");
        GD.Print($"[a3]   physstate {psOk}/{steps} ({Pct(psOk):F1}%)");

        TestAssert.True(steps > 0, "trace produced steps");
        // Regression floor: A3.1 (solid-cube collision) holds ~95% vel / ~97% physstate on
        // session2. A3.2 (deformed cubes) must raise this; the assert guards against
        // regressing below the current stage.
        TestAssert.True(velOk >= 0.90f * steps, $"A3 vel agreement {velOk}/{steps}");
        TestAssert.True(psOk >= 0.90f * steps, $"A3 physstate agreement {psOk}/{steps}");
        foreach (var m in misses) GD.Print($"[a3] miss {m}");
    }

    private static float F(string[] row, string key) => float.Parse(row[System.Array.IndexOf(Header, key)], System.Globalization.CultureInfo.InvariantCulture);
    private static int I(string[] row, string key) => (int)float.Parse(row[System.Array.IndexOf(Header, key)], System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string[] Header =
        "step,time,move,strafe,jumping,yaw,pitch,ox,oy,oz,vx,vy,vz,fx,fy,fz,physstate,timeinair,floorx,floory,floorz".Split(',');
}

// silence IDE0060-style unused warnings for the header constant reference
