using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>Temporary diagnostic for the remaining genclipplanes nplanes diff.</summary>
public class NPlanesProbe : GoTestBase
{
    public NPlanesProbe(Node testScene) : base(testScene) { }

    [Test]
    public void Probe()
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath("res://"), "tools/refharness/traces/session7.csv.clips");
        if (!File.Exists(path)) { GD.Print("[nplanes] missing clips"); return; }

        var octa = TestData.ReadMap("church51").ToOcta();
        var lines = File.ReadAllLines(path);
        int cubes = 0, shown = 0;

        for (int i = 0; i < lines.Length;)
        {
            if (lines[i].Length == 0 || lines[i][0] != 'C') { i++; continue; }
            var c1 = lines[i].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            int step = int.Parse(c1[1]);
            var co = new Vector3I(int.Parse(c1[3]), int.Parse(c1[4]), int.Parse(c1[5]));
            int size = int.Parse(c1[6]);
            int expPVisible = int.Parse(c1[7]);
            int expNPlanes = int.Parse(c1[8]);
            var c = CubeFromHex(c1[15]);
            c.Material = (ushort)int.Parse(c1[16]);
            i++;

            var expClassify = new int[6]; var expCollide = new int[6];
            for (int f = 0; f < 6; f++)
            {
                var f1 = lines[i++].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                int o = int.Parse(f1[1]);
                expClassify[o] = int.Parse(f1[2]);
                expCollide[o] = int.Parse(f1[3]);
            }
            i += expNPlanes;

            cubes++;
            int ourMask = OctaVisible.CollisionVisibleMask(octa, c, co, size);
            var cp = OctaClipping.GetClipPlanes(octa, c, co, size, ourMask);
            if (cp.Size == expNPlanes) continue;

            if (shown++ >= 12) continue;
            var faceDiffs = new List<string>();
            for (int f = 0; f < 6; f++)
            {
                int ourCl = OctaVisible.ClassifyFace(octa, c, f, co, size);
                if (ourCl != expClassify[f])
                    faceDiffs.Add($"f{f}: ours cf={ourCl} exp cf={expClassify[f]}");
                int ourVt = OctaVisible.VisibleTris(octa, c, f, co, size, Phys.MatNoClip, Phys.MatFClip);
                // engine visibletris isn't dumped directly; skip
            }
            GD.Print($"[nplanes] step={step} co={co} size={size} mat={c.Material} edges={c1[15]} ourMask=0x{ourMask:X2} expMask=0x{(c1[17].Length>0?int.Parse(c1[17])&0x3F:0):X2} ourNPlanes={cp.Size} expNPlanes={expNPlanes} ourPVis=0x{cp.Visible:X2} expPVis=0x{expPVisible:X2} [{string.Join(" ", faceDiffs)}]");
        }
        GD.Print($"[nplanes] {cubes} cubes, {shown} shown");
    }

    private static Cube CubeFromHex(string hex)
    {
        var c = new Cube();
        var eb = new byte[12];
        for (int k = 0; k < 12; k++) eb[k] = byte.Parse(hex.Substring(k * 2, 2), NumberStyles.HexNumber);
        for (int d = 0; d < 3; d++)
            c.Faces[d] = (uint)eb[d * 4] | ((uint)eb[d * 4 + 1] << 8) | ((uint)eb[d * 4 + 2] << 16) | ((uint)eb[d * 4 + 3] << 24);
        return c;
    }
}
