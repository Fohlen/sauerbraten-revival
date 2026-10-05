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

    /// <summary>Wrap the parsed octree in the runtime <see cref="Octa"/>.</summary>
    public Octa ToOcta()
    {
        int worldscale = 0;
        while ((1 << worldscale) < Header.WorldSize) worldscale++;
        return new Octa(worldscale, Header.WorldSize, WorldRoot, CountAllocNodes(WorldRoot));
    }

    private static int CountAllocNodes(Cube[] cubes)
    {
        int count = 1; // worldio.cpp newcubes() allocated this cube[8] block
        foreach (var c in cubes)
            if (c.Children != null) count += CountAllocNodes(c.Children);
        return count;
    }
}
