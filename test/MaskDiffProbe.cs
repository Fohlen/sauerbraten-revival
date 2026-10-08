using System.Globalization;
using System.IO;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>Temporary diagnostic for the ClassifyFace divergence.</summary>
public class MaskDiffProbe : GoTestBase
{
    public MaskDiffProbe(Node testScene) : base(testScene) { }

    [Test]
    public void Probe()
    {
        var octa = TestData.ReadMap("church51").ToOcta();

        // Cube from the first A3ClipsProbe example: co=(3192,1040,3032) size=8,
        // edges=808080808080808040404040 mat=0. Engine classifyface(f3)=0; ours=3.
        var co = new Vector3I(3192, 1040, 3032);
        int size = 8;
        var c = CubeFromHex("808080808080808040404040");
        c.Material = 0;

        int orient = 3; // FRONT (+Y)
        TraceClassify(octa, c, orient, co, size);
    }

    private static void TraceClassify(Octa octa, Cube c, int orient, Vector3I co, int size)
    {
        GD.Print($"[maskprobe] classifyface orient={orient} co={co} size={size} mat={c.Material} edges={Hex(c)}");
        GD.Print($"[maskprobe] collapsedface={OctaVisible.CollapsedFace(c, orient)} touchingface={c.TouchingFace(orient)}");

        int vismask = (c.Material & Phys.MatFClip) == Phys.MatNoClip ? 1 : 3;
        if (!c.TouchingFace(orient)) { GD.Print($"[maskprobe] !touching -> {vismask}"); return; }

        var o = octa.NeighbourCube(c, orient, co, size, out var no, out int nsize);
        GD.Print($"[maskprobe] neighbour: same={o == c} nsize={nsize} no={no} edges={Hex(o)} children={o.Children != null} mat={o.Material}");
        if (o == c) { GD.Print("[maskprobe] o==c -> 0"); return; }

        int opp = orient ^ 1;
        GD.Print($"[maskprobe] opp={opp}");

        var vo = new Vector3I(co.X & 0xFFF, co.Y & 0xFFF, co.Z & 0xFFF);
        var nom = new Vector3I(no.X & 0xFFF, no.Y & 0xFFF, no.Z & 0xFFF);
        GD.Print($"[maskprobe] vo={vo} nom={nom}");

        if (nsize > size || (nsize == size && o.Children == null))
        {
            GD.Print($"[maskprobe] leaf branch");
            int vis = 0;
            if (((~c.Material & o.Material) & Phys.MatAlpha) != 0) { vis |= 1; GD.Print("[maskprobe] alpha diff -> vis|=1"); }
            if ((o.Material & Phys.MatFClip) == Phys.MatNoClip) { vis |= vismask & 2; GD.Print("[maskprobe] noclip -> vis|=2"); }
            GD.Print($"[maskprobe] vis={vis} vismask={vismask} o.solid={o.IsEntirelySolid()} o.empty={o.IsEmpty()} o.notouch={OctaVisible.NotTouchingFace(o, opp)} o.touch={o.TouchingFace(opp)} o.faceedges={OctaVisible.FaceEdges(o, opp):X8} F_SOLID={Octa.F_SOLID:X8}");

            var cf = new Vector2I[4]; var of = new Vector2I[4];
            int numc = OctaVisible.GenFaceVecs(c, orient, vo, size, false, cf);
            int numo = OctaVisible.GenFaceVecs(o, opp, nom, nsize, false, of);
            GD.Print($"[maskprobe] numc={numc} cf=[{string.Join(",", cf.Take(numc))}]");
            GD.Print($"[maskprobe] numo={numo} of=[{string.Join(",", of.Take(numo))}]");
            bool inside = numo >= 3 && OctaVisible.InsideFace(cf, numc, of, numo);
            int result = (numo < 3 || !inside) ? vismask : 0;
            GD.Print($"[maskprobe] insideface(cf in of)={OctaVisible.InsideFace(cf, numc, of, numo)} -> result={result}");
            return;
        }

        GD.Print("[maskprobe] node branch (recursive occludesface)");
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

    private static string Hex(Cube c)
    {
        var sb = new System.Text.StringBuilder();
        for (int d = 0; d < 3; d++)
            for (int k = 0; k < 4; k++)
                sb.Append(((c.Faces[d] >> (k * 8)) & 0xFF).ToString("x2"));
        return sb.ToString();
    }
}
