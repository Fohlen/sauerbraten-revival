using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers the M1 face/texture/material/flip/rotate ops in <see cref="OctaEdit"/>
/// (<c>edittexcube</c>, <c>replacetexcube</c>, <c>setmat</c>, <c>flipcube</c>,
/// <c>rotatecube</c>). Values marked "octaprobe" are the original engine's exact
/// output from <c>tools/octaprobe</c> (ops.cpp copies the engine functions
/// verbatim).
/// </summary>
public class OctaFaceTest : GoTestBase
{
    public OctaFaceTest(Node testScene) : base(testScene) { }

    [Test]
    public void SetFaceTextureAllFaces()
    {
        // octaprobe: edittex all tex0=42 tex5=42
        var c = new Cube();
        for (int l = 0; l < 6; l++) c.Texture[l] = 1;
        OctaEdit.SetFaceTexture(c, 42, orient: -1);
        TestAssert.Equal(42, c.Texture[0], "face 0 set");
        TestAssert.Equal(42, c.Texture[5], "face 5 set");
    }

    [Test]
    public void SetFaceTextureSingleFace()
    {
        // octaprobe: edittex orient0 tex0=7 tex1=1 (solid cube -> visibleorient == orient)
        var c = new Cube();
        c.SolidFaces();
        for (int l = 0; l < 6; l++) c.Texture[l] = 1;
        OctaEdit.SetFaceTexture(c, 7, orient: 0);
        TestAssert.Equal(7, c.Texture[0], "orient 0 face set");
        TestAssert.Equal(1, c.Texture[1], "other faces untouched");
    }

    [Test]
    public void SetFaceTextureRecursesChildren()
    {
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        Cube root = w.WorldRoot[0];
        OctaEdit.SetFaceTexture(root, 5, orient: -1);
        for (int i = 0; i < 8; i++)
            for (int l = 0; l < 6; l++)
                TestAssert.Equal(5, root.Children![i].Texture[l], $"child {i} face {l} set");
    }

    [Test]
    public void ReplaceTextureMatchesEngine()
    {
        // octaprobe: replace old5new9 tex2=9 tex0=1
        var c = new Cube();
        for (int l = 0; l < 6; l++) c.Texture[l] = (ushort)(l == 2 ? 5 : 1);
        OctaEdit.ReplaceTexture(c, oldtex: 5, newtex: 9);
        TestAssert.Equal(9, c.Texture[2], "old texture replaced");
        TestAssert.Equal(1, c.Texture[0], "other faces untouched");
    }

    [Test]
    public void ReplaceTextureOverSelection()
    {
        var w = new Octa(3);
        Cube target = w.WorldRoot[0];
        for (int l = 0; l < 6; l++) target.Texture[l] = 5;

        var sel = new Block3(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), 4, 0);
        OctaEdit.ReplaceTexture(w, sel, oldtex: 5, newtex: 9, insel: true);

