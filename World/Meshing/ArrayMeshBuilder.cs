using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Meshing;

/// <summary>
/// Accumulates triangles into one Godot surface per <see cref="SurfaceKey"/>
/// (texture + lightmap atlas). One <see cref="ArrayMesh"/> surface carries a
/// single material, so this is the natural grouping point for the importer's
/// material manifest.
/// </summary>
public sealed class ArrayMeshBuilder
{
    private readonly Dictionary<SurfaceKey, List<Vector3>> _positions = new();
    private readonly Dictionary<SurfaceKey, List<Vector3>> _normals = new();
    private readonly Dictionary<SurfaceKey, List<Vector2>> _uvs = new();
    private readonly Dictionary<SurfaceKey, List<Vector2>> _uv2s = new();
    private readonly Dictionary<SurfaceKey, List<int>> _indices = new();
    private readonly HashSet<SurfaceKey> _hasUv2 = new();

    /// <summary>Total triangles emitted.</summary>
    public int TriangleCount { get; private set; }

    /// <summary>Number of distinct surfaces (texture + atlas).</summary>
    public int TextureCount => _positions.Count;

    /// <summary>
    /// Emit a quad from four shared vertices with a flat normal and per-vertex
    /// UVs. The two triangles (0,1,2) and (0,2,3) can be reversed independently,
    /// so a non-planar or concave face still faces the same way on both halves.
    ///
    /// When <paramref name="hasUv2"/> is true the four <c>uv2*</c> values are the
    /// baked-lightmap atlas coordinates (normalized <c>u/65536</c>); otherwise the
    /// surface gets no UV2 channel and the face is unlit.
    /// </summary>
    public void AddQuad(SurfaceKey key, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3, Vector3 normal,
                        bool reverseFirst = false, bool reverseSecond = false,
                        bool hasUv2 = false,
                        Vector2 uv2_0 = default, Vector2 uv2_1 = default,
                        Vector2 uv2_2 = default, Vector2 uv2_3 = default)
    {
        if (!_positions.TryGetValue(key, out var positions))
        {
            positions = new List<Vector3>();
            _positions[key] = positions;
            _normals[key] = new List<Vector3>();
            _uvs[key] = new List<Vector2>();
            _uv2s[key] = new List<Vector2>();
            _indices[key] = new List<int>();
        }

        var normals = _normals[key];
        var uvs = _uvs[key];
        var uv2s = _uv2s[key];
        var indices = _indices[key];
        if (hasUv2) _hasUv2.Add(key);
        int b = positions.Count;
        positions.Add(p0);
        positions.Add(p1);
        positions.Add(p2);
        positions.Add(p3);
        uvs.Add(uv0);
        uvs.Add(uv1);
        uvs.Add(uv2);
        uvs.Add(uv3);
        uv2s.Add(uv2_0);
        uv2s.Add(uv2_1);
        uv2s.Add(uv2_2);
        uv2s.Add(uv2_3);
        for (int i = 0; i < 4; i++) normals.Add(normal);
        indices.Add(b);
        indices.Add(reverseFirst ? b + 2 : b + 1);
        indices.Add(reverseFirst ? b + 1 : b + 2);
        indices.Add(b);
        indices.Add(reverseSecond ? b + 3 : b + 2);
        indices.Add(reverseSecond ? b + 2 : b + 3);
        TriangleCount += 2;
    }

    /// <summary>Build the mesh. When materials are supplied, each surface gets its key's material.</summary>
    public ArrayMesh Build(MaterialSet? materials = null)
    {
        var mesh = new ArrayMesh();
        foreach (var entry in _positions)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = entry.Value.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _normals[entry.Key].ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = _uvs[entry.Key].ToArray();
            // Emit UV2 only when the surface was built with atlas coordinates;
            // Godot uses a zero-length array to mean "absent".
            if (_hasUv2.Contains(entry.Key))
                arrays[(int)Mesh.ArrayType.TexUV2] = _uv2s[entry.Key].ToArray();
            arrays[(int)Mesh.ArrayType.Index] = _indices[entry.Key].ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            var material = materials?.MaterialFor(entry.Key);
            if (material != null) mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
        return mesh;
    }
}
