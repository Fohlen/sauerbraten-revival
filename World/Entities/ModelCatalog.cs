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

    /// <summary>Alpha test cutoff threshold in [0.0, 1.0] (Cube2 <c>mdlalphatest</c>).</summary>
    public float? AlphaTest;

    /// <summary>True for alpha blend, false for alpha scissor / cutout (Cube2 <c>mdlalphablend</c>).</summary>
    public bool? AlphaBlend;

    /// <summary>True to cull backfaces, false to disable backface culling for 2-sided geometry (Cube2 <c>mdlcullface</c>).</summary>
    public bool? CullFace;

    /// <summary>Reference-dump-relative source geometry (e.g. <c>models/mrfixit/mrfixit.md5mesh</c>), or null.</summary>
    public string? Source;

    /// <summary>True for skeletal (MD5) models.</summary>
    public bool Skeleton;

    /// <summary>Animation name -> reference-dump-relative <c>.md5anim</c> path (empty for static models).</summary>
    public readonly Dictionary<string, string> Anims = new();

    /// <summary>Animation name -> engine playback fps (anims.cfg); 0/absent = engine default.</summary>
    public readonly Dictionary<string, float> AnimSpeeds = new();

    /// <summary>
    /// Named bone tags from <c>md5tag</c> (e.g. <c>tag_weapon</c> -&gt; <c>Weapon</c>),
    /// used to resolve the engine's attachment points onto the baked glb's bones.
    /// </summary>
    public readonly Dictionary<string, string> Tags = new();

    /// <summary>
    /// MD5 animation parts (<c>md5animpart</c>). Part 0 is implicit (the full-body
    /// complement); entry <c>i</c> here describes part <c>i+1</c>'s bone mask. The
    /// engine expands each named bone to its subtree, later parts overriding earlier
    /// ones (skelmodel.h <c>applybonemask</c>).
    /// </summary>
    public readonly List<AnimPartInfo> AnimParts = new();

    /// <summary>
    /// Per-part clip tables (<c>md5anim</c>), index-aligned to part 0 + <see cref="AnimParts"/>.
    /// Only present for models that declare <c>md5animpart</c>.
    /// </summary>
    public readonly List<Dictionary<string, PartAnimInfo>> PartAnims = new();

    /// <summary>View-pitch corrections (<c>md5pitch</c>): per-bone, or part-wide when the bone is empty.</summary>
    public readonly List<PitchInfo> Pitches = new();

    /// <summary>Linked submodels, as used by first-person hudguns.</summary>
    public readonly List<ModelInfo> Parts = new();

    /// <summary>For linked submodels, the parent part and attachment tag.</summary>
    public int LinkParent = -1;
    public string? LinkTag;
}

/// <summary>One MD5 animation part's bone mask (<c>md5animpart</c>).</summary>
public sealed class AnimPartInfo
{
    public readonly List<string> Bones = new();
}

/// <summary>One clip inside an animation part (<c>md5anim</c>): file + engine speed/priority.</summary>
public sealed class PartAnimInfo
{
    public string? File;

    /// <summary>Engine playback fps (skelmodel <c>setanim</c> speed); 0 = engine default (10 fps).</summary>
    public float Fps;

    /// <summary>Per-part selection priority (animmodel.h <c>calcanim</c>); higher wins.</summary>
    public int Priority;
}

/// <summary>One <c>md5pitch</c> entry: bone view-pitch scale/offset with a clamp range.</summary>
public sealed class PitchInfo
{
    public string Bone = "";
    public float Scale;
    public float Offset;
    public float Min;
    public float Max;
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
        if (!Godot.FileAccess.FileExists(jsonPath)) return null;

