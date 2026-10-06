using Godot;

namespace SauerbratenRevival.Tests;

/// <summary>Mesh inspection helpers for tests.</summary>
public static class MeshAssert
{
    /// <summary>Total triangles across all of a mesh's surfaces.</summary>
    public static long Triangles(ArrayMesh mesh)
    {
        long triangles = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            triangles += mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Index].AsInt32Array().Length / 3;
        return triangles;
    }

    /// <summary>Total triangles across a chunked world mesh.</summary>
    public static long Triangles(World.Meshing.ChunkedWorldMesh world)
    {
        long triangles = 0;
        foreach (var chunk in world.Chunks) triangles += Triangles(chunk.Mesh);
        return triangles;
    }
}
