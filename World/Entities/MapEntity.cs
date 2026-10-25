using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SauerbratenRevival.World.Commands;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// The one stored map entity — shared/ents.h <c>struct entity</c> (type, position,
/// five shorts), used by both the classic OGZ reader and the native snapshot format.
///
/// <see cref="Position"/> stays a <see cref="float"/> in memory because that is what
/// r6889 holds and what <c>.ogz</c> stores (worldio.cpp writes/reads float positions,
/// and classic maps do contain sub-1/16 coordinates — see metl3/metl4). The *stored*
/// form (world.res, late-join snapshots) quantizes to <see cref="FixedPoint.DMF"/>,
/// so no floats reach disk or the wire and a snapshot hash stays deterministic
/// (docs/editor-and-commands.md §11.7). <see cref="Hash"/> mixes exactly the values
/// <see cref="Write"/> stores, so a live world and its reloaded snapshot hash alike.
///
/// Entities are a *view* of the command log, not scene data: <c>Level.tscn</c> never
/// stores them, and hand-editing is editor/command-path only (§3.5, §7.2).
/// </summary>
public sealed class MapEntity
{
    /// <summary>shared/ents.h type id (see <see cref="EntityType"/>).</summary>
    public byte Type { get; set; }

    /// <summary>Position in Sauerbraten (Z-up) world coordinates, in world units.</summary>
    public Vector3 Position { get; set; }

    public short Attr1 { get; set; }
    public short Attr2 { get; set; }
    public short Attr3 { get; set; }
    public short Attr4 { get; set; }
    public short Attr5 { get; set; }

    /// <summary>The canonical fixed-point position — the stored/hashed representation.</summary>
    public (int X, int Y, int Z) FixedPosition =>
        FixedPoint.Encode(Position.X, Position.Y, Position.Z);

    public void Write(BinaryWriter w)
    {
        var (x, y, z) = FixedPosition;
        w.Write(Type);
        w.Write(x); w.Write(y); w.Write(z);
        w.Write(Attr1); w.Write(Attr2); w.Write(Attr3); w.Write(Attr4); w.Write(Attr5);
    }

    public static MapEntity Read(BinaryReader r) => new()
    {
        Type = r.ReadByte(),
        Position = new Vector3(
            r.ReadInt32() / (float)FixedPoint.DMF,
            r.ReadInt32() / (float)FixedPoint.DMF,
            r.ReadInt32() / (float)FixedPoint.DMF),
        Attr1 = r.ReadInt16(),
        Attr2 = r.ReadInt16(),
        Attr3 = r.ReadInt16(),
        Attr4 = r.ReadInt16(),
        Attr5 = r.ReadInt16(),
    };

    /// <summary>Mix the semantic fields into a hash accumulator, in a fixed order.</summary>
    public void Hash(Action<byte> mix)
    {
        var (x, y, z) = FixedPosition;
        mix(Type);
        void MixI32(int v) { for (int i = 0; i < 4; i++) mix((byte)(v >> (i * 8))); }
        void MixI16(short v) { mix((byte)v); mix((byte)(v >> 8)); }
        MixI32(x); MixI32(y); MixI32(z);
        MixI16(Attr1); MixI16(Attr2); MixI16(Attr3); MixI16(Attr4); MixI16(Attr5);
    }
}

/// <summary>Query helpers for entity lists; entities are named by their map order.</summary>
public static class MapEntityExtensions
{
    /// <summary>Iterate entities matching an entity type ID, yielding (index, entity).</summary>
    public static IEnumerable<(int Index, MapEntity Entity)> OfType(this IReadOnlyList<MapEntity> entities, byte type)
    {
        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i].Type == type)
                yield return (i, entities[i]);
        }
    }

    /// <summary>Iterate entities matching a predicate, yielding (index, entity).</summary>
    public static IEnumerable<(int Index, MapEntity Entity)> WhereIndexed(this IReadOnlyList<MapEntity> entities, Func<MapEntity, bool> predicate)
    {
        for (int i = 0; i < entities.Count; i++)
        {
            if (predicate(entities[i]))
                yield return (i, entities[i]);
        }
    }
}
