using System;
using System.IO;
using Godot;
using SauerbratenRevival.World.Commands;

namespace SauerbratenRevival.World.Save;

/// <summary>
/// A serializable map entity — the data definition behind a runtime projection
/// (docs/editor-and-commands.md §3.5, §7.2). Mirrors <see cref="Ogz.OgzEntity"/> but
/// with **integer, fixed-point positions** (no floats on disk or wire, §11.7) so a
/// snapshot hash is trivial to keep deterministic.
///
/// Entities are a *view* of the command log, not scene data: <c>Level.tscn</c> never
/// stores them, and hand-editing is editor/command-path only. Until entity commands
/// land, this carries imported entities through the native format losslessly.
/// </summary>
public sealed class MapEntity
{
    /// <summary>shared/ents.h type id (see <see cref="Entities.EntityType"/>).</summary>
    public byte Type;

    /// <summary>Position in world units, fixed-point <see cref="FixedPoint.DMF"/> (1/16 unit).</summary>
    public int X, Y, Z;

    public short Attr1, Attr2, Attr3, Attr4, Attr5;

    public MapEntity() { }

    /// <summary>Build from a float position (OGZ import), quantizing to fixed-point exactly as Cube2 sends it.</summary>
    public static MapEntity FromOgz(Ogz.OgzEntity e)
    {
        var (x, y, z) = FixedPoint.Encode(e.Position.X, e.Position.Y, e.Position.Z);
        return new MapEntity
        {
            Type = e.Type,
            X = x, Y = y, Z = z,
            Attr1 = e.Attr1, Attr2 = e.Attr2, Attr3 = e.Attr3, Attr4 = e.Attr4, Attr5 = e.Attr5,
        };
    }

    /// <summary>The position in world units (float) — for building runtime projections.</summary>
    public Vector3 Position =>
        new(X / (float)FixedPoint.DMF, Y / (float)FixedPoint.DMF, Z / (float)FixedPoint.DMF);

    public void Write(BinaryWriter w)
    {
        w.Write(Type);
        w.Write(X); w.Write(Y); w.Write(Z);
        w.Write(Attr1); w.Write(Attr2); w.Write(Attr3); w.Write(Attr4); w.Write(Attr5);
    }

    public static MapEntity Read(BinaryReader r) => new()
    {
        Type = r.ReadByte(),
        X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(),
        Attr1 = r.ReadInt16(), Attr2 = r.ReadInt16(), Attr3 = r.ReadInt16(),
        Attr4 = r.ReadInt16(), Attr5 = r.ReadInt16(),
    };

    /// <summary>Mix the semantic fields into a hash accumulator, in a fixed order.</summary>
    public void Hash(Action<byte> mix)
    {
        mix(Type);
        void MixI32(int v) { for (int i = 0; i < 4; i++) mix((byte)(v >> (i * 8))); }
        void MixI16(short v) { mix((byte)v); mix((byte)(v >> 8)); }
        MixI32(X); MixI32(Y); MixI32(Z);
        MixI16(Attr1); MixI16(Attr2); MixI16(Attr3); MixI16(Attr4); MixI16(Attr5);
    }
}