        using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(jsonPath));
        if (!doc.RootElement.TryGetProperty("models", out var models)) return null;

        var catalog = new ModelCatalog();
        foreach (var entry in models.EnumerateObject())
        {
            var info = ReadInfo(entry.Name, entry.Value, glbOsDir, glbResDir);
            if (entry.Value.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                foreach (var part in parts.EnumerateArray())
                {
                    var partInfo = ReadInfo(entry.Name, part, glbOsDir, glbResDir);
                    if (part.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Object)
                    {
                        if (link.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Number)
                            partInfo.LinkParent = parent.GetInt32();
                        if (link.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.String)
                            partInfo.LinkTag = tag.GetString();
                    }
                    info.Parts.Add(partInfo);
                }
            catalog.Models[entry.Name] = info;
        }
        return catalog;
    }

    private static ModelInfo ReadInfo(string name, JsonElement v, string glbOsDir, string glbResDir)
    {
        var info = new ModelInfo { Name = name };
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
        if (v.TryGetProperty("alphatest", out var at) && at.ValueKind == JsonValueKind.Number)
            info.AlphaTest = (float)at.GetDouble();
        if (v.TryGetProperty("alphablend", out var ab))
        {
            if (ab.ValueKind is JsonValueKind.True or JsonValueKind.False)
                info.AlphaBlend = ab.GetBoolean();
            else if (ab.ValueKind == JsonValueKind.Number)
                info.AlphaBlend = ab.GetInt32() != 0;
        }
        if (v.TryGetProperty("cullface", out var cf))
        {
            if (cf.ValueKind is JsonValueKind.True or JsonValueKind.False)
                info.CullFace = cf.GetBoolean();
            else if (cf.ValueKind == JsonValueKind.Number)
                info.CullFace = cf.GetInt32() != 0;
        }
        if (v.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String)
            info.Source = src.GetString();
        if (v.TryGetProperty("skeleton", out var skel) && skel.ValueKind == JsonValueKind.True)
            info.Skeleton = true;
        if (v.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var t in tags.EnumerateArray())
                if (t.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.String &&
                    t.TryGetProperty("bone", out var bone) && bone.ValueKind == JsonValueKind.String)
                    info.Tags[tag.GetString()!] = bone.GetString()!;
        if (v.TryGetProperty("anims", out var anims) && anims.ValueKind == JsonValueKind.Object)
            foreach (var a in anims.EnumerateObject())
                if (a.Value.ValueKind == JsonValueKind.String)
                    info.Anims[a.Name] = a.Value.GetString()!;
        if (v.TryGetProperty("animSpeeds", out var speeds) && speeds.ValueKind == JsonValueKind.Object)
            foreach (var s in speeds.EnumerateObject())
                if (s.Value.ValueKind == JsonValueKind.Number)
                    info.AnimSpeeds[s.Name] = (float)s.Value.GetDouble();
        if (v.TryGetProperty("animParts", out var animParts) && animParts.ValueKind == JsonValueKind.Array)
            foreach (var p in animParts.EnumerateArray())
            {
                var part = new AnimPartInfo();
                if (p.TryGetProperty("mask", out var mask) && mask.ValueKind == JsonValueKind.Array)
                    foreach (var b in mask.EnumerateArray())
                        if (b.ValueKind == JsonValueKind.String) part.Bones.Add(b.GetString()!);
                info.AnimParts.Add(part);
            }
        if (v.TryGetProperty("partAnims", out var partAnims) && partAnims.ValueKind == JsonValueKind.Array)
            foreach (var p in partAnims.EnumerateArray())
            {
                var table = new Dictionary<string, PartAnimInfo>();
                if (p.ValueKind == JsonValueKind.Object)
                    foreach (var clip in p.EnumerateObject())
                    {
                        var ci = new PartAnimInfo();
                        if (clip.Value.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String)
                            ci.File = file.GetString();
                        if (clip.Value.TryGetProperty("fps", out var fps) && fps.ValueKind == JsonValueKind.Number)
                            ci.Fps = (float)fps.GetDouble();
                        if (clip.Value.TryGetProperty("priority", out var priority) && priority.ValueKind == JsonValueKind.Number)
                            ci.Priority = priority.GetInt32();
                        table[clip.Name] = ci;
                    }
                info.PartAnims.Add(table);
            }
        if (v.TryGetProperty("pitch", out var pitches) && pitches.ValueKind == JsonValueKind.Array)
            foreach (var p in pitches.EnumerateArray())
            {
                var pi = new PitchInfo();
                if (p.TryGetProperty("bone", out var pitchBone) && pitchBone.ValueKind == JsonValueKind.String)
                    pi.Bone = pitchBone.GetString()!;
                if (p.TryGetProperty("scale", out var pitchScale) && pitchScale.ValueKind == JsonValueKind.Number)
                    pi.Scale = (float)pitchScale.GetDouble();
                if (p.TryGetProperty("offset", out var pitchOffset) && pitchOffset.ValueKind == JsonValueKind.Number)
                    pi.Offset = (float)pitchOffset.GetDouble();
                if (p.TryGetProperty("min", out var pitchMin) && pitchMin.ValueKind == JsonValueKind.Number)
                    pi.Min = (float)pitchMin.GetDouble();
                if (p.TryGetProperty("max", out var pitchMax) && pitchMax.ValueKind == JsonValueKind.Number)
                    pi.Max = (float)pitchMax.GetDouble();
                info.Pitches.Add(pi);
            }
        if (v.TryGetProperty("trans", out var trans) && trans.ValueKind == JsonValueKind.Array && trans.GetArrayLength() == 3)
            info.Translation = new Vector3((float)trans[0].GetDouble(), (float)trans[1].GetDouble(), (float)trans[2].GetDouble());
        return info;
    }
}
