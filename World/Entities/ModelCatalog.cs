using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>One model's converted output + Cube2 model parameters.</summary>
public sealed class ModelInfo
{
    public string Name = "";

    /// <summary><c>res://</c> path of the imported glTF (used by the editor/game).</summary>
    public string? GlbPath;

    /// <summary>OS path of the glTF on disk (used by tests / as a runtime fallback).</summary>
    public string? GlbOsPath;

    public float Scale = 1.0f;
    public Vector3 Translation;
    public string? Skin;
    public string? Bump;

    /// <summary>Reference-dump-relative source geometry (e.g. <c>models/mrfixit/mrfixit.md5mesh</c>), or null.</summary>
    public string? Source;

    /// <summary>True for skeletal (MD5) models.</summary>
    public bool Skeleton;

    /// <summary>Animation name -> reference-dump-relative <c>.md5anim</c> path (empty for static models).</summary>
    public readonly Dictionary<string, string> Anims = new();

    /// <summary>Animation name -> engine playback fps (anims.cfg); 0/absent = engine default.</summary>
    public readonly Dictionary<string, float> AnimSpeeds = new();
}

/// <summary>
/// The manifest produced by <c>tools/model-import/import_models.py</c>: converted
/// glTF paths plus the Cube2 <c>mdlscale</c>/<c>mdltrans</c>/skin parameters.
/// </summary>
public sealed class ModelCatalog
{
    public readonly Dictionary<string, ModelInfo> Models = new();

    public ModelInfo? Get(string name) => Models.GetValueOrDefault(name);

    public static ModelCatalog? Load(string jsonPath, string glbOsDir, string glbResDir)
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
            {
                string file = glb.GetString()!;
                info.GlbPath = $"{glbResDir.TrimEnd('/')}/{file}";
                info.GlbOsPath = Path.Combine(glbOsDir, file);
            }
            if (v.TryGetProperty("scale", out var scale)) info.Scale = (float)scale.GetDouble();
            if (v.TryGetProperty("skin", out var skin) && skin.ValueKind == JsonValueKind.String)
                info.Skin = skin.GetString();
            if (v.TryGetProperty("bump", out var bump) && bump.ValueKind == JsonValueKind.String)
                info.Bump = bump.GetString();
            if (v.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String)
                info.Source = src.GetString();
            if (v.TryGetProperty("skeleton", out var skel) && skel.ValueKind == JsonValueKind.True)
                info.Skeleton = true;
            if (v.TryGetProperty("anims", out var anims) && anims.ValueKind == JsonValueKind.Object)
                foreach (var a in anims.EnumerateObject())
                    if (a.Value.ValueKind == JsonValueKind.String)
                        info.Anims[a.Name] = a.Value.GetString()!;
            if (v.TryGetProperty("animSpeeds", out var speeds) && speeds.ValueKind == JsonValueKind.Object)
                foreach (var s in speeds.EnumerateObject())
                    if (s.Value.ValueKind == JsonValueKind.Number)
                        info.AnimSpeeds[s.Name] = (float)s.Value.GetDouble();
            if (v.TryGetProperty("trans", out var trans) && trans.ValueKind == JsonValueKind.Array && trans.GetArrayLength() == 3)
                info.Translation = new Vector3((float)trans[0].GetDouble(), (float)trans[1].GetDouble(), (float)trans[2].GetDouble());

            catalog.Models[entry.Name] = info;
        }
        return catalog;
    }
}
