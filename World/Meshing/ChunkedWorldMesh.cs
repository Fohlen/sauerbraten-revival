using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Meshing;

/// <summary>One spatial chunk of world geometry, positioned at <see cref="Origin"/> (Sauerbraten coords).</summary>
public sealed class MeshChunk
{
    public Vector3I Origin;
    public ArrayMesh Mesh = null!;
}

/// <summary>The meshed world, split into spatial chunks for incremental updates and culling.</summary>
public sealed class ChunkedWorldMesh
{
    public List<MeshChunk> Chunks = new();
    public int TriangleCount;
    public int SurfaceCount;
    public int ChunkSize;
}
