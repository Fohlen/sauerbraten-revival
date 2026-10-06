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
/// Diffs our genclipplanes/classifyface/collideface/neighbourcube against the
/// engine's SAUER_TRACE.clips dump. Temporary diagnostic.
/// </summary>
public class A3ClipsProbe : TestClass
{
    public A3ClipsProbe(Node testScene) : base(testScene) { }

    [Test]
    public void DiffClipPlanes()
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath("res://"), "tools/refharness/traces/session6.csv.clips");
        if (!File.Exists(path)) { GD.Print("[clips] missing; skipping"); return; }

        var octa = TestData.ReadMap("church51").ToOcta();
        var lines = File.ReadAllLines(path);
        int cubes = 0, visMis = 0, faceMis = 0, nsEdgesMis = 0, nsizeMis = 0, nplanesMis = 0, bboxMis = 0;
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
            var expO = new Vector3(F(c1[9]), F(c1[10]), F(c1[11]));
            var expR = new Vector3(F(c1[12]), F(c1[13]), F(c1[14]));
            var c = CubeFromHex(c1[15]);
            c.Material = (ushort)int.Parse(c1[16]);
            i++;

            var expFaces = new int[6]; var expCollide = new int[6]; var expNsize = new int[6];
            var expNbEdges = new string[6];
            for (int f = 0; f < 6; f++)
            {
                // F <orient> <classifyface> <collideface> <nsize> <isSelf> <ro> <nb edges hex>
                var f1 = lines[i++].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                int o = int.Parse(f1[1]);
                expFaces[o] = int.Parse(f1[2]);
                expCollide[o] = int.Parse(f1[3]);
                expNsize[o] = int.Parse(f1[4]);
                expNbEdges[o] = f1[9];
            }
            // The collidemask genclipplanes() actually used = p.visible (flat faces) | plane sides.
            int expMask = expVisible;
            for (int s = 0; s < expNPlanes; s++)
            {
                var p1 = lines[i++].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                expMask |= 1 << int.Parse(p1[6]);
            }

            cubes++;
            int ourMask = OctaVisible.CollisionVisibleMask(octa, c, co, size);
            var cp = OctaClipping.GetClipPlanes(octa, c, co, size, ourMask);
            if (ourMask != expMask) { visMis++; if (examples.Count < 12) examples.Add($"{step} co={co} size={size} mat={c.Material}: mask ours {ourMask} exp {expMask} | cEdges={c1[15]}"); }
            for (int f = 0; f < 6; f++)
            {
                var nb = octa.NeighbourCube(c, f, co, size, out _, out int nsize);
                if (nsize != expNsize[f] || HexOf(nb) != expNbEdges[f])
                {
                    nsEdgesMis++;
                    if (examples.Count < 12) examples.Add($"{step} co={co} f{f}: nsize ours {nsize} exp {expNsize[f]} nbEdges ours {HexOf(nb)} exp {expNbEdges[f]}");
                }
            }
            if (cp.Size != expNPlanes) nplanesMis++;
            if ((cp.O - expO).Length() > 1e-3f || (cp.R - expR).Length() > 1e-3f) bboxMis++;
        }

        GD.Print($"[clips] cubes={cubes}  MASK={visMis}  nsize/nbEdges={nsEdgesMis}  nplanes={nplanesMis} bbox={bboxMis}");
        foreach (var e in examples) GD.Print($"[clips] {e}");
    }

    private void UnusedOld()
    {
        var expFaces = new int[6]; var expCollide = new int[6]; var expNsize = new int[6];
        var expNbEdges = new string[6];
        for (int f = 0; f < 6; f++)
        {
            expFaces[f] = expCollide[f] = expNsize[f] = 0; expNbEdges[f] = "";
        }
    }

    private static float F(string s) => float.Parse(s, C);

    private static Cube CubeFromHex(string hex)
    {
        var c = new Cube();
        var eb = new byte[12];
        for (int k = 0; k < 12; k++) eb[k] = byte.Parse(hex.Substring(k * 2, 2), NumberStyles.HexNumber);
        for (int d = 0; d < 3; d++)
            c.Faces[d] = (uint)eb[d * 4] | ((uint)eb[d * 4 + 1] << 8) | ((uint)eb[d * 4 + 2] << 16) | ((uint)eb[d * 4 + 3] << 24);
        return c;
    }

    private static string HexOf(Cube c)
    {
        var sb = new System.Text.StringBuilder();
        for (int d = 0; d < 3; d++)
            for (int k = 0; k < 4; k++)
                sb.Append(((c.Faces[d] >> (k * 8)) & 0xFF).ToString("x2"));
        return sb.ToString();
    }

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    [Test]
    public void DebugOne()
    {
        var octa = TestData.ReadMap("church51").ToOcta();
        var c = CubeFromHex("808080808080808040404040");
        c.Material = 0;
        var co = new Vector3I(2888, 1392, 3064);
        int size = 8;
        int f = 3, opp = 2;
        var vo = new Vector3I(co.X & 0xFFF, co.Y & 0xFFF, co.Z & 0xFFF);
        var nb = octa.NeighbourCube(c, f, co, size, out var no, out int nsize);
        var no2 = new Vector3I(no.X & 0xFFF, no.Y & 0xFFF, no.Z & 0xFFF);
        GD.Print($"[dbg2] c.collapsed({f})={OctaVisible.CollapsedFace(c, f)} c.touch({f})={c.TouchingFace(f)} " +
                 $"nsize={nsize} nb.children={nb.Children != null} nb.empty={nb.IsEmpty()} nb.solid={nb.IsEntirelySolid()} " +
                 $"nb.touch({opp})={nb.TouchingFace(opp)} nb.faceEdges={OctaVisible.FaceEdges(nb, opp):X8} " +
                 $"nb.notTouch={OctaVisible.NotTouchingFace(nb, opp)} classify={OctaVisible.ClassifyFace(octa, c, f, co, size)}");
        var cf = new Vector2I[4]; var of = new Vector2I[4];
        int numc = OctaVisible.GenFaceVecs(c, f, vo, size, false, cf);
        int numo = OctaVisible.GenFaceVecs(nb, opp, no2, nsize, false, of);
        GD.Print($"[dbg2] cf({numc})={string.Join(" ", cf.Take(numc))}");
        GD.Print($"[dbg2] of({numo})={string.Join(" ", of.Take(numo))} inside={OctaVisible.InsideFace(cf, numc, of, numo)}");
    }
}
