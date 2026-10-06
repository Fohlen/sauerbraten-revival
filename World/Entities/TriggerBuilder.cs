using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Builds the trigger entities (<see cref="Teleport"/>/<see cref="TeleDest"/>/
/// <see cref="JumpPad"/>) and records the teleport → destination mapping. Only
/// representation for now; the runtime behaviour needs collision + game logic.
/// </summary>
public static class TriggerBuilder
{
    public static Node3D Build(OgzMap map, MetadataCatalogue? catalogue = null)
    {
        var root = new Node3D { Name = "Triggers" };
        var teleports = new List<Teleport>();
        var destinations = new List<TeleDest>();

        for (int i = 0; i < map.Entities.Count; i++)
        {
            var e = map.Entities[i];
            switch (e.Type)
            {
                case EntityType.Teleport:
                    var teleport = new Teleport
                    {
                        Name = $"Teleport_{i}",
                        Position = e.Position,
                        Tag = e.Attr1,
                        ModelIndex = e.Attr2,
                        Hook = e.Attr3,
                        Sound = e.Attr4,
                        ModelPath = ResolveModel(e.Attr2, catalogue),
                    };
                    teleports.Add(teleport);
                    root.AddChild(teleport);
                    break;

                case EntityType.TeleDest:
                    var dest = new TeleDest
                    {
                        Name = $"TeleDest_{i}",
                        Position = e.Position,
                        Tag = e.Attr2,
                        Yaw = e.Attr1,
                        Pushed = e.Attr3 > 0,
                    };
                    var forward = PlayerStartBuilder.Forward(e.Attr1);
                    if (forward.LengthSquared() > 1e-6f)
                        dest.Basis = Basis.LookingAt(forward, new Vector3(0, 0, 1));
                    destinations.Add(dest);
                    root.AddChild(dest);
                    break;

                case EntityType.JumpPad:
                    root.AddChild(new JumpPad
                    {
                        Name = $"JumpPad_{i}",
                        Position = e.Position,
                        Push = Push(e),
                        Sound = e.Attr4,
                    });
                    break;
            }
        }

        // The engine picks the first TELEDEST whose attr2 equals the teleport's attr1.
        foreach (var teleport in teleports)
        {
            foreach (var dest in destinations)
            {
                if (dest.Tag == teleport.Tag) { teleport.Destination = dest; break; }
            }
        }

        return root;
    }

    /// <summary>fpsgame/entities.cpp JUMPPAD: <c>((sbyte)attr3*10, (sbyte)attr2*10, attr1*12.5)</c>.</summary>
    public static Vector3 Push(OgzEntity e) =>
        new((sbyte)e.Attr3 * 10.0f, (sbyte)e.Attr2 * 10.0f, e.Attr1 * 12.5f);

    public static string? ResolveModel(int modelIndex, MetadataCatalogue? catalogue) => modelIndex switch
    {
        > 0 => catalogue?.MapModelPath(modelIndex),
        0 => "teleporter",
        _ => null,
    };
}
