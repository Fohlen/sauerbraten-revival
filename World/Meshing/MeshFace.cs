using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Meshing;

/// <summary>
/// Emits a leaf face as a quad, owning the one place where the winding and normal
/// conventions live. Both meshers go through here so they produce identical,
/// cullable geometry.
/// </summary>
public static class MeshFace
{
    /// <summary>
    /// Godot's front faces are clockwise when viewed from the front. Verified
    /// headlessly against <see cref="BoxMesh"/>/<see cref="PlaneMesh"/>, whose
    /// stored normals are outward (every triangle has cross·normal &lt; 0).
    /// </summary>
    public static readonly bool FrontFacesAreClockwise = true;

    /// <summary>
    /// Emit four world-space corners as a quad with per-vertex UVs. The corners
    /// are accepted in any planar order and split (0,1,2)+(0,2,3); each triangle
    /// is wound independently against the outward <paramref name="orient"/>
    /// normal, so concave or non-planar faces never flip.
    /// </summary>
    public static void Quad(ArrayMeshBuilder builder, int vslot, int orient,
                            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                            Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
    {
        // The engine never derives a face normal from the polygon's corners; for a
        // face with no stored/envmap normal it uses the flat axis normal
        // (octarender.cpp: bvec(128,128,255) == the +axis normal). Using the
        // geometric cross-product instead makes each face's normal tilt slightly
        // (and differ from its coplanar neighbours when the corners are off-plane),
        // which shimmers at grazing angles as the per-face coverage changes.
        Vector3 normal = AxisNormal(orient);

        builder.AddQuad(vslot, p0, p1, p2, p3, uv0, uv1, uv2, uv3, normal,
            Reverse(p0, p1, p2, normal), Reverse(p0, p2, p3, normal));
    }

    /// <summary>
    /// Emit three world-space corners as a single triangle with per-vertex UVs,
    /// wound against the outward <paramref name="orient"/> normal.
    /// </summary>
    public static void Triangle(ArrayMeshBuilder builder, int vslot, int orient,
                                Vector3 p0, Vector3 p1, Vector3 p2,
                                Vector2 uv0, Vector2 uv1, Vector2 uv2)
    {
        Vector3 normal = AxisNormal(orient);

        builder.AddTriangle(vslot, p0, p1, p2, uv0, uv1, uv2, normal,
            Reverse(p0, p1, p2, normal));
    }

    /// <summary>
    /// Emit a convex polygon (3..MAXFACEVERTS vertices) fanned from vertex 0:
    /// (0, i+1, i+2) for i in 0..numverts-3, skipping degenerate triangles,
    /// wound against the outward <paramref name="orient"/> normal.
    /// Matches octarender.cpp addtris.
    /// </summary>
    public static void Polygon(ArrayMeshBuilder builder, int vslot, int orient,
                               System.ReadOnlySpan<Vector3> verts,
                               System.ReadOnlySpan<Vector2> uvs)
    {
        Vector3 normal = AxisNormal(orient);
        for (int i = 0; i < verts.Length - 2; i++)
        {
            Vector3 p0 = verts[0], p1 = verts[i + 1], p2 = verts[i + 2];
            if (p0 == p1 || p1 == p2 || p2 == p0) continue;
            Vector2 uv0 = uvs[0], uv1 = uvs[i + 1], uv2 = uvs[i + 2];
            builder.AddTriangle(vslot, p0, p1, p2, uv0, uv1, uv2, normal,
                Reverse(p0, p1, p2, normal));
        }
    }

    /// <summary>Whether a triangle must be reversed to face outward under Godot's front-face convention.</summary>
    public static bool Reverse(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        Vector3 cross = (b - a).Cross(c - a);
        return FrontFacesAreClockwise == (cross.Dot(normal) > 0);
    }

    /// <summary>Outward axis normal for a face orientation (also the degenerate-face fallback).</summary>
    public static Vector3 AxisNormal(int orient) => orient switch
    {
        0 => new Vector3(-1, 0, 0),
        1 => new Vector3(1, 0, 0),
        2 => new Vector3(0, -1, 0),
        3 => new Vector3(0, 1, 0),
        4 => new Vector3(0, 0, -1),
        _ => new Vector3(0, 0, 1),
    };

    /// <summary>Sauerbraten cube-corner units (0..8) → absolute world position.</summary>
    public static Vector3 ToWorld(Vector3I v, Vector3I co, int size) => new(
        co.X + v.X * (size / 8.0f),
        co.Y + v.Y * (size / 8.0f),
        co.Z + v.Z * (size / 8.0f));
}
