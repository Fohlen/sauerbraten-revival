using System.Collections.Generic;
using System.IO;
using Godot;

namespace SauerbratenRevival.World.Materials;

/// <summary>
/// Supplies texture images and their dimensions to the material builder. The dev
/// viewer uses <see cref="DumpTextureSource"/>; the shipped importer will swap in
/// a Godot-resource-backed source (textures imported into the project).
/// </summary>
public interface ITextureSource
{
    Texture2D? Get(string path);
    Vector2I Size(string path);
    Image? GetImage(string path);
}

/// <summary>Loads textures from Godot-imported Resources under <c>res://assets/textures/</c> (see tools/texture-import).</summary>
public sealed class ResourceTextureSource : ITextureSource
{
    private readonly string _root;
    private readonly Dictionary<string, Texture2D?> _textures = new();

    public ResourceTextureSource(string root = "res://assets/textures") => _root = root.TrimEnd('/');

    public Texture2D? Get(string path)
    {
        if (_textures.TryGetValue(path, out var texture)) return texture;
        string res = $"{_root}/{path}";
        texture = ResourceLoader.Exists(res) ? ResourceLoader.Load<Texture2D>(res) : null;
        _textures[path] = texture;
        return texture;
    }

    public Vector2I Size(string path) => Get(path) is { } texture ? (Vector2I)texture.GetSize() : Vector2I.Zero;

    public Image? GetImage(string path) => Get(path)?.GetImage();
}

/// <summary>Reads from the project's imported textures first, falling back to the dump.</summary>
public sealed class FallbackTextureSource : ITextureSource
{
    private readonly ITextureSource _primary, _fallback;

    public FallbackTextureSource(ITextureSource primary, ITextureSource fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public Texture2D? Get(string path) => _primary.Get(path) ?? _fallback.Get(path);

    public Vector2I Size(string path)
    {
        Vector2I size = _primary.Size(path);
        return size != Vector2I.Zero ? size : _fallback.Size(path);
    }

    public Image? GetImage(string path) => _primary.GetImage(path) ?? _fallback.GetImage(path);
}

/// <summary>Loads textures on demand from the reference dump's <c>packages/</c> directory.</summary>
public sealed class DumpTextureSource : ITextureSource
{
    private readonly string _packagesRoot;
    private readonly Dictionary<string, Texture2D?> _textures = new();
    private readonly Dictionary<string, Vector2I> _sizes = new();

    public DumpTextureSource(string referenceRoot) => _packagesRoot = Path.Combine(referenceRoot, "packages");

    public Texture2D? Get(string path)
    {
        if (_textures.TryGetValue(path, out var texture)) return texture;
        var image = LoadImage(path);
        texture = image != null ? ImageTexture.CreateFromImage(image) : null;
        _textures[path] = texture;
        return texture;
    }

    public Vector2I Size(string path)
    {
        if (_sizes.TryGetValue(path, out var size)) return size;
        size = LoadImage(path)?.GetSize() ?? new Vector2I(64, 64);
        _sizes[path] = size;
        return size;
    }

    public Image? GetImage(string path) => LoadImage(path);

    private Image? LoadImage(string path)
    {
        string full = Path.Combine(_packagesRoot, path);
        if (!File.Exists(full)) return null;
        var image = new Image();
        return image.Load(full) == Error.Ok ? image : null;
    }
}
