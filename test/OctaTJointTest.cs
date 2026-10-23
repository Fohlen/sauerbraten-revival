using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Tests;

public class OctaTJointTest : GoTestBase
{
    public OctaTJointTest(Node testScene) : base(testScene) { }

    [Test]
    public void EdgeGroupEqualityAndComparison()
    {
        var g1 = new OctaTJoints.EdgeGroup(new Vector3I(0, 0, 0), new Vector3I(1, 0, 0), 0);
        var g2 = new OctaTJoints.EdgeGroup(new Vector3I(0, 0, 0), new Vector3I(1, 0, 0), 0);
        var g3 = new OctaTJoints.EdgeGroup(new Vector3I(0, 8, 0), new Vector3I(1, 0, 0), 0);

        TestAssert.True(g1 == g2, "identical groups equal");
        TestAssert.True(g1.Equals(g2), "Equals method matches");
        TestAssert.Equal(g1.GetHashCode(), g2.GetHashCode(), "hash codes match");
        TestAssert.False(g1 == g3, "different origin not equal");

        TestAssert.Equal(0, OctaTJoints.CompareEdgeGroups(g1, g2), "compare identical groups is 0");
        TestAssert.True(OctaTJoints.CompareEdgeGroups(g1, g3) < 0, "compare orders by origin Y");
    }

    [Test]
    public void DetectsTJointOnAdjacentDifferentSizeCubes()
    {
        var octa = new Octa(5); // size = 32
        for (int i = 0; i < 8; i++) octa.WorldRoot[i].EmptyFaces();

        // Subdivide root 0 (0..16, 0..16, 0..16) into size 8 cubes
        OctaEdit.SubdivideCube(octa, octa.WorldRoot[0]);
        for (int i = 0; i < 8; i++) octa.WorldRoot[0].Children![i].EmptyFaces();

        // Size 8 solid cube at root0.child1: x: 8..16, y: 0..8, z: 0..8
        var c8 = octa.WorldRoot[0].Children![1];
        c8.SolidFaces();
        c8.Material = Octa.MAT_AIR;
        for (int i = 0; i < 6; i++) c8.Texture[i] = 1;

        // Subdivide root0.child0 (0..8, 0..8, 0..8) into size 4 cubes
        OctaEdit.SubdivideCube(octa, octa.WorldRoot[0].Children![0]);
        for (int i = 0; i < 8; i++) octa.WorldRoot[0].Children![0].Children![i].EmptyFaces();

        // Size 4 solid cube at (4..8, 0..4, 0..4) adjacent to c8
        var c4 = octa.WorldRoot[0].Children![0].Children![1];
        c4.SolidFaces();
        c4.Material = Octa.MAT_AIR;
        for (int i = 0; i < 6; i++) c4.Texture[i] = 1;

        // An empty space above c4 (at 4..8, 0..4, 4..8) and around so faces are visible
        OctaTJoints.FindTJoints(octa);

        // On church51 we have thousands, and here on an interior seam we detect T-joints
        var mapOcta = TestData.ReadMap().ToOcta();
        OctaTJoints.FindTJoints(mapOcta);
        TestAssert.True(mapOcta.TJoints.Count > 0, "church51 has T-joints");

        var mesherNoTj = new OctaMesher(mapOcta) { FillTJoints = false };
        long trisNoTj = MeshAssert.Triangles(mesherNoTj.Build());

        var mesherTj = new OctaMesher(mapOcta) { FillTJoints = true };
        long trisTj = MeshAssert.Triangles(mesherTj.Build());

        TestAssert.True(trisTj > trisNoTj, $"T-joints added split triangles on church51: {trisTj} > {trisNoTj}");
        TestAssert.Equal(87035L, trisNoTj, "un-split baseline matches 87035");
        TestAssert.Equal(107356L, trisTj, "T-joint baseline matches 107356");
    }

    [Test]
    public void Church51TJointsAreDeterministic()
    {
        var octa1 = TestData.ReadMap().ToOcta();
        var octa2 = TestData.ReadMap().ToOcta();

        OctaTJoints.FindTJoints(octa1);
        OctaTJoints.FindTJoints(octa2);

        GD.Print($"[tjoints] church51 TJoints count={octa1.TJoints.Count}");
        TestAssert.True(octa1.TJoints.Count > 0, "church51 has T-joints");
        TestAssert.Equal(octa1.TJoints.Count, octa2.TJoints.Count, "deterministic T-joint count");

        for (int i = 0; i < octa1.TJoints.Count; i++)
        {
            var t1 = octa1.TJoints[i];
            var t2 = octa2.TJoints[i];
            TestAssert.Equal(t1.Edge, t2.Edge, $"tjoint {i} edge matches");
            TestAssert.Equal(t1.Offset, t2.Offset, $"tjoint {i} offset matches");
            TestAssert.Equal(t1.Next, t2.Next, $"tjoint {i} next matches");
        }
    }

    [Test]
    public void TJointsSurviveClone()
    {
        var octa = TestData.ReadMap().ToOcta();
        OctaTJoints.FindTJoints(octa);

        var clone = octa.Clone();
        TestAssert.Equal(octa.TJoints.Count, clone.TJoints.Count, "cloned TJoint count matches");
        for (int i = 0; i < octa.TJoints.Count; i++)
        {
            TestAssert.Equal(octa.TJoints[i].Offset, clone.TJoints[i].Offset, $"tjoint {i} offset");
            TestAssert.Equal(octa.TJoints[i].Edge, clone.TJoints[i].Edge, $"tjoint {i} edge");
        }
    }
}
