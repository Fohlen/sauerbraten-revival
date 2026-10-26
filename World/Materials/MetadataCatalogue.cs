using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace SauerbratenRevival.World.Materials;

/// <summary>One texture layer inside a slot definition.</summary>
public sealed class TextureLayer
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>Indexed resource path (for mapmodels and sounds).</summary>
public sealed class IndexedPath
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>One shader parameter (<c>SlotShaderParam</c>) from a slot or vslot definition.</summary>
public sealed class ShaderParam
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("val")]
    public float?[]? Val { get; set; }

    public Vector3 ToVector3(Vector3 fallback = default)
    {
        if (Val is { Length: >= 3 })
        {
            return new Vector3(
                Val[0] ?? fallback.X,
                Val[1] ?? fallback.Y,
                Val[2] ?? fallback.Z);
        }
        return fallback;
    }
}

/// <summary>One <c>slot</c> from the metadata catalogue (a set of texture layers + shader).</summary>
public sealed class CatalogueSlot
{
    /// <summary>Default <c>envscale</c> parameter from Sauerbraten's <c>envworld</c> / <c>bumpenvworld</c> shaders.</summary>
    public static readonly Vector3 DefaultEnvScale = new(0.2f, 0.2f, 0.2f);

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("shader")]
    public string Shader { get; set; } = "";

    [JsonPropertyName("shaderType")]
    public string ShaderType { get; set; } = "";

    [JsonPropertyName("params")]
    public List<ShaderParam>? Params { get; set; }

    /// <summary>True if this slot uses environment cubemap reflections (<c>SHADER_ENVMAP</c>, e.g. <c>envworld</c> or <c>bumpenv*world</c>).</summary>
    [JsonIgnore]
    public bool IsEnvMap =>
        string.Equals(ShaderType, "envmap", StringComparison.OrdinalIgnoreCase) ||
        Shader.Contains("env", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve the <c>envscale</c> RGB reflection tint/intensity for this slot and optional <paramref name="vslot"/> override.
    /// VSlot <c>params</c> take precedence over Slot <c>params</c>, falling back to <see cref="DefaultEnvScale"/>.
    /// </summary>
    public Vector3 ResolveEnvScale(CatalogueVSlot? vslot = null)
    {
        if (vslot?.Params != null)
        {
            for (int i = 0; i < vslot.Params.Count; i++)
            {
                if (string.Equals(vslot.Params[i].Name, "envscale", StringComparison.OrdinalIgnoreCase))
                    return vslot.Params[i].ToVector3(DefaultEnvScale);
            }
        }

        if (Params != null)
        {
            for (int i = 0; i < Params.Count; i++)
            {
                if (string.Equals(Params[i].Name, "envscale", StringComparison.OrdinalIgnoreCase))
                    return Params[i].ToVector3(DefaultEnvScale);
            }
        }

        return DefaultEnvScale;
    }

    [JsonIgnore]
    public string? DiffusePath;

    [JsonIgnore]
    public string? NormalPath;

    [JsonIgnore]
    public string? GlowPath;

    [JsonPropertyName("textures")]
    public List<TextureLayer>? Textures
    {
        get
        {
            var list = new List<TextureLayer>();
            if (DiffusePath != null) list.Add(new TextureLayer { Type = "diffuse", Path = DiffusePath });
            if (NormalPath != null) list.Add(new TextureLayer { Type = "normal", Path = NormalPath });
            if (GlowPath != null) list.Add(new TextureLayer { Type = "glow", Path = GlowPath });
            return list.Count > 0 ? list : null;
        }
        set
        {
            if (value == null) return;
            foreach (var t in value)
            {
                switch (t.Type)
                {
                    case "diffuse": DiffusePath = t.Path; break;
                    case "normal": NormalPath = t.Path; break;
                    case "glow": GlowPath = t.Path; break;
                }
            }
        }
    }
}

/// <summary>One <c>vslot</c> from the metadata catalogue (resolved variant: transform + tint).</summary>
public sealed class CatalogueVSlot
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("slot")]
    public int Slot { get; set; }

    [JsonPropertyName("scale")]
    public float Scale { get; set; } = 1f;

    [JsonPropertyName("rotation")]
    public int Rotation { get; set; }

    [JsonPropertyName("layer")]
    public int Layer { get; set; }

    [JsonIgnore]
    public int OffsetX, OffsetY;

    [JsonIgnore]
    public Vector3 Color = Vector3.One;

    [JsonIgnore]
    public Vector3 Glow = Vector3.One;

    [JsonIgnore]
    public float AlphaFront = 0.5f, AlphaBack;

    [JsonPropertyName("offset")]
    public int[]? OffsetRaw
    {
        get => new[] { OffsetX, OffsetY };
        set
        {
            if (value is { Length: >= 1 }) OffsetX = value[0];
            if (value is { Length: >= 2 }) OffsetY = value[1];
        }
    }

    [JsonPropertyName("color")]
    public float[]? ColorRaw
    {
        get => new[] { Color.X, Color.Y, Color.Z };
        set
        {
            if (value is { Length: >= 3 })
                Color = new Vector3(value[0], value[1], value[2]);
        }
    }

    [JsonPropertyName("glow")]
    public float[]? GlowRaw
    {
        get => new[] { Glow.X, Glow.Y, Glow.Z };
        set
        {
            if (value is { Length: >= 3 })
                Glow = new Vector3(value[0], value[1], value[2]);
        }
    }

    [JsonPropertyName("alpha")]
    public float[]? AlphaRaw
    {
        get => new[] { AlphaFront, AlphaBack };
        set
        {
            if (value is { Length: >= 1 }) AlphaFront = value[0];
            if (value is { Length: >= 2 }) AlphaBack = value[1];
        }
    }

    [JsonPropertyName("params")]
    public List<ShaderParam>? Params { get; set; }
}

