using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>Temporary diagnostic for the session3 step-262 divergence.</summary>
public class Step262Probe : TestClass
{
    public Step262Probe(Node testScene) : base(testScene) { }

    [Test]
    public void Probe()
    {
        var octa = TestData.ReadMap("church51").ToOcta();

        // Player AABB at the start of step 262 (row 261 state).
        var o = new Vector3(2916.37061f, 1264.32715f, 2958.00098f);
        float radius = 4.1f, eye = 14f, above = 1f;
        var bo = new Vector3I((int)(o.X - radius), (int)(o.Y - radius), (int)(o.Z - eye));
        var bs = new Vector3I((int)(o.X + radius), (int)(o.Y + radius), (int)(o.Z + above)) + Vector3I.One;

        GD.Print($"[probe] AABB bo={bo} bs={bs}");

        var found = new List<string>();
        Walk(octa, octa.WorldRoot, Vector3I.Zero, octa.WorldSize >> 1, bo, bs, found);

        GD.Print($"[probe] {found.Count} leaves in AABB:");
        foreach (var s in found) GD.Print("[probe]   " + s);
    }

    private static void Walk(Octa octa, Cube[] c, Vector3I cor, int size, Vector3I bo, Vector3I bs, List<string> out_)
    {
        int possible = OctaBoxOverlap(cor, size, bo, bs);
        for (int i = 0; i < 8; i++)
        {
            if ((possible & (1 << i)) == 0) continue;
            var co = new Vector3I(cor.X + ((i & 1) >> 0) * size, cor.Y + ((i & 2) >> 1) * size, cor.Z + ((i & 4) >> 2) * size);
            var cube = c[i];
            if (cube.Children != null)
            {
                Walk(octa, cube.Children, co, size >> 1, bo, bs, out_);
                continue;
            }
            if (cube.IsEmpty()) continue;

            int mask = OctaVisible.CollisionVisibleMask(octa, cube, co, size);
            var cp = OctaClipping.GetClipPlanes(octa, cube, co, size, mask);
            var cf = new List<string>();
            for (int f = 0; f < 6; f++)
            {
                int cl = OctaVisible.ClassifyFace(octa, cube, f, co, size);
                if (cl != 0) cf.Add($"f{f}:cf={cl},col={OctaVisible.CollideFace(cube, f)}");
            }
            out_.Add($"co={co} size={size} mat={cube.Material} edges={Hex(cube)} mask=0x{mask:X2} nplanes={cp.Size} pvisible=0x{cp.Visible:X2} [{string.Join(" ", cf)}]");
        }
    }

    private static int OctaBoxOverlap(Vector3I o, int size, Vector3I bbmin, Vector3I bbmax)
    {
        int p = 0xFF;
        var mid = new Vector3I(o.X + size, o.Y + size, o.Z + size);
        if (mid.Z <= bbmin.Z) p &= 0xF0; else if (mid.Z >= bbmax.Z) p &= 0x0F;
        if (mid.Y <= bbmin.Y) p &= 0xCC; else if (mid.Y >= bbmax.Y) p &= 0x33;
        if (mid.X <= bbmin.X) p &= 0xAA; else if (mid.X >= bbmax.X) p &= 0x55;
        return p;
    }

    private static string Hex(Cube c)
    {
        var sb = new System.Text.StringBuilder();
        for (int d = 0; d < 3; d++)
            for (int k = 0; k < 4; k++)
                sb.Append(((c.Faces[d] >> (k * 8)) & 0xFF).ToString("x2"));
        return sb.ToString();
    }
}
