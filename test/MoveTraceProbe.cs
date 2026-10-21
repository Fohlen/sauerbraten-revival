using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Diagnostic: dumps the port's move() decision chain for one step of a trace
/// (see <see cref="OctaPhysics.MoveTrace"/>) so it can be diffed against the
/// reference's SAUER_MOVETRACE dump (tools/refharness/patches/a2e-move-trace.patch).
/// Used to chase the remaining step/slide residual. See docs/movement-verification.md.
/// </summary>
public class MoveTraceProbe : GoTestBase
{
    public MoveTraceProbe(Node testScene) : base(testScene) { }

    [Test] public void Step3036() => Probe("session-stable3.csv", 3036);

    private static void Probe(string file, int step)
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath("res://"), "tools/refharness/traces", file);
        if (!File.Exists(path)) { GD.Print("[movetrace] missing trace"); return; }
        var rows = File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')).ToList();
        var prev = rows[step - 1];
        var cur = rows[step];

        var octa = TestData.ReadMap("church51").ToOcta();
        var phys = new OctaPhysics(octa);
        var pl = new PhysEnt
        {
            O = new Vector3(F(prev, 7), F(prev, 8), F(prev, 9)),
            Vel = new Vector3(F(prev, 10), F(prev, 11), F(prev, 12)),
            Falling = new Vector3(F(prev, 13), F(prev, 14), F(prev, 15)),
            Floor = new Vector3(F(prev, 18), F(prev, 19), F(prev, 20)),
            PhysState = (int)F(prev, 16),
            TimeInAir = (int)F(prev, 17),
            Move = (int)F(cur, 2),
            Strafe = (int)F(cur, 3),
            Jumping = F(cur, 4) != 0,
            Yaw = F(cur, 5),
        };

        GD.Print($"[movetrace] step {step}: prev ps={pl.PhysState} o={pl.O} v={pl.Vel} flr={pl.Floor} | inputs m={pl.Move} s={pl.Strafe}");
        var log = new List<string>();
        phys.MoveTrace = log.Add;
        phys.MovePlayer(pl, Phys.MoveRes, Phys.PhysFrameTime);
        phys.MoveTrace = null;

        foreach (var l in log) GD.Print($"[mt] {l}");
        GD.Print($"[movetrace] predicted v={pl.Vel} ps={pl.PhysState} o={pl.O}");
        GD.Print($"[movetrace] expected  v=({F(cur, 10)},{F(cur, 11)},{F(cur, 12)}) ps={F(cur, 16)} o=({F(cur, 7)},{F(cur, 8)},{F(cur, 9)})");
    }

    private static float F(string[] row, int i) => float.Parse(row[i], CultureInfo.InvariantCulture);
}
