using System;
using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Universal read-only contract for a stored map entity (Type, Position, and 5 short attributes).
/// Implemented by both legacy OGZ entities (<see cref="Runtime.Formats.Ogz.OgzEntity"/>)
/// and native snapshot entities (<see cref="Runtime.Formats.Save.MapEntity"/>).
/// </summary>
public interface IEntityData
{
    /// <summary>Entity type identifier (<see cref="EntityType"/>).</summary>
    byte Type { get; }

    /// <summary>Entity position in Sauerbraten (Z-up) world coordinates.</summary>
    Vector3 Position { get; }

    short Attr1 { get; }
    short Attr2 { get; }
    short Attr3 { get; }
    short Attr4 { get; }
    short Attr5 { get; }
}

/// <summary>
/// Query extension methods for entity collections.
/// </summary>
public static class EntityDataExtensions
{
    /// <summary>Iterate entities matching an entity type ID, yielding (index, entity).</summary>
    public static IEnumerable<(int Index, T Entity)> OfType<T>(this IReadOnlyList<T> entities, byte type) where T : IEntityData
    {
        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i].Type == type)
                yield return (i, entities[i]);
        }
    }

    /// <summary>Iterate entities matching a predicate, yielding (index, entity).</summary>
    public static IEnumerable<(int Index, T Entity)> WhereIndexed<T>(this IReadOnlyList<T> entities, Func<T, bool> predicate) where T : IEntityData
    {
        for (int i = 0; i < entities.Count; i++)
        {
            if (predicate(entities[i]))
                yield return (i, entities[i]);
        }
    }
}
