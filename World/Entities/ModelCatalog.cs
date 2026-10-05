using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>One model's converted output + Cube2 model parameters.</summary>
public sealed class ModelInfo
{
    public string Name = "";
    public string? GlbPath;
    public float Scale = 1.0f;
    public Vector3 Translation;
    public string? Skin;
    public string? Bump;
}

/// <summary>
/// The manifest produced by <c>tools/model-import/import_models.py</c>: converted
/// glTF paths plus the Cube2 <c>mdlscale</c>/<c>mdltrans</c>/skin parameters.
/// </summary>
public sealed class ModelCatalog
{
    public readonly Dictionary<string, ModelInfo> Models = new();

    public ModelInfo? Get(string name) => Models.GetValueOrDefault(name);

    public static ModelCatalog? Load(string jsonPath, string glbDir)
    {
        if (!File.Exists(jsonPath)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        if (!doc.RootElement.TryGetProperty("models", out var models)) return null;

        var catalog = new ModelCatalog();
        foreach (var entry in models.EnumerateObject())
        {
            var v = entry.Value;
            var info = new ModelInfo { Name = entry.Name };

            if (v.TryGetProperty("glb", out var glb) && glb.ValueKind == JsonValueKind.String)
                info.GlbPath = Path.Combine(glbDir, glb.GetString()!);
            if (v.TryGetProperty("scale", out var scale)) info.Scale = (float)scale.GetDouble();
            if (v.TryGetProperty("skin", out var skin) && skin.ValueKind == JsonValueKind.String)
                info.Skin = skin.GetString();
            if (v.TryGetProperty("bump", out var bump) && bump.ValueKind == JsonValueKind.String)
                info.Bump = bump.GetString();
            if (v.TryGetProperty("trans", out var trans) && trans.ValueKind == JsonValueKind.Array && trans.GetArrayLength() == 3)
                info.Translation = new Vector3((float)trans[0].GetDouble(), (float)trans[1].GetDouble(), (float)trans[2].GetDouble());

            catalog.Models[entry.Name] = info;
        }
        return catalog;
    }
}
