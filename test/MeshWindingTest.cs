using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Guards the winding/normal convention in <see cref="MeshFace"/>. Godot renders
/// front faces clockwise; we derive that from a built-in primitive (whose normals
/// are outward) rather than hardcoding it, and check our world mesh agrees.
/// </summary>
public class MeshWindingTest : GoTestBase
{
    public MeshWindingTest(Node testScene) : base(testScene) { }

    [Test]
    public void GodotFrontFacesAreClockwise()
    {
        // BoxMesh's stored normals point outward. If the triangle's right-hand-rule
        // normal opposes them (cross·normal < 0), then front faces are clockwise.
        Mesh box = new BoxMesh();
        var arrays = box.SurfaceGetArrays(0);
        var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();

        int outward = 0, inward = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var cross = (positions[indices[t + 1]] - positions[indices[t]])
                .Cross(positions[indices[t + 2]] - positions[indices[t]]);
            if (cross.Dot(normals[indices[t]]) > 0) outward++; else inward++;
        }

        TestAssert.True(inward > 0 && outward == 0, "Godot front faces are clockwise");
        TestAssert.Equal(true, MeshFace.FrontFacesAreClockwise, "MeshFace matches Godot's convention");
    }

    [Test]
    public void WorldTrianglesWindOutward()
    {
        // With the vertex normal pointing outward and Godot's CW convention, every
        // emitted triangle's RHR normal must oppose its vertex normal — i.e. it is
        // front-facing when seen from outside and can be back-face culled.
        var octa = TestData.ReadMap().ToOcta();
        var mesh = new OctaMesher(octa).Build();

        long checkedTriangles = 0;
        int wrongWinding = 0, perpendicular = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                var cross = (positions[indices[t + 1]] - positions[indices[t]])
                    .Cross(positions[indices[t + 2]] - positions[indices[t]]);
                if (cross.LengthSquared() < 1e-12f) continue; // degenerate face
                float cosine = cross.Dot(normals[indices[t]]) / cross.Length();
                if (Mathf.Abs(cosine) < 1e-3f) { perpendicular++; continue; } // no well-defined facing
                if (cosine >= 0) wrongWinding++;
                checkedTriangles++;
            }
        }

        GD.Print($"[winding] checked {checkedTriangles} triangles, {wrongWinding} wrongly wound, {perpendicular} perpendicular");
        TestAssert.True(checkedTriangles > 1000, "checked a meaningful number of triangles");
        TestAssert.Equal(0, wrongWinding, "every world triangle winds outward (cullable)");
    }
}
