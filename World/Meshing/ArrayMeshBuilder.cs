using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Meshing;

/// <summary>
/// Accumulates triangles into one Godot surface per texture (vslot index).
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

    /// <summary>Number of distinct textures (Godot surfaces).</summary>
    public int TextureCount => _positions.Count;

    /// <summary>Emit a quad (p0,p1,p2,p3) as two triangles with flat normal and per-vertex UVs.</summary>
    public void AddQuad(int texture, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3, Vector3 normal)
    {
        if (!_positions.TryGetValue(texture, out var positions))
        {
            positions = new List<Vector3>();
            _positions[texture] = positions;
            _normals[texture] = new List<Vector3>();
            _uvs[texture] = new List<Vector2>();
            _indices[texture] = new List<int>();
        }

        var normals = _normals[texture];
        var uvs = _uvs[texture];
        var indices = _indices[texture];
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
        indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
        indices.Add(b); indices.Add(b + 2); indices.Add(b + 3);
        TriangleCount += 2;
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
