using System.Collections.Generic;
using Godot;

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
    private readonly Dictionary<int, List<int>> _indices = new();

    /// <summary>Total triangles emitted.</summary>
    public int TriangleCount { get; private set; }

    /// <summary>Number of distinct textures (Godot surfaces).</summary>
    public int TextureCount => _positions.Count;

    /// <summary>Emit a quad (p0,p1,p2,p3) as two triangles with a flat normal.</summary>
    public void AddQuad(int texture, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal)
    {
        if (!_positions.TryGetValue(texture, out var positions))
        {
            positions = new List<Vector3>();
            _positions[texture] = positions;
            _normals[texture] = new List<Vector3>();
            _indices[texture] = new List<int>();
        }

        var normals = _normals[texture];
        var indices = _indices[texture];
        int b = positions.Count;
        positions.Add(p0);
        positions.Add(p1);
        positions.Add(p2);
        positions.Add(p3);
        for (int i = 0; i < 4; i++) normals.Add(normal);
        indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
        indices.Add(b); indices.Add(b + 2); indices.Add(b + 3);
        TriangleCount += 2;
    }

    /// <summary>Build the mesh. Surfaces are keyed by texture; materials are assigned separately.</summary>
    public ArrayMesh Build()
    {
        var mesh = new ArrayMesh();
        foreach (var entry in _positions)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = entry.Value.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _normals[entry.Key].ToArray();
            arrays[(int)Mesh.ArrayType.Index] = _indices[entry.Key].ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
        return mesh;
    }
}
