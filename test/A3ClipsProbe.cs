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
/// Diffs our collision visibility mask / classifyface / neighbourcube / genclipplanes
/// against the engine's SAUER_TRACE.clips dump. Temporary diagnostic.
/// </summary>
public class A3ClipsProbe : GoTestBase
{
    public A3ClipsProbe(Node testScene) : base(testScene) { }

    [Test]
    public void DiffClipPlanes()
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath("res://"), "tools/refharness/traces/session7.csv.clips");
        if (!File.Exists(path)) { GD.Print("[clips] missing; skipping"); return; }

        var octa = TestData.ReadMap("church51").ToOcta();
        var lines = File.ReadAllLines(path);
        int cubes = 0, maskMis = 0, childMis = 0, nsizeMis = 0, edgesMis = 0, nplanesMis = 0, bboxMis = 0;
        var examples = new List<string>();

        for (int i = 0; i < lines.Length;)
        {
            if (lines[i].Length == 0 || lines[i][0] != 'C') { i++; continue; }
            var c1 = lines[i].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            int step = int.Parse(c1[1]);
            var co = new Vector3I(int.Parse(c1[3]), int.Parse(c1[4]), int.Parse(c1[5]));
            int size = int.Parse(c1[6]);
            int expNPlanes = int.Parse(c1[8]);
            var expO = new Vector3(F(c1[9]), F(c1[10]), F(c1[11]));
            var expR = new Vector3(F(c1[12]), F(c1[13]), F(c1[14]));
            var c = CubeFromHex(c1[15]);
            c.Material = (ushort)int.Parse(c1[16]);
            int expCVisible = int.Parse(c1[17]);
            i++;

            var expChildren = new int[6]; var expNsize = new int[6]; var expNbEdges = new string[6];
            for (int f = 0; f < 6; f++)
            {
                var f1 = lines[i++].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                int o = int.Parse(f1[1]);
                expNsize[o] = int.Parse(f1[4]);
                expNbEdges[o] = f1[9];
                expChildren[o] = int.Parse(f1[10]);
            }
            i += expNPlanes;

            cubes++;
            int ourMask = OctaVisible.CollisionVisibleMask(octa, c, co, size);
            var cp = OctaClipping.GetClipPlanes(octa, c, co, size, ourMask);
            if (ourMask != (expCVisible & 0x3F))
            {
                maskMis++;
                if (examples.Count < 12) examples.Add($"{step} co={co} size={size} mat={c.Material}: mask ours {ourMask} exp {expCVisible & 0x3F} (c.visible {expCVisible}) cEdges={c1[15]}");
            }
            for (int f = 0; f < 6; f++)
            {
                var nb = octa.NeighbourCube(c, f, co, size, out _, out int nsize);
                int oc = nb.Children != null ? 1 : 0;
                if (nsize != expNsize[f]) nsizeMis++;
                if (HexOf(nb) != expNbEdges[f]) edgesMis++;
                if (oc != expChildren[f])
                {
                    childMis++;
                    if (examples.Count < 12) examples.Add($"{step} co={co} f{f}: children ours {oc} exp {expChildren[f]} nsize ours {nsize} exp {expNsize[f]}");
                }
            }
            if (cp.Size != expNPlanes) nplanesMis++;
            if ((cp.O - expO).Length() > 1e-3f || (cp.R - expR).Length() > 1e-3f) bboxMis++;
        }

        GD.Print($"[clips] cubes={cubes}  MASK={maskMis}  children={childMis}  nsize={nsizeMis}  nbEdges={edgesMis}  nplanes={nplanesMis}  bbox={bboxMis}");
        foreach (var e in examples) GD.Print($"[clips] {e}");
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
}
