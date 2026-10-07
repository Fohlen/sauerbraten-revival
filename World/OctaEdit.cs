using Godot;

namespace SauerbratenRevival.World;

/// <summary>
/// Editor/world-mutation primitives ported from <c>octaedit.cpp</c> and the
/// structural helpers from <c>octa.cpp</c>. This is the "OctaEdit" module
/// referenced by docs/editor-and-commands.md: the algorithms, not the original's
/// rendering/GUI/undo plumbing.
///
/// M1a scope: the managed-octree structural primitives that the original's flat
/// <c>cube[]</c> model assumed — copy, subdivide, paste, discard. Op handlers
/// (add/remove/fill/face) are built on these in M1b.
///
/// The load-bearing divergence from the original: <see cref="Cube.Children"/> is
/// a managed array, not a pointer into a flat pool. There is no manual alloc/free;
/// we unwire children and let the GC collect, keeping <see cref="Octa.AllocNodes"/>
/// in step for bookkeeping/tests.
/// </summary>
public static class OctaEdit
{
    /// <summary>
    /// octaedit.cpp:644 <c>copycube(src, dst)</c> — deep-copy <paramref name="src"/>
    /// into <paramref name="dst"/>, allocating a fresh child block. Visibility,
    /// merge state and extended render info are cleared (the source owns its
    /// <c>va</c>/<c>ext</c> lifecycle in the original; here <see cref="CubeExt"/>
    /// is not shared on copy).
    /// </summary>
    public static void CopyCube(Octa octa, Cube src, Cube dst)
    {
        dst.Children = null;
        dst.Ext = null;
        dst.Faces = src.Faces;
        dst.Texture = src.Texture;
        dst.Material = src.Material;
        dst.Merged = 0;
        dst.Visible = 0;
        if (src.Children != null)
        {
            dst.Children = octa.NewCubes(Octa.F_EMPTY);
            for (int i = 0; i < 8; i++) CopyCube(octa, src.Children[i], dst.Children[i]);
        }
    }

    /// <summary>
    /// octaedit.cpp:657 <c>pastecube(src, dst)</c> — replace <paramref name="dst"/>
    /// with a copy of <paramref name="src"/>, discarding any existing subtree.
    /// </summary>
    public static void PasteCube(Octa octa, Cube src, Cube dst)
    {
        octa.DiscardChildren(dst);
        CopyCube(octa, src, dst);
    }

    /// <summary>
    /// octa.cpp:370-ish <c>subdividecube(c, ...)</c> — split a leaf cube into eight
    /// children, inheriting its solid/empty state, material and textures. Used by
    /// lookups that descend into cube-sized regions.
    ///
    /// Edge-deformation resampling (the original's <c>copyedges</c>/<c>forcemip</c>
    /// path for non-flat cubes) is NOT handled here yet. Subdividing a deformed
    /// leaf is a fidelity gap; it belongs with the ops that require it (M1b) and
    /// must be verified against the original. Flat / entirely-solid / entirely-empty
    /// cubes — the common editor cases — inherit exactly.
    /// </summary>
    public static void SubdivideCube(Octa octa, Cube c)
    {
        if (c.Children != null) return;
        bool empty = c.IsEmpty();
        Cube[] ch = octa.NewCubes(empty ? Octa.F_EMPTY : Octa.F_SOLID, c.Material);
        for (int i = 0; i < 8; i++)
            for (int l = 0; l < 6; l++) ch[i].Texture[l] = c.Texture[l];
        c.Children = ch;
    }
}