        TestAssert.Equal(9, target.Texture[0], "selection texture replaced");
        // WorldRoot[1] keeps its default texture (DEFAULT_GEOM = 1), untouched.
        TestAssert.Equal(Octa.DEFAULT_GEOM, w.WorldRoot[1].Texture[0], "unselected cube unchanged");
    }

    [Test]
    public void SetMaterialWaterMatchesEngine()
    {
        // octaprobe: setmat water material=4 (MAT_WATER).
        var c = new Cube();
        c.SolidFaces();
        OctaEdit.SetMaterial(c, Octa.MAT_WATER, matmask: 0, filtermat: 0, filtermask: 0, filtergeom: 0);
        TestAssert.Equal(Octa.MAT_WATER, c.Material, "water applied");
    }

    [Test]
    public void SetMaterialAirClears()
    {
        var c = new Cube { Material = Octa.MAT_WATER };
        c.SolidFaces();
        OctaEdit.SetMaterial(c, Octa.MAT_AIR, matmask: 0, filtermat: 0, filtermask: 0, filtergeom: 0);
        TestAssert.Equal(Octa.MAT_AIR, c.Material, "air clears the material");
    }

    [Test]
    public void SetMaterialRespectsFilter()
    {
        var c = new Cube { Material = Octa.MAT_WATER };
        c.SolidFaces();
        // Filter for MAT_LAVA only: a water cube must be skipped.
        OctaEdit.SetMaterial(c, Octa.MAT_GLASS, matmask: 0,
            filtermat: Octa.MAT_LAVA, filtermask: Octa.MATF_VOLUME, filtergeom: 0);
        TestAssert.Equal(Octa.MAT_WATER, c.Material, "non-matching filter leaves material");

        OctaEdit.SetMaterial(c, Octa.MAT_GLASS, matmask: 0,
            filtermat: Octa.MAT_WATER, filtermask: Octa.MATF_VOLUME, filtergeom: 0);
        TestAssert.Equal(Octa.MAT_GLASS, c.Material, "matching filter replaces material");
    }

    [Test]
    public void EditMaterialAppliesToSelection()
    {
        var w = new Octa(3);
        var sel = new Block3(new Vector3I(0, 0, 0), new Vector3I(1, 1, 1), 4, 0);
        OctaEdit.EditMaterial(w, sel, Octa.MAT_WATER, filter: -1);
        TestAssert.Equal(Octa.MAT_WATER, w.WorldRoot[0].Material, "selected cube gets water");
    }

    [Test]
    public void FlipCubeMatchesEngine()
    {
        // octaprobe: flip d0 tex0=2 tex1=1 tex2=3 (textures 1..6 swapped on d*2/d*2+1).
        var c = new Cube();
        for (int l = 0; l < 6; l++) c.Texture[l] = (ushort)(l + 1);
        OctaEdit.FlipCube(c, d: 0);
        TestAssert.Equal(2, c.Texture[0], "tex0/tex1 swapped");
        TestAssert.Equal(1, c.Texture[1], "tex1/tex0 swapped");
        TestAssert.Equal(3, c.Texture[2], "tex2 untouched");
    }

    [Test]
    public void RotateCubeMatchesEngine()
    {
        // octaprobe: rotate d1 tex0=5 tex1=6 tex2=3 tex3=4.
        var c = new Cube();
        for (int l = 0; l < 6; l++) c.Texture[l] = (ushort)(l + 1);
        OctaEdit.RotateCube(c, d: 1);
        TestAssert.Equal(5, c.Texture[0], "rotated tex0");
        TestAssert.Equal(6, c.Texture[1], "rotated tex1");
        TestAssert.Equal(3, c.Texture[2], "rotated tex2");
        TestAssert.Equal(4, c.Texture[3], "rotated tex3");
    }

    [Test]
    public void FlipCubeFourTimesIsIdentity()
    {
        var c = new Cube();
        for (int l = 0; l < 6; l++) c.Texture[l] = (ushort)(l + 1);
        c.SolidFaces();
        uint before0 = c.Faces[0], before1 = c.Faces[1], before2 = c.Faces[2];
        for (int i = 0; i < 4; i++) OctaEdit.FlipCube(c, d: 1);
        for (int l = 0; l < 6; l++) TestAssert.Equal(l + 1, c.Texture[l], $"texture {l} restored");
        for (int f = 0; f < 3; f++) TestAssert.Equal(new[]{ before0, before1, before2 }[f], c.Faces[f], "faces restored");
    }

    [Test]
    public void RotateCubeFourTimesIsIdentity()
    {
        var c = new Cube();
        for (int l = 0; l < 6; l++) c.Texture[l] = (ushort)(l + 1);
        c.SolidFaces();
        uint before0 = c.Faces[0], before1 = c.Faces[1], before2 = c.Faces[2];
        for (int i = 0; i < 4; i++) OctaEdit.RotateCube(c, d: 2);
        for (int l = 0; l < 6; l++) TestAssert.Equal(l + 1, c.Texture[l], $"texture {l} restored");
        for (int f = 0; f < 3; f++) TestAssert.Equal(new[]{ before0, before1, before2 }[f], c.Faces[f], "faces restored");
    }

    [Test]
    public void FlipCubeRecursesChildren()
    {
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        Cube root = w.WorldRoot[0];
        for (int i = 0; i < 8; i++)
            for (int l = 0; l < 6; l++) root.Children![i].Texture[l] = (ushort)(i * 6 + l + 1);
        // Just verify it does not throw and children are permuted (not all identity).
        OctaEdit.FlipCube(root, d: 2);
        bool permuted = false;
        for (int i = 0; i < 8; i++)
            if (root.Children![i].Texture[0] != i * 6 + 1) permuted = true;
        TestAssert.True(permuted, "children permuted by flip");
    }

    [Test]
    public void FlipHelpersRoundTrip()
    {
        // rflip/cflip/dflip are involutions; mflip is not (byte rotation).
        uint f = Octa.F_SOLID;
        TestAssert.Equal(f, OctaEdit.RFlip(OctaEdit.RFlip(f)), "rflip is an involution");
        TestAssert.Equal(f, OctaEdit.CFlip(OctaEdit.CFlip(f)), "cflip is an involution");
        TestAssert.Equal(f, OctaEdit.DFlip(OctaEdit.DFlip(f)), "dflip is an involution");
        TestAssert.Equal(Octa.F_EMPTY, OctaEdit.DFlip(Octa.F_EMPTY), "dflip leaves empty unchanged");
    }

    [Test]
    public void SetCubeVectorRoundTrips()
    {
        // Guards the nibble-level vertex write: a whole-byte write would clobber
        // the adjacent vertex's nibble (this was a real latent bug).
        var c = new Cube();
        c.EmptyFaces();
        for (int i = 0; i < 8; i++)
            CubeGeometry.SetCubeVector(c, i, new Vector3I(i % 8, (i * 2) % 8, (i * 3) % 8));
        for (int i = 0; i < 8; i++)
        {
            var v = CubeGeometry.GetCubeVector(c, i);
            TestAssert.Equal(new Vector3I(i % 8, (i * 2) % 8, (i * 3) % 8), v, $"vertex {i} round-trips");
        }
    }

    [Test]
    public void ForceMipMatchesEngine()
    {
        // octaprobe: forcemip parent faces=84848446 84848484 84848484. A parent
        // whose only non-empty child (7) is a solid cube with edge[0]=4 is re-mipped
        // to a single cube carrying that child's outer vertex.
        var w = new Octa(4);
        var p = new Cube();
        p.EmptyFaces();
        for (int l = 0; l < 6; l++) p.Texture[l] = (ushort)l;
        p.Children = w.NewCubes(Octa.F_EMPTY);
        p.Children[7].SolidFaces();
        p.Children[7].SetCubeEdge(0, 0, 0, 4);

        OctaEdit.ForceMip(w, p, fixtex: false);

        TestAssert.Equal((uint)0x84848446, p.Faces[0], "faces[0] matches engine");
        TestAssert.Equal((uint)0x84848484, p.Faces[1], "faces[1] matches engine");
        TestAssert.Equal((uint)0x84848484, p.Faces[2], "faces[2] matches engine");
    }

    // ---- M4c: pushedge / linkedpush (mpeditface modes 0 and 2) ----

    private static string EdgeBytes(Cube c)
    {
        var sb = new System.Text.StringBuilder();
        for (int d = 0; d < 3; d++)
            for (int i = 0; i < 4; i++)
                sb.Append($" {((byte)((c.Faces[d] >> (i * 8)) & 0xFF)):x2}");
        return sb.ToString().TrimStart();
    }

    [Test]
    public void PushEdgeMatchesEngine()
    {
        // octaprobe: PUSH edge0 dir1 dc0 edges=81 80... (nudge one nibble).
        var c = new Cube();
        c.SolidFaces();
        OctaEdit.PushEdge(c, d: 0, x: 0, y: 0, dir: 1, dc: 0);
        TestAssert.Equal("81 80 80 80 80 80 80 80 80 80 80 80", EdgeBytes(c), "edge nibble nudged");
    }

    [Test]
    public void PushEdgeClampsAtBounds()
    {
        // Can't push past 8 (solid) outward, or below 0 inward.
        var c = new Cube();
        c.SolidFaces();
        OctaEdit.PushEdge(c, 0, 0, 0, dir: 1, dc: 0);   // low nibble 0 -> 1
        OctaEdit.PushEdge(c, 0, 0, 0, dir: 1, dc: 1);   // high nibble stays 8
        byte b = c.CubeEdge(0, 0, 0);
        TestAssert.Equal(1, Cube.EdgeGet(b, 0), "low nibble advanced");
        TestAssert.Equal(8, Cube.EdgeGet(b, 1), "high nibble clamped at 8");
    }

    [Test]
    public void LinkedPushMatchesEngine()
    {
        // octaprobe: LINK d0 x0y0 dir-1: 43 80 80... (0x44 pulled to 0x43).
        var c = new Cube();
        c.SolidFaces();
        c.SetCubeEdge(0, 0, 0, 0x44);
        OctaEdit.LinkedPush(c, d: 0, x: 0, y: 0, dc: 0, dir: -1);
        TestAssert.Equal("43 80 80 80 80 80 80 80 80 80 80 80", EdgeBytes(c), "linked push moved the shared vertex");
    }

    [Test]
    public void LinkedPushDoesNotAffectOtherVertex()
    {
        // octaprobe: LINK d0 x1y1 dir-1 leaves the deformed corner alone -> 44 80...
        var c = new Cube();
        c.SolidFaces();
        c.SetCubeEdge(0, 0, 0, 0x44);
        OctaEdit.LinkedPush(c, d: 0, x: 1, y: 1, dc: 0, dir: -1);
        TestAssert.Equal("44 80 80 80 80 80 80 80 80 80 80 80", EdgeBytes(c), "unrelated corner untouched");
    }

    [Test]
    public void BoundedClamps()
    {
        TestAssert.Equal(0, OctaEdit.Bounded(-5), "clamps low");
        TestAssert.Equal(8, OctaEdit.Bounded(99), "clamps high");
        TestAssert.Equal(3, OctaEdit.Bounded(3), "passes through");
    }

    [Test]
    public void EditFaceModeOneMatchesFill()
    {
        // EditFace(mode 1) is the old Fill path; confirm the wrapper changes the world.
        var w = new Octa(3);
        OctaEdit.SubdivideCube(w, w.WorldRoot[0]);
        var lower = w.WorldRoot[0].Children![0];
        lower.EmptyFaces();
        var upper = w.WorldRoot[0].Children![4];
        for (int l = 0; l < 6; l++) upper.Texture[l] = 9;
        upper.EmptyFaces();

        var sel = new SauerbratenRevival.World.Commands.Selection(
            new Vector3I(0, 0, 2), new Vector3I(1, 1, 1), 2, (int)Orient.Bottom);
        OctaEdit.EditFace(w, sel, dir: -1, mode: 1);
        TestAssert.True(lower.IsEntirelySolid(), "mode 1 fill pulls the slice solid");
        TestAssert.Equal(9, lower.Texture[0], "textures copied from backing cell");
    }
}
