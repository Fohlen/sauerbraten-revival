using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Meshing;
using SauerbratenRevival.World.Physics;
using System.Collections.Generic;

namespace SauerbratenRevival.Tests;

public class OctaMergeTest : GoTestBase
{
    public OctaMergeTest(Node testScene) : base(testScene) { }

    [Test]
    public void ReduceSlopeSimplifiesNormals()
    {
        var n = new Vector3I(0, 0, 8);
        OctaMerges.ReduceSlope(ref n);
        TestAssert.Equal(new Vector3I(0, 0, 1), n, "axis normal reduced to unit");

        n = new Vector3I(4, 4, 0);
        OctaMerges.ReduceSlope(ref n);
        TestAssert.Equal(new Vector3I(1, 1, 0), n, "diagonal reduced");

        n = new Vector3I(8, 16, 24);
        OctaMerges.ReduceSlope(ref n);
        TestAssert.Equal(new Vector3I(1, 2, 3), n, "proportional reduced");
    }

    [Test]
    public void MergePolysCombinesTwoAdjacentQuads()
    {
        var p = new OctaMerges.Poly { C = new Cube(), NumVerts = 4 };
        p.Verts[0] = new OctaMerges.PVert(0, 0);
        p.Verts[1] = new OctaMerges.PVert(0, 8);
        p.Verts[2] = new OctaMerges.PVert(8, 8);
        p.Verts[3] = new OctaMerges.PVert(8, 0);

        var q = new OctaMerges.Poly { C = new Cube(), NumVerts = 4 };
        q.Verts[0] = new OctaMerges.PVert(0, 8);
        q.Verts[1] = new OctaMerges.PVert(0, 16);
        q.Verts[2] = new OctaMerges.PVert(8, 16);
        q.Verts[3] = new OctaMerges.PVert(8, 8);

        var links = new Dictionary<OctaMerges.PEdge, OctaMerges.PLink>();
        var queue = new List<OctaMerges.PLink>();
        var sharedEdge = new OctaMerges.PEdge(new OctaMerges.PVert(0, 8), new OctaMerges.PVert(8, 8));

        bool merged = OctaMerges.MergePolys(5, links, queue, 0, p, q, sharedEdge);
        TestAssert.True(merged, "polys merged successfully");
        TestAssert.True(p.Merged, "p marked merged");
        TestAssert.True(q.Merged, "q marked merged");
        TestAssert.Equal(0, q.NumVerts, "q absorbed");
        TestAssert.Equal(4, p.NumVerts, "collinear vertices removed leaving a 4-vertex rectangle");
    }

    [Test]
    public void CalcMergesMergesAdjacentSolidCubes()
    {
        var octa = new Octa(4); // worldscale = 4 -> worldsize = 16
        // Subdivide root so we have size=4 cubes
        OctaEdit.SubdivideCube(octa, octa.WorldRoot[0]);
        // Set cubes 0 and 1 solid with the same texture, and cubes 4 and 5 (above them) empty (air)
        octa.WorldRoot[0].Children![0].SolidFaces();
        octa.WorldRoot[0].Children![0].Material = Octa.MAT_AIR;
        for (int i = 0; i < 6; i++) octa.WorldRoot[0].Children![0].Texture[i] = 1;

        octa.WorldRoot[0].Children![1].SolidFaces();
        octa.WorldRoot[0].Children![1].Material = Octa.MAT_AIR;
        for (int i = 0; i < 6; i++) octa.WorldRoot[0].Children![1].Texture[i] = 1;

        octa.WorldRoot[0].Children![4].SetFaces(Octa.F_EMPTY);
        octa.WorldRoot[0].Children![5].SetFaces(Octa.F_EMPTY);

        OctaMerges.CalcMerges(octa);

        Cube c0 = octa.WorldRoot[0].Children![0];
        Cube c1 = octa.WorldRoot[0].Children![1];

        // Top face (orient 5, +Z) should be merged across the two cubes:
        bool c0MergedTop = (c0.Merged & (1 << 5)) != 0;
        bool c1MergedTop = (c1.Merged & (1 << 5)) != 0;
        TestAssert.True(c0MergedTop && c1MergedTop, "both cubes have merged bit set for top face");

        int c0Verts = c0.Ext != null ? (c0.Ext.Surfaces[5].NumVerts & Octa.MAXFACEVERTS) : 0;
        int c1Verts = c1.Ext != null ? (c1.Ext.Surfaces[5].NumVerts & Octa.MAXFACEVERTS) : 0;

        TestAssert.True((c0Verts > 0 && c1Verts == 0) || (c1Verts > 0 && c0Verts == 0),
            "exactly one cube is the origin holding the merged polygon verts, the other has 0 verts");
    }

    [Test]
    public void InvalidateMergesClearsMergedBits()
    {
        var c = new Cube();
        c.Merged = 0x3F;
        c.Ext = new CubeExt();
        c.Ext.TJoints = 5;

        OctaMerges.InvalidateMerges(c);
        TestAssert.Equal(0, c.Merged, "merged cleared");
        TestAssert.Equal(-1, c.Ext.TJoints, "tjoints reset");
    }

    [Test]
    public void CalcMergesIsDeterministic()
    {
        var octa1 = TestData.ReadMap().ToOcta();
        var octa2 = TestData.ReadMap().ToOcta();

        OctaMerges.CalcMerges(octa1);
        OctaMerges.CalcMerges(octa2);

        // Verify identical merged flags across all leaves
        void CheckCubes(Cube[] c1, Cube[] c2)
        {
            for (int i = 0; i < 8; i++)
            {
                TestAssert.Equal(c1[i].Merged, c2[i].Merged, $"cube {i} merged bits match");
                if (c1[i].Children != null && c2[i].Children != null)
                    CheckCubes(c1[i].Children!, c2[i].Children!);
            }
        }

        CheckCubes(octa1.WorldRoot, octa2.WorldRoot);
    }
}
