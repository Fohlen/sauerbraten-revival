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
    public static (Node3D Root, List<JumpPad> JumpPads, List<Teleport> Teleports) BuildTriggers(
        OgzMap map,
        MetadataCatalogue? catalogue = null,
        IModelCatalog? models = null,
        ITextureSource? textures = null)
    {
        var root = new Node3D { Name = "Triggers" };
        var teleports = new List<Teleport>();
        var destinations = new List<TeleDest>();
        var jumpPads = new List<JumpPad>();
        var skinCache = new Dictionary<string, Material>();

        for (int i = 0; i < map.Entities.Count; i++)
        {
            var e = map.Entities[i];
            switch (e.Type)
            {
                case EntityType.Teleport:
                    string? modelName = ResolveModel(e.Attr2, catalogue);
                    var modelInfo = modelName != null ? models?.Get(modelName) : null;
                    var teleport = new Teleport
                    {
                        Name = $"Teleport_{i}",
                        Position = e.Position,
                        BasePosition = e.Position,
                        Tag = e.Attr1,
                        ModelIndex = e.Attr2,
                        Hook = e.Attr3,
                        Sound = e.Attr4,
                        ModelPath = modelName,
                        GlbPath = modelInfo?.GlbPath,
                        GlbOsPath = modelInfo?.GlbOsPath,
                        ModelScale = modelInfo?.Scale ?? 1.0f,
                        ModelTranslation = modelInfo?.Translation ?? Vector3.Zero,
                        SkinMaterial = ModelSkin.Build(modelInfo, textures, skinCache),
                    };
                    teleports.Add(teleport);
                    root.AddChild(teleport);
                    EntityMarker.Add(teleport, textures);
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
                    EntityFacing.ApplyYaw(dest, e.Attr1);
                    destinations.Add(dest);
                    root.AddChild(dest);
                    EntityMarker.Add(dest, textures, arrow: true);
                    break;

                case EntityType.JumpPad:
                    var pad = new JumpPad
                    {
                        Name = $"JumpPad_{i}",
                        Position = e.Position,
                        Push = Push(e),
                        Sound = e.Attr4,
                    };
                    jumpPads.Add(pad);
                    root.AddChild(pad);
                    EntityMarker.Add(pad, textures);
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

        return (root, jumpPads, teleports);
    }

    public static Node3D Build(
        OgzMap map,
        MetadataCatalogue? catalogue = null,
        IModelCatalog? models = null,
        ITextureSource? textures = null)
        => BuildTriggers(map, catalogue, models, textures).Root;

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
