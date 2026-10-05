using System;
using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Ogz;

/// <summary>worldio.cpp: <c>enum { OCTSAV_CHILDREN = 0, OCTSAV_EMPTY, OCTSAV_SOLID, OCTSAV_NORMAL, OCTSAV_LODCUBE }</c>.</summary>
public enum OgzCubeType : byte
{
    Children = 0,
    Empty = 1,
    Solid = 2,
    Normal = 3,
    Lod = 4,
}

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

/// <summary>shared/ents.h: <c>struct entity</c> plus the fpsgame-specific fields stay raw here.</summary>
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

/// <summary>worldio.cpp: a face's <c>surfaceinfo</c> plus the vertices reconstructed from the file.</summary>
public sealed class OgzSurface
{
    public byte Lmid0, Lmid1;

    /// <summary>The raw <c>verts</c> byte read from the file (the vertmask).</summary>
    public byte VertMask;

    /// <summary>The relocated offset into the cube's vertex list (engine's <c>surf.verts</c> after load).</summary>
    public int VertOffset;

    public byte NumVerts;
    public VertInfo[] Vertices = Array.Empty<VertInfo>();

    public int TotalVerts => (NumVerts & Octa.LAYER_DUP) != 0
        ? (NumVerts & Octa.MAXFACEVERTS) * 2
        : NumVerts & Octa.MAXFACEVERTS;

    public bool Used => Lmid0 != 0 || Lmid1 != 0 || (NumVerts & ~Octa.LAYER_TOP) != 0;
}

/// <summary>worldio.cpp: one octree node. Children != null means an internal node (OCTSAV_CHILDREN or LODCUBE).</summary>
public sealed class OgzCube
{
    public OgzCubeType Type;
    public ushort[] Texture = new ushort[6];
    public byte[] Edges = new byte[12];
    public ushort Material;
    public byte Merged;
    public OgzSurface?[]? Surfaces;
    public OgzCube[]? Children;

    public bool IsEmpty => Type == OgzCubeType.Empty;
    public bool IsEntirelySolid => Type == OgzCubeType.Solid;
}

/// <summary>The parsed contents of an OGZ map, up to (and including) the octree.</summary>
public sealed class OgzMap
{
    public OgzHeader Header = new();
    public List<OgzVar> Vars = new();
    public string GameIdent = "";
    public int ExtraEntInfoSize;
    public List<ushort> TexMru = new();
    public List<OgzEntity> Entities = new();
    public List<OgzVSlot> VSlots = new();
    public OgzCube[] WorldRoot = Array.Empty<OgzCube>();
}
