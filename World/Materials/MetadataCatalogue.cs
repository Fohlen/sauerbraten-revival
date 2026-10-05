using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace SauerbratenRevival.World.Materials;

/// <summary>One <c>slot</c> from the metadata catalogue (a set of texture layers + shader).</summary>
public sealed class CatalogueSlot
{
    public int Index;
    public string? DiffusePath;
    public string? NormalPath;
    public string? GlowPath;
    public string Shader = "";
}

/// <summary>One <c>vslot</c> from the metadata catalogue (resolved variant: transform + tint).</summary>
public sealed class CatalogueVSlot
{
    public int Index;
    public int Slot;
    public float Scale = 1f;
    public int Rotation;
    public int OffsetX, OffsetY;
    public Vector3 Color = Vector3.One;
    public Vector3 Glow = Vector3.One;
    public float AlphaFront = 0.5f, AlphaBack;
}

/// <summary>
/// The engine-exported metadata catalogue (see tools/refharness/patches/ogz-metadata.patch):
/// resolved slot/vslot data for one map. Only what the map uses is present.
/// </summary>
public sealed class MetadataCatalogue
{
    public readonly Dictionary<int, CatalogueSlot> Slots = new();
    public readonly Dictionary<int, CatalogueVSlot> VSlots = new();
    public readonly Dictionary<int, string> MapModels = new();

    public string? MapModelPath(int index) => MapModels.GetValueOrDefault(index);

    public static MetadataCatalogue Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return Parse(doc.RootElement);
    }

    public static MetadataCatalogue Parse(JsonElement root)
    {
        var catalogue = new MetadataCatalogue();

        foreach (var s in root.GetProperty("slots").EnumerateArray())
        {
            var slot = new CatalogueSlot
            {
                Index = s.GetProperty("index").GetInt32(),
                Shader = s.TryGetProperty("shader", out var sh) && sh.ValueKind == JsonValueKind.String
                    ? sh.GetString() ?? ""
                    : "",
            };
            foreach (var t in s.GetProperty("textures").EnumerateArray())
            {
                string? path = t.GetProperty("path").GetString();
                switch (t.GetProperty("type").GetString())
                {
                    case "diffuse": slot.DiffusePath = path; break;
                    case "normal": slot.NormalPath = path; break;
                    case "glow": slot.GlowPath = path; break;
                }
            }
            catalogue.Slots[slot.Index] = slot;
        }

        foreach (var v in root.GetProperty("vslots").EnumerateArray())
        {
            var off = v.GetProperty("offset");
            var color = v.GetProperty("color");
            var glow = v.GetProperty("glow");
            var alpha = v.GetProperty("alpha");
            var vs = new CatalogueVSlot
            {
                Index = v.GetProperty("index").GetInt32(),
                Slot = v.GetProperty("slot").GetInt32(),
                Scale = (float)v.GetProperty("scale").GetDouble(),
                Rotation = v.GetProperty("rotation").GetInt32(),
                OffsetX = off[0].GetInt32(),
                OffsetY = off[1].GetInt32(),
                Color = new Vector3((float)color[0].GetDouble(), (float)color[1].GetDouble(), (float)color[2].GetDouble()),
                Glow = new Vector3((float)glow[0].GetDouble(), (float)glow[1].GetDouble(), (float)glow[2].GetDouble()),
                AlphaFront = (float)alpha[0].GetDouble(),
                AlphaBack = (float)alpha[1].GetDouble(),
            };
            catalogue.VSlots[vs.Index] = vs;
        }

        if (root.TryGetProperty("mapmodels", out var mapModels))
        {
            foreach (var m in mapModels.EnumerateArray())
                catalogue.MapModels[m.GetProperty("index").GetInt32()] = m.GetProperty("path").GetString() ?? "";
        }

        return catalogue;
    }

    /// <summary>Load the committed catalogue for a map name, or null if there isn't one.</summary>
    public static MetadataCatalogue? TryLoadForMap(string mapName)
    {
        string resPath = $"res://metadata-catalogue/{mapName}.json";
        string path = ProjectSettings.GlobalizePath(resPath);
        return File.Exists(path) ? Load(path) : null;
    }
}
