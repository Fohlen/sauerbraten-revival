using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Materials;

/// <summary>
/// One Godot surface's identity: the vslot (material) <em>and</em> the lightmap
/// atlas it samples. Two faces that share a material but sample different atlases
/// must land in different surfaces, because a surface carries one material — and
/// the material is where the atlas texture is bound (docs/lighting.md §3.4). This
/// is the importer's version of the engine's element key
/// <c>sortkey(tex, lmid, ...)</c> (octarender.cpp:180).
/// </summary>
public readonly record struct SurfaceKey(int Texture, int Lmid)
{
    /// <summary>An unlit surface (no baked atlas): the reserved-0 case.</summary>
    public static SurfaceKey Unlit(int texture) => new(texture, 0);
}

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
/// Per-vslot Godot materials plus the UV generators the mesher needs, and an
/// optional baked-lightmap material per <see cref="SurfaceKey"/> (vslot + atlas).
/// The mesher groups triangles by <see cref="SurfaceKey"/>, so a face that samples
/// a baked atlas resolves to a lightmap material, and an unlit face resolves to
/// the plain vslot material.
/// </summary>
public sealed class MaterialSet
{
    private readonly Dictionary<int, Material> _materials = new();
    private readonly Dictionary<int, TexGen[]> _uv = new();
    private readonly Dictionary<SurfaceKey, Material> _lightmapped = new();

    public IEnumerable<KeyValuePair<int, Material>> Materials => _materials;

    public void Add(int vslot, Material material, TexGen[] uvByDim)
    {
        _materials[vslot] = material;
        _uv[vslot] = uvByDim;
    }

    /// <summary>
    /// Register a lightmap material for a specific (vslot, atlas) surface. The
    /// mesher's surfaces are keyed on this pair, so this is where the atlas texture
    /// is bound (built from the map's parsed atlases — see docs/lighting.md §3.3).
    /// </summary>
    public void AddLightmapped(SurfaceKey key, Material material) => _lightmapped[key] = material;

    /// <summary>Material for a surface key: the lightmap material when present, else the vslot's.</summary>
    public Material? MaterialFor(SurfaceKey key) =>
        _lightmapped.TryGetValue(key, out var lm) ? lm : _materials.GetValueOrDefault(key.Texture);

    /// <summary>Material for a vslot, ignoring the atlas (the unlit path).</summary>
    public Material? MaterialFor(int vslot) => _materials.GetValueOrDefault(vslot);

    /// <summary>UV for a world-space (Sauerbraten) position on a face of the given axis.</summary>
    public Vector2 Uv(int vslot, int dim, Vector3 worldPos)
    {
        if (!_uv.TryGetValue(vslot, out var gens)) return Vector2.Zero;
        return gens[dim].Uv(worldPos);
    }
}
