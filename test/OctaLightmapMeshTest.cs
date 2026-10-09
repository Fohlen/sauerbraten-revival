using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Meshing;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.Tests;

/// <summary>
/// The mesher's baked-lightmap plumbing (docs/lighting.md §3.4): faces that carry
/// a <see cref="CubeExt"/> surface link must emit <c>TexUV2</c> from the stored
/// per-vertex atlas UV, and their geometry must come from the <c>Ext</c> vertices
/// (which is what the engine renders — <c>octarender.cpp</c> builds <c>pos[k]</c>
/// from <c>verts[k].getxyz()</c> in stored order, with no convexity reorder).
///
/// This is plumbing only: the UV is the *normalized* atlas coordinate
/// (<c>u / 65536</c>); binding the actual atlas texture + shader is a later step.
/// </summary>
public class OctaLightmapMeshTest : GoTestBase
{
    public OctaLightmapMeshTest(Node testScene) : base(testScene) { }

    /// <summary>u/v are stored as ushort over the 512-wide atlas, so Godot UV = u / 65536.</summary>
    private const float UvScale = 1f / 65536f;

    private static Octa Church51() => TestData.ReadMap().ToOcta();

    /// <summary>Collect every face in the octree that has lightmap vertices.</summary>
    private static int CountLinkedFaces(Octa octa)
    {
        int faces = 0;
        void Walk(Cube[] cubes)
        {
            foreach (var c in cubes)
            {
                if (c.Children != null) { Walk(c.Children); continue; }
                if (c.Ext == null) continue;
                for (int f = 0; f < 6; f++)
                    if (c.Ext.Surfaces[f].TotalVerts() > 0) faces++;
            }
        }
        Walk(octa.WorldRoot);
        return faces;
    }

    [Test]
    public void EmitsUv2ForLightmappedFaces()
    {
        var octa = Church51();
        var mesh = new OctaMesher(octa).Build();

        long uv2Verts = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            if (!arrays[(int)Mesh.ArrayType.TexUV2].VariantType.Equals(Variant.Type.Nil))
                uv2Verts += arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array().Length;
        }
        TestAssert.True(uv2Verts > 0, "lightmapped faces emit TexUV2");
        GD.Print($"[lm-mesh] uv2 vertices across surfaces: {uv2Verts}");
    }

    [Test]
    public void Uv2MatchesStoredAtlasCoordinates()
    {
        // Every emitted UV2 must equal one of the stored (u,v)/65536 values on the
        // face it came from: build the set of the map's stored UVs and assert each
        // emitted one is a member. (The exact per-face correspondence is covered by
        // the position test below; this catches scaling/ordering slips.)
        var octa = Church51();
        var expected = new HashSet<(int, int)>();
        void Walk(Cube[] cubes)
        {
            foreach (var c in cubes)
            {
                if (c.Children != null) { Walk(c.Children); continue; }
                if (c.Ext == null) continue;
                for (int f = 0; f < 6; f++)
                {
                    var surf = c.Ext.Surfaces[f];
                    int total = surf.TotalVerts();
                    for (int k = 0; k < total; k++)
                    {
                        var v = c.Ext.Verts[surf.Verts + k];
                        expected.Add(((int)Mathf.Round(v.U * UvScale * 65536f), (int)Mathf.Round(v.V * UvScale * 65536f)));
                    }
                }
            }
        }
        Walk(octa.WorldRoot);

        var mesh = new OctaMesher(octa).Build();
        int checkedUvs = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            if (arrays[(int)Mesh.ArrayType.TexUV2].VariantType.Equals(Variant.Type.Nil)) continue;
            foreach (var uv in arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array())
            {
                // UV = ushort/65536, so unscale and round to the stored integer.
                int u = (int)Mathf.Round(uv.X * 65536f);
                int v = (int)Mathf.Round(uv.Y * 65536f);
                TestAssert.True(expected.Contains((u, v)), $"emitted UV2 ({u},{v}) is a stored atlas coordinate");
                checkedUvs++;
            }
        }
        TestAssert.True(checkedUvs > 0, "checked at least one UV2");
    }

    [Test]
    public void LinkedFaceGeometryComesFromExtVerts()
    {
        // A single linked face meshed in isolation must produce exactly the Ext
        // vertex positions (world-space), paired with their stored UVs. This pins
        // the k-order contract: ext verts are authoritative, not genfaceverts.
        var octa = Church51();
        var (cube, orient, co, size) = FindLinkedFace(octa);
        var surf = cube.Ext!.Surfaces[orient];
        int total = surf.TotalVerts();

        // Expected world positions + UVs, in stored order. VertInfo x/y/z are
        // absolute 1/8-world units (see OgzReader.SetXyz), so world = value / 8.
        var expectedPos = new List<Vector3>();
        var expectedUv = new List<Vector2>();
        for (int k = 0; k < total; k++)
        {
            var v = cube.Ext!.Verts[surf.Verts + k];
            var world = new Vector3(v.X / 8.0f, v.Y / 8.0f, v.Z / 8.0f);
            expectedPos.Add(world);
            expectedUv.Add(new Vector2(v.U * UvScale, v.V * UvScale));
        }

        var builder = new ArrayMeshBuilder();
        // Emit just this face via the mesher's per-face path.
        var mesher = new OctaMesher(octa);
        mesher.EmitFaceForTest(new CubeFace(cube, orient, co, size), co, builder);
        var mesh = builder.Build();

        var emitted = GatherPositionsAndUv2(mesh);
        TestAssert.Equal(total, emitted.Count, "emitted corner count for the face");

        // Compare as a multiset against the stored corners (chunk-local coords).
        foreach (var (pos, uv) in emitted)
        {
            bool found = false;
            for (int k = 0; k < expectedPos.Count; k++)
            {
                var localPos = expectedPos[k] - new Vector3(co.X, co.Y, co.Z);
                if (pos.IsEqualApprox(localPos) && uv.IsEqualApprox(expectedUv[k])) { found = true; break; }
            }
            TestAssert.True(found, $"emitted ({pos}, {uv}) matches a stored Ext vertex");
        }
    }

    private static List<(Vector3 Pos, Vector2 Uv)> GatherPositionsAndUv2(ArrayMesh mesh)
    {
        var result = new List<(Vector3, Vector2)>();
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            var pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (arrays[(int)Mesh.ArrayType.TexUV2].VariantType.Equals(Variant.Type.Nil)) continue;
            var uv2 = arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
            for (int i = 0; i < pos.Length; i++) result.Add((pos[i], uv2[i]));
        }
        return result;
    }

    private static (Cube Cube, int Orient, Vector3I Co, int Size) FindLinkedFace(Octa octa)
    {
        (Cube, int, Vector3I, int)? found = null;
        int size = octa.WorldSize >> 1;
        void Walk(Cube[] cubes, Vector3I co, int s)
        {
            for (int i = 0; i < 8 && found == null; i++)
            {
                var childCo = new Vector3I(
                    co.X + ((i & 1) >> 0) * s,
                    co.Y + ((i & 2) >> 1) * s,
                    co.Z + ((i & 4) >> 2) * s);
                var c = cubes[i];
                if (c.Children != null) { Walk(c.Children, childCo, s >> 1); continue; }
                if (c.Ext == null) continue;
                for (int f = 0; f < 6; f++)
                {
                    if (c.Ext.Surfaces[f].TotalVerts() > 0) { found = (c, f, childCo, s); return; }
                }
            }
        }
        Walk(octa.WorldRoot, Vector3I.Zero, size);
        TestAssert.True(found.HasValue, "found a linked face");
        return found!.Value;
    }
}
