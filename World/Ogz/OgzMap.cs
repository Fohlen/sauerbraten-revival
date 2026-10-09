using System;
using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Ogz;

/// <summary>worldio.cpp: <c>octaheader</c>. The r6889 header is 40 bytes (adds <c>numvslots</c> over the v29 docs).</summary>
public sealed class OgzHeader
{
    public string Magic = "";
    public int Version;
    public int HeaderSize;
    public int WorldSize;
    public int NumEnts;
    public int NumPvs;
    public int Lightmaps;
    public int BlendMap;
    public int NumVars;
    public int NumVSlots;
}

/// <summary>worldio.cpp variable block (ID_VAR / ID_FVAR / ID_SVAR).</summary>
public sealed class OgzVar
{
    public byte Type;
    public string Name = "";
    public int IntValue;
    public float FloatValue;
    public string StringValue = "";
}

/// <summary>shared/ents.h: <c>struct entity</c>; fpsgame-specific fields stay raw here.</summary>
public sealed class OgzEntity
{
    public Vector3 Position;
    public short Attr1, Attr2, Attr3, Attr4, Attr5;
    public byte Type;
    public byte Reserved;
}

/// <summary>A resolved slot shader param (VSLOT_SHPARAM).</summary>
public sealed class OgzShaderParam
{
    public string Name = "";
    public float[] Value = new float[4];
}

/// <summary>worldio.cpp: one <c>VSlot</c> as serialized by <c>loadvslot</c>.</summary>
public sealed class OgzVSlot
{
    public int Changed;
    public int Prev = -1;
    public List<OgzShaderParam> Params = new();
    public float Scale = 1f;
    public int Rotation;
    public int OffsetX, OffsetY;
    public float ScrollX, ScrollY;
    public int Layer;
    public float AlphaFront = 0.5f, AlphaBack;
    public Vector3 ColorScale = Vector3.One;
}

/// <summary>worldio.cpp: one <c>LightMap</c> as stored in the OGZ (engine/lightmap.h).</summary>
public sealed class OgzLightMap
{
    /// <summary>Raw type byte: low nibble <c>LM_TYPE</c> (0 diffuse, 1 bump0, 2 bump1), bit 0x10 <c>LM_ALPHA</c>, bit 0x80 unlit-texel present.</summary>
    public int Type;

    /// <summary>Bytes per pixel: 4 when <c>(type &amp; LM_ALPHA) &amp;&amp; (type &amp; LM_TYPE) != LM_BUMPMAP1</c>, else 3.</summary>
    public int Bpp;

    /// <summary>Whether the 0x80 flag was set (a 1x1 unlit/ambient texel exists at <see cref="UnlitX"/>,<see cref="UnlitY"/>).</summary>
    public bool HasUnlit;

    /// <summary>Atlas coordinates of the unlit texel (valid only when <see cref="HasUnlit"/>).</summary>
    public ushort UnlitX, UnlitY;

    /// <summary>Raw atlas pixels, <c>Bpp * 512 * 512</c> bytes, in file order (not flipped).</summary>
    public byte[] Pixels = Array.Empty<byte>();
}

/// <summary>
/// The parsed contents of an OGZ map. <see cref="WorldRoot"/> is the runtime
/// octree (the reader builds <see cref="Cube"/>/<see cref="CubeExt"/> directly),
/// so there is no separate conversion pass.
/// </summary>
public sealed class OgzMap
{
    public OgzHeader Header = new();
    public List<OgzVar> Vars = new();
    public string GameIdent = "";
    public int ExtraEntInfoSize;
    public List<ushort> TexMru = new();
    public List<OgzEntity> Entities = new();
    public List<OgzVSlot> VSlots = new();
    public Cube[] WorldRoot = Array.Empty<Cube>();

    /// <summary>Baked lightmap atlases, one per <c>hdr.lightmaps</c> (empty when the map is unlit).</summary>
    public List<OgzLightMap> Lightmaps = new();

    /// <summary>Look up a string map variable (e.g. <c>skybox</c>).</summary>
    public string? GetVarString(string name)
    {
        foreach (var v in Vars)
            if (v.Type == 2 && v.Name == name) return v.StringValue;
        return null;
    }

    /// <summary>Iterate the entities of one type, yielding (index, entity) so nodes can be named by map order.</summary>
    public IEnumerable<(int Index, OgzEntity Entity)> OfType(byte type) => Where(e => e.Type == type);

    /// <summary>Iterate the entities matching a predicate, yielding (index, entity).</summary>
    public IEnumerable<(int Index, OgzEntity Entity)> Where(System.Func<OgzEntity, bool> predicate)
    {
        for (int i = 0; i < Entities.Count; i++)
            if (predicate(Entities[i])) yield return (i, Entities[i]);
    }

    /// <summary>Wrap the parsed octree in the runtime <see cref="Octa"/>.</summary>
    public Octa ToOcta()
    {
        int worldscale = 0;
        while ((1 << worldscale) < Header.WorldSize) worldscale++;
        return new Octa(worldscale, Header.WorldSize, WorldRoot, CountAllocNodes(WorldRoot));
    }

    /// <summary>
    /// Independent deep copy: the octree is cloned (see <see cref="Octa.Clone"/>)
    /// and every metadata list is copied, so a clone can be mutated without
    /// touching the original. Used to hand out a fresh world from a cached parse
    /// without re-reading and re-parsing the OGZ file.
    /// </summary>
    public OgzMap Clone()
    {
        var header = Header;
        return new OgzMap
        {
            Header = new OgzHeader
            {
                Magic = header.Magic, Version = header.Version, HeaderSize = header.HeaderSize,
                WorldSize = header.WorldSize, NumEnts = header.NumEnts, NumPvs = header.NumPvs,
                Lightmaps = header.Lightmaps, BlendMap = header.BlendMap, NumVars = header.NumVars,
                NumVSlots = header.NumVSlots,
            },
            Vars = new List<OgzVar>(Vars),
            GameIdent = GameIdent,
            ExtraEntInfoSize = ExtraEntInfoSize,
            TexMru = new List<ushort>(TexMru),
            Entities = new List<OgzEntity>(Entities),
            VSlots = new List<OgzVSlot>(VSlots),
            WorldRoot = Octa.CloneCubes(WorldRoot),
            // Atlas pixels are immutable after load; a shallow list copy is enough
            // (the OgzLightMap objects are never mutated).
            Lightmaps = new List<OgzLightMap>(Lightmaps),
        };
    }

    private static int CountAllocNodes(Cube[] cubes)
    {
        int count = 1; // worldio.cpp newcubes() allocated this cube[8] block
        foreach (var c in cubes)
            if (c.Children != null) count += CountAllocNodes(c.Children);
        return count;
    }
}
