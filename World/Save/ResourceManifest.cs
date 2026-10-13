using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

namespace SauerbratenRevival.World.Save;

/// <summary>
/// The resource manifest stored alongside a native map (<c>materials/manifest.res</c>
/// in docs/editor-and-commands.md §7; the "material manifest" of docs/ogz-import.md).
/// It is what makes a converted map **self-describing**: the octree stores only
/// integer vslot indices, and this manifest resolves each index to the *full engine
/// path* of every texture, model and sound the map needs.
///
/// Deliberately **index → full path**, never index → index: the metadata
/// catalogue's global slot/model/sound registries are meaningless outside the
/// original engine's load order, so we bake the resolved paths in once (see
/// docs/ogz-import.md, "Metadata JSON").
///
/// Like <see cref="WorldResource"/>, the Resource is a **thin byte holder**: the
/// payload is a canonical <see cref="ManifestData.Encode"/> blob, so it is
/// byte-stable, version-independent of Godot's Resource framing, and the builder /
/// round-trip are testable headless.
/// </summary>
[GlobalClass]
public partial class ResourceManifest : Resource
{
    /// <summary>Script resource path, assigned so <see cref="ResourceSaver"/> writes a proper ext_resource reference.</summary>
    public const string ScriptPath = "res://World/Save/ResourceManifest.cs";

    /// <summary>The encoded manifest blob (see <see cref="ManifestData.Encode"/>).</summary>
    [Export] public byte[] Data { get; set; } = Array.Empty<byte>();

    public static ResourceManifest FromData(ManifestData data)
    {
        byte[] blob = data.Encode();
        var res = new ResourceManifest();
        res.Data = blob;
        AttachScript(res);
        return res;
    }

    /// <summary>Attach this class's own script (see <see cref="WorldResource.AttachScript"/> for why).</summary>
    private static void AttachScript(Resource res)
    {
        var script = GD.Load<Script>(ScriptPath);
        if (script != null) res.SetScript(script);
    }

    public ManifestData ToData() => ManifestData.Decode(Data);
}

/// <summary>
/// One material (a resolved <c>vslot</c>): the texture **paths** for its layers plus
/// the transform/tint the mesher applies. Index-keyed by the octree face texture.
/// A plain C# type (no Godot object) so it serializes into the manifest blob.
/// </summary>
public sealed class MaterialEntry
{
    /// <summary>The octree face texture index (the map's vslot index).</summary>
    public int Index;

    /// <summary>The slot this vslot derives from (informational; the paths are authoritative).</summary>
    public int Slot;

    public float Scale = 1f;
    public int Rotation;
    public int OffsetX, OffsetY;
    public float ColorR = 1, ColorG = 1, ColorB = 1;
    public float GlowR = 1, GlowG = 1, GlowB = 1;
    public float AlphaFront = 0.5f, AlphaBack;

    /// <summary>Full engine path (relative to <c>packages/</c>) of each texture layer; empty = absent.</summary>
    public string DiffusePath = "";
    public string NormalPath = "";
    public string GlowPath = "";

    /// <summary>Engine shader name (e.g. <c>stdworld</c>, <c>bumpworld</c>); informational for now.</summary>
    public string Shader = "";
}

/// <summary>Plain, Godot-free manifest payload — buildable and testable headless.</summary>
public sealed class ManifestData
{
    /// <summary>Bump when the blob layout changes incompatibly.</summary>
    public const uint FormatVersion = 1;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SRMF"); // SauerbratenReVival Manifest

    public readonly List<MaterialEntry> Materials = new();
    public readonly SortedDictionary<int, string> Models = new();
    public readonly SortedDictionary<int, string> Sounds = new();

    /// <summary>Encode to the canonical blob stored in <c>manifest.res</c>.</summary>
    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(FormatVersion);

        w.Write(Materials.Count);
        foreach (var m in Materials)
        {
            w.Write(m.Index);
            w.Write(m.Slot);
            w.Write(m.Scale);
            w.Write(m.Rotation);
            w.Write(m.OffsetX);
            w.Write(m.OffsetY);
            w.Write(m.ColorR); w.Write(m.ColorG); w.Write(m.ColorB);
            w.Write(m.GlowR); w.Write(m.GlowG); w.Write(m.GlowB);
            w.Write(m.AlphaFront);
            w.Write(m.AlphaBack);
            w.Write(m.DiffusePath);
            w.Write(m.NormalPath);
            w.Write(m.GlowPath);
            w.Write(m.Shader);
        }

        w.Write(Models.Count);
        foreach (var kv in Models) { w.Write(kv.Key); w.Write(kv.Value); }

        w.Write(Sounds.Count);
        foreach (var kv in Sounds) { w.Write(kv.Key); w.Write(kv.Value); }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Decode a blob produced by <see cref="Encode"/>. Rejects unknown versions.</summary>
    public static ManifestData Decode(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var magic = r.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !System.Linq.Enumerable.SequenceEqual(magic, Magic))
            throw new InvalidDataException("Not a resource manifest (bad magic).");

        uint version = r.ReadUInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported manifest version {version} (expected {FormatVersion}).");

        var D = new ManifestData();

        int materials = r.ReadInt32();
        for (int i = 0; i < materials; i++)
        {
            D.Materials.Add(new MaterialEntry
            {
                Index = r.ReadInt32(),
                Slot = r.ReadInt32(),
                Scale = r.ReadSingle(),
                Rotation = r.ReadInt32(),
                OffsetX = r.ReadInt32(),
                OffsetY = r.ReadInt32(),
                ColorR = r.ReadSingle(),
                ColorG = r.ReadSingle(),
                ColorB = r.ReadSingle(),
                GlowR = r.ReadSingle(),
                GlowG = r.ReadSingle(),
                GlowB = r.ReadSingle(),
                AlphaFront = r.ReadSingle(),
                AlphaBack = r.ReadSingle(),
                DiffusePath = r.ReadString(),
                NormalPath = r.ReadString(),
                GlowPath = r.ReadString(),
                Shader = r.ReadString(),
            });
        }

        int models = r.ReadInt32();
        for (int i = 0; i < models; i++) D.Models[r.ReadInt32()] = r.ReadString();

        int sounds = r.ReadInt32();
        for (int i = 0; i < sounds; i++) D.Sounds[r.ReadInt32()] = r.ReadString();

        return D;
    }

    /// <summary>Content hash of the semantic fields, for the archive hash / parity.</summary>
    public ulong Hash()
    {
        ulong h = 14695981039346656037UL;
        void Mix(byte b) { h ^= b; h *= 1099511628211UL; }
        foreach (var b in Encode()) Mix(b);
        return h;
    }
}