/// <summary>Map configuration values (lighting, skybox, etc.) from the metadata catalogue.</summary>
public sealed class MapConfig
{
    [JsonPropertyName("ambient")]
    public int Ambient { get; set; } = 0x191919;

    [JsonPropertyName("skylight")]
    public int Skylight { get; set; } = 0;

    [JsonPropertyName("sunlight")]
    public int Sunlight { get; set; } = 0;

    [JsonPropertyName("sunlightscale")]
    public float SunlightScale { get; set; } = 1.0f;

    [JsonPropertyName("sunlightyaw")]
    public int SunlightYaw { get; set; } = 0;

    [JsonPropertyName("sunlightpitch")]
    public int SunlightPitch { get; set; } = 90;

    [JsonPropertyName("skyboxcolour")]
    public int SkyboxColour { get; set; } = 0xFFFFFF;

    [JsonPropertyName("skybox")]
    public string Skybox { get; set; } = "";
}

/// <summary>
/// The engine-exported metadata catalogue (see tools/ogztool/patches/ogz-metadata.patch):
/// resolved slot/vslot data for one map. Only what the map uses is present.
/// </summary>
public sealed class MetadataCatalogue
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    [JsonIgnore]
    public readonly Dictionary<int, CatalogueSlot> Slots = new();

    [JsonIgnore]
    public readonly Dictionary<int, CatalogueVSlot> VSlots = new();

    [JsonIgnore]
    public readonly Dictionary<int, string> MapModels = new();

    [JsonIgnore]
    public readonly Dictionary<int, string> Sounds = new();

    [JsonPropertyName("config")]
    public MapConfig Config { get; set; } = new();

    [JsonPropertyName("slots")]
    public List<CatalogueSlot>? SerializedSlots
    {
        get => Slots.Values.OrderBy(s => s.Index).ToList();
        set
        {
            if (value == null) return;
            foreach (var s in value) Slots[s.Index] = s;
        }
    }

    [JsonPropertyName("vslots")]
    public List<CatalogueVSlot>? SerializedVSlots
    {
        get => VSlots.Values.OrderBy(v => v.Index).ToList();
        set
        {
            if (value == null) return;
            foreach (var v in value) VSlots[v.Index] = v;
        }
    }

    [JsonPropertyName("mapmodels")]
    public List<IndexedPath>? SerializedMapModels
    {
        get => MapModels.OrderBy(kv => kv.Key).Select(kv => new IndexedPath { Index = kv.Key, Path = kv.Value }).ToList();
        set
        {
            if (value == null) return;
            foreach (var m in value) MapModels[m.Index] = m.Path ?? "";
        }
    }

    [JsonPropertyName("sounds")]
    public List<IndexedPath>? SerializedSounds
    {
        get => Sounds.OrderBy(kv => kv.Key).Select(kv => new IndexedPath { Index = kv.Key, Path = kv.Value }).ToList();
        set
        {
            if (value == null) return;
            foreach (var s in value) Sounds[s.Index] = s.Path ?? "";
        }
    }

    public string? MapModelPath(int index) => MapModels.GetValueOrDefault(index);
    public string? SoundPath(int index) => Sounds.GetValueOrDefault(index);

    public static MetadataCatalogue Load(string path)
    {
        if (!Godot.FileAccess.FileExists(path))
            throw new FileNotFoundException($"Metadata catalogue not found: '{path}'", path);

        using var fa = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (fa == null)
            throw new FileNotFoundException($"Metadata catalogue could not be opened: '{path}'", path);

        ulong len = fa.GetLength();
        if (len == 0)
            throw new InvalidDataException($"Metadata catalogue is empty: '{path}'");

        byte[] bytes = fa.GetBuffer((long)len);
        return Deserialize(bytes);
    }

    public static MetadataCatalogue Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<MetadataCatalogue>(utf8Json, JsonOptions) ?? new();

    public static MetadataCatalogue Deserialize(string json) =>
        JsonSerializer.Deserialize<MetadataCatalogue>(json, JsonOptions) ?? new();

    public static MetadataCatalogue Parse(JsonElement root) =>
        root.Deserialize<MetadataCatalogue>(JsonOptions) ?? new();

    public byte[] SerializeToUtf8Bytes(bool writeIndented = false) =>
        JsonSerializer.SerializeToUtf8Bytes(this, writeIndented ? IndentedOptions : JsonOptions);

    public string SerializeToJson(bool writeIndented = false) =>
        JsonSerializer.Serialize(this, writeIndented ? IndentedOptions : JsonOptions);

    /// <summary>Load the committed catalogue for a map name, or null if there isn't one.</summary>
    public static MetadataCatalogue? TryLoadForMap(string mapName)
    {
        string resPath = $"res://metadata-catalogue/{mapName}.json";
        return Godot.FileAccess.FileExists(resPath) ? Load(resPath) : null;
    }
}
