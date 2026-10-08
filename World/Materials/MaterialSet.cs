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
/// Per-vslot Godot materials plus the UV generators the mesher needs.
/// </summary>
public sealed class MaterialSet
{
    /// <summary>
    /// High bit used to key a per-vslot <em>transparent</em> surface variant. The
    /// mesher groups triangles by this key (<see cref="Meshing.ArrayMeshBuilder"/>),
    /// so an alpha face and an opaque face sharing a vslot still land in separate
    /// Godot surfaces with different materials.
    /// </summary>
    public const int AlphaBit = 1 << 30;

    private readonly Dictionary<int, Material> _materials = new();
    private readonly Dictionary<int, Material> _alphaMaterials = new();
    private readonly Dictionary<int, TexGen[]> _uv = new();
    private readonly Dictionary<int, int> _layer = new();

    public IEnumerable<KeyValuePair<int, Material>> Materials => _materials;

    public void Add(int vslot, Material material, TexGen[] uvByDim)
    {
        _materials[vslot] = material;
        _uv[vslot] = uvByDim;
    }

    /// <summary>Register the transparent variant for a vslot (used for <c>MAT_ALPHA</c> cubes).</summary>
    public void SetAlphaMaterial(int vslot, Material material) => _alphaMaterials[vslot] = material;

    /// <summary>Whether this vslot has a transparent variant.</summary>
    public bool HasAlpha(int vslot) => _alphaMaterials.ContainsKey(vslot);

    /// <summary>Record a vslot's bottom-layer vslot (<c>vslot.layer</c>; 0 means none).</summary>
    public void SetLayer(int vslot, int layer)
    {
        if (layer != 0) _layer[vslot] = layer;
    }

    /// <summary>The bottom-layer vslot index for a face texture, or 0 if it has none.</summary>
    public int LayerFor(int vslot) => _layer.GetValueOrDefault(vslot);

    /// <summary>Material for a surface key (vslot, optionally with <see cref="AlphaBit"/> set).</summary>
    public Material? MaterialFor(int key) => (key & AlphaBit) != 0
        ? _alphaMaterials.GetValueOrDefault(key & ~AlphaBit)
        : _materials.GetValueOrDefault(key);

    /// <summary>
    /// Surface key for a face: the vslot index, with <see cref="AlphaBit"/> set when
    /// the face is a transparent (<c>MAT_ALPHA</c>) cube face.
    /// </summary>
    public int Key(int vslot, bool alpha) => alpha ? vslot | AlphaBit : vslot;

    /// <summary>UV for a world-space (Sauerbraten) position on a face of the given axis.</summary>
    public Vector2 Uv(int vslot, int dim, Vector3 worldPos)
    {
        if (!_uv.TryGetValue(vslot, out var gens)) return Vector2.Zero;
        return gens[dim].Uv(worldPos);
    }
}
