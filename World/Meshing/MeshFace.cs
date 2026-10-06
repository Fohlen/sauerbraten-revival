using Godot;

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
    public static void Quad(ArrayMeshBuilder builder, int texture, int orient,
                            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                            Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
    {
        Vector3 axis = AxisNormal(orient);
        Vector3 cross = (p1 - p0).Cross(p2 - p0);
        Vector3 normal = cross.LengthSquared() < 1e-9f ? axis : cross.Normalized();
        if (normal.Dot(axis) < 0) normal = -normal; // outward, regardless of input order

        builder.AddQuad(texture, p0, p1, p2, p3, uv0, uv1, uv2, uv3, normal,
            Reverse(p0, p1, p2, normal), Reverse(p0, p2, p3, normal));
    }

    /// <summary>Whether a triangle must be reversed to face outward under Godot's front-face convention.</summary>
    private static bool Reverse(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
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
