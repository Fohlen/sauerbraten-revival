using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Meshing;

/// <summary>
/// Accumulates triangles into one Godot surface per texture vslot.
/// One <see cref="ArrayMesh"/> surface carries a single material, so this is the
/// natural grouping point for the importer's material manifest.
/// </summary>
public sealed class ArrayMeshBuilder
{
    private readonly Dictionary<int, List<Vector3>> _positions = new();
    private readonly Dictionary<int, List<Vector3>> _normals = new();
    private readonly Dictionary<int, List<Vector2>> _uvs = new();
    private readonly Dictionary<int, List<int>> _indices = new();

    /// <summary>Total triangles emitted.</summary>
    public int TriangleCount { get; private set; }

    /// <summary>Number of distinct surfaces (textures).</summary>
    public int TextureCount => _positions.Count;

    /// <summary>
    /// Emit a quad from four shared vertices with a flat normal and per-vertex
    /// UVs. The two triangles (0,1,2) and (0,2,3) can be reversed independently,
    /// so a non-planar or concave face still faces the same way on both halves.
    /// </summary>
    public void AddQuad(int vslot, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3, Vector3 normal,
                        bool reverseFirst = false, bool reverseSecond = false)
    {
        if (!_positions.TryGetValue(vslot, out var positions))
        {
            positions = new List<Vector3>();
            _positions[vslot] = positions;
            _normals[vslot] = new List<Vector3>();
            _uvs[vslot] = new List<Vector2>();
            _indices[vslot] = new List<int>();
        }

        var normals = _normals[vslot];
        var uvs = _uvs[vslot];
        var indices = _indices[vslot];
        int b = positions.Count;
        positions.Add(p0);
        positions.Add(p1);
        positions.Add(p2);
        positions.Add(p3);
        uvs.Add(uv0);
        uvs.Add(uv1);
        uvs.Add(uv2);
        uvs.Add(uv3);
        for (int i = 0; i < 4; i++) normals.Add(normal);
        indices.Add(b);
        indices.Add(reverseFirst ? b + 2 : b + 1);
        indices.Add(reverseFirst ? b + 1 : b + 2);
        indices.Add(b);
        indices.Add(reverseSecond ? b + 3 : b + 2);
        indices.Add(reverseSecond ? b + 2 : b + 3);
        TriangleCount += 2;
    }

    /// <summary>
    /// Emit a single triangle with a flat normal and per-vertex UVs.
    /// </summary>
    public void AddTriangle(int vslot, Vector3 p0, Vector3 p1, Vector3 p2,
                            Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 normal,
                            bool reverse = false)
    {
        if (!_positions.TryGetValue(vslot, out var positions))
        {
            positions = new List<Vector3>();
            _positions[vslot] = positions;
            _normals[vslot] = new List<Vector3>();
            _uvs[vslot] = new List<Vector2>();
            _indices[vslot] = new List<int>();
        }

        var normals = _normals[vslot];
        var uvs = _uvs[vslot];
        var indices = _indices[vslot];
        int b = positions.Count;
        positions.Add(p0);
        positions.Add(p1);
        positions.Add(p2);
        uvs.Add(uv0);
        uvs.Add(uv1);
        uvs.Add(uv2);
        for (int i = 0; i < 3; i++) normals.Add(normal);
        indices.Add(b);
        indices.Add(reverse ? b + 2 : b + 1);
        indices.Add(reverse ? b + 1 : b + 2);
        TriangleCount += 1;
    }

    /// <summary>Build the mesh. When materials are supplied, each surface gets its vslot's material.</summary>
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
            arrays[(int)Mesh.ArrayType.Index] = _indices[entry.Key].ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            var material = materials?.MaterialFor(entry.Key);
            if (material != null) mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
        return mesh;
    }
}
