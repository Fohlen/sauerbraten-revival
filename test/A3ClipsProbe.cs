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
/// Diffs our genclipplanes/classifyface/collideface against the engine's
/// SAUER_TRACE.clips dump (deformed cubes the player's collision touched).
/// Temporary diagnostic.
/// </summary>
public class A3ClipsProbe : TestClass
{
    public A3ClipsProbe(Node testScene) : base(testScene) { }

    [Test]
    public void DiffClipPlanes()
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath("res://"), "tools/refharness/traces/session4.csv.clips");
        if (!File.Exists(path)) { GD.Print("[clips] missing; skipping"); return; }

        var octa = TestData.ReadMap("church51").ToOcta();
        var lines = File.ReadAllLines(path);
        int cubes = 0, visMis = 0, sizeMis = 0, oMis = 0, planeMis = 0;
        var examples = new List<string>();

        for (int i = 0; i < lines.Length;)
        {
            if (lines[i].Length == 0 || lines[i][0] != 'C') { i++; continue; }
            var c1 = lines[i].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            int step = int.Parse(c1[1]);
            var co = new Vector3I(int.Parse(c1[3]), int.Parse(c1[4]), int.Parse(c1[5]));
            int size = int.Parse(c1[6]);
            int expVisible = int.Parse(c1[7]);
            int expNPlanes = int.Parse(c1[8]);
            var expO = new Vector3(float.Parse(c1[9], C), float.Parse(c1[10], C), float.Parse(c1[11], C));
            var expR = new Vector3(float.Parse(c1[12], C), float.Parse(c1[13], C), float.Parse(c1[14], C));
            string edgesHex = c1[15];
            i++;

            var c = new Cube { Material = Octa.MAT_AIR };
            var eb = new byte[12];
            for (int k = 0; k < 12; k++) eb[k] = byte.Parse(edgesHex.Substring(k * 2, 2), NumberStyles.HexNumber);
            for (int d = 0; d < 3; d++)
                c.Faces[d] = (uint)eb[d * 4] | ((uint)eb[d * 4 + 1] << 8) | ((uint)eb[d * 4 + 2] << 16) | ((uint)eb[d * 4 + 3] << 24);

            var expFaces = new int[6]; var expCollide = new int[6];
            for (int f = 0; f < 6; f++)
            {
                var f1 = lines[i++].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                expFaces[int.Parse(f1[1])] = int.Parse(f1[2]);
                expCollide[int.Parse(f1[1])] = int.Parse(f1[3]);
            }
            var expPlanes = new List<(float nx, float ny, float nz, float off, int side)>();
            for (int p = 0; p < expNPlanes; p++)
            {
                var p1 = lines[i++].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                expPlanes.Add((float.Parse(p1[2], C), float.Parse(p1[3], C), float.Parse(p1[4], C), float.Parse(p1[5], C), int.Parse(p1[6])));
            }

            cubes++;
            var mask = OctaVisible.CollisionVisibleMask(octa, c, co, size);
            var cp = OctaClipping.GetClipPlanes(octa, c, co, size, mask);

            bool bad = false;
            for (int f = 0; f < 6; f++)
            {
                int cf = OctaVisible.ClassifyFace(octa, c, f, co, size);
                int cd = OctaVisible.CollideFace(c, f) ? 1 : 0;
                if (cf != expFaces[f] || cd != expCollide[f])
                {
                    bad = true;
                    if (examples.Count < 12)
                        examples.Add($"{step} co={co} size={size} face{f}: classify ours {cf} exp {expFaces[f]}, collide ours {cd} exp {expCollide[f]} edges={edgesHex}");
                }
            }
            if (cp.Visible != expVisible) { visMis++; bad = true; if (examples.Count < 12) examples.Add($"{step} co={co} size={size}: visible ours {cp.Visible} exp {expVisible} mask {mask}"); }
            if (cp.Size != expNPlanes) { sizeMis++; bad = true; if (examples.Count < 12) examples.Add($"{step} co={co} size={size}: nplanes ours {cp.Size} exp {expNPlanes}"); }
            if ((cp.O - expO).Length() > 1e-3f || (cp.R - expR).Length() > 1e-3f) { oMis++; bad = true; if (examples.Count < 12) examples.Add($"{step} co={co} size={size}: bbox ours {cp.O}/{cp.R} exp {expO}/{expR}"); }
            if (cp.Size == expNPlanes)
                for (int p = 0; p < cp.Size; p++)
                {
                    var e = expPlanes[p];
                    if (Mathf.Abs(cp.P[p].N.X - e.nx) > 1e-3f || Mathf.Abs(cp.P[p].N.Y - e.ny) > 1e-3f ||
                        Mathf.Abs(cp.P[p].N.Z - e.nz) > 1e-3f || Mathf.Abs(cp.P[p].Offset - e.off) > 1e-3f)
                    { planeMis++; bad = true; if (examples.Count < 12) examples.Add($"{step} co={co} size={size}: plane {p} ours {cp.P[p].N}/{cp.P[p].Offset} exp {e.nx},{e.ny},{e.nz}/{e.off}"); }
                }
            _ = bad;
        }

        GD.Print($"[clips] cubes={cubes}  visible-mismatch={visMis} nplanes-mismatch={sizeMis} bbox-mismatch={oMis} plane-mismatch={planeMis}");
        foreach (var e in examples) GD.Print($"[clips] {e}");
    }

    [Test]
    public void DebugFirstCube()
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath("res://"), "tools/refharness/traces/session4.csv.clips");
        if (!File.Exists(path)) return;
        var octa = TestData.ReadMap("church51").ToOcta();
        var lines = File.ReadAllLines(path);
        var c1 = lines[0].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var co = new Vector3I(int.Parse(c1[3]), int.Parse(c1[4]), int.Parse(c1[5]));
        int size = int.Parse(c1[6]);
        string edgesHex = c1[15];
        var c = new Cube { Material = Octa.MAT_AIR };
        var eb = new byte[12];
        for (int k = 0; k < 12; k++) eb[k] = byte.Parse(edgesHex.Substring(k * 2, 2), NumberStyles.HexNumber);
        for (int d = 0; d < 3; d++)
            c.Faces[d] = (uint)eb[d * 4] | ((uint)eb[d * 4 + 1] << 8) | ((uint)eb[d * 4 + 2] << 16) | ((uint)eb[d * 4 + 3] << 24);

        for (int f = 0; f < 6; f++)
        {
            var o = octa.NeighbourCube(c, f, co, size, out var no, out int nsize);
            int opp = f ^ 1;
            GD.Print($"[dbg] face{f}: collapsed={OctaVisible.CollapsedFace(c, f)} touching={c.TouchingFace(f)} " +
                     $"neighbour isSelf={ReferenceEquals(o, c)} nsize={nsize} children={o.Children != null} " +
                     $"entirelySolid={o.IsEntirelySolid()} empty={o.IsEmpty()} " +
                     $"o.touch={o.TouchingFace(opp)} o.faceEdges={OctaVisible.FaceEdges(o, opp):X8} F_SOLID={Octa.F_SOLID:X8} " +
                     $"classify={OctaVisible.ClassifyFace(octa, c, f, co, size)}");
            if (f == 3)
            {
                var vo = new Vector3I(co.X & 0xFFF, co.Y & 0xFFF, co.Z & 0xFFF);
                var no2 = new Vector3I(no.X & 0xFFF, no.Y & 0xFFF, no.Z & 0xFFF);
                var cf = new Vector2I[4]; var of = new Vector2I[4];
                int numc = OctaVisible.GenFaceVecs(c, 3, vo, size, false, cf);
                int numo = OctaVisible.GenFaceVecs(o, 2, no2, nsize, false, of);
                GD.Print($"[dbg]   cf({numc})={string.Join(" ", cf.Take(numc))} of({numo})={string.Join(" ", of.Take(numo))} inside={OctaVisible.InsideFace(cf, numc, of, numo)}");
            }
        }
    }

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
}
