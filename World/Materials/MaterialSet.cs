using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Materials;

/// <summary>World-space texture-coordinate generators for one face axis (octarender.cpp calctexgen).</summary>
public readonly struct TexGen
{
    public readonly Vector4 S;
    public readonly Vector4 T;

    public TexGen(Vector4 s, Vector4 t)
    {
        S = s;
        T = t;
    }

    public Vector2 Uv(Vector3 p) => new(
        S.X * p.X + S.Y * p.Y + S.Z * p.Z + S.W,
        T.X * p.X + T.Y * p.Y + T.Z * p.Z + T.W);
}

/// <summary>
/// Per-vslot Godot materials plus the UV generators the mesher needs. The mesher
/// groups triangles by vslot, so one material per vslot is the natural mapping.
/// </summary>
public sealed class MaterialSet
{
    private readonly Dictionary<int, Material> _materials = new();
    private readonly Dictionary<int, TexGen[]> _uv = new();

    public IEnumerable<KeyValuePair<int, Material>> Materials => _materials;

    public void Add(int vslot, Material material, TexGen[] uvByDim)
    {
        _materials[vslot] = material;
        _uv[vslot] = uvByDim;
    }

    public Material? MaterialFor(int vslot) => _materials.GetValueOrDefault(vslot);

    /// <summary>UV for a world-space (Sauerbraten) position on a face of the given axis.</summary>
    public Vector2 Uv(int vslot, int dim, Vector3 worldPos)
    {
        if (!_uv.TryGetValue(vslot, out var gens)) return Vector2.Zero;
        return gens[dim].Uv(worldPos);
    }
}
