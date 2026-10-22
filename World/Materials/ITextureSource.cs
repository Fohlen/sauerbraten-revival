using System.Collections.Generic;
using System.IO;
using Godot;
using SauerbratenRevival.World.Content;

namespace SauerbratenRevival.World.Materials;

/// <summary>
/// Supplies texture images and their dimensions to the material builder. The dev
/// viewer uses a <see cref="FallbackTextureSource"/> (project Resources, with the
/// reference dump as a fallback); the shipped importer bakes Resources directly.
/// </summary>
public interface ITextureSource
{
    Texture2D? Get(string path);
    Vector2I Size(string path);
    Image? GetImage(string path);
}

/// <summary>
/// Loads project textures: the Godot-imported Resource when present, else the raw
/// image on disk (so a fresh checkout without imports still works). The path root
/// comes from <see cref="ContentRoots.ProjectDir"/> (see tools/texture-import).
/// </summary>
public sealed class ResourceTextureSource : ITextureSource
{
    private readonly string _root;
    private readonly Dictionary<string, Texture2D?> _textures = new();

    public ResourceTextureSource(string? root = null)
        => _root = (root ?? ContentRoots.ProjectDir(ContentKind.Texture)).TrimEnd('/');

    public Texture2D? Get(string path)
    {
        if (_textures.TryGetValue(path, out var texture)) return texture;
        texture = Load($"{_root}/{path}");
        _textures[path] = texture;
        return texture;
    }

    public Vector2I Size(string path) => Get(path) is { } texture ? (Vector2I)texture.GetSize() : Vector2I.Zero;

    public Image? GetImage(string path) => Get(path)?.GetImage();

    private static Texture2D? Load(string resPath)
    {
        if (ResourceLoader.Exists(resPath)) return ResourceLoader.Load<Texture2D>(resPath);

        // Imported resource absent (e.g. a fresh checkout): read the source image.
        string diskPath = ProjectSettings.GlobalizePath(resPath);
        if (!File.Exists(diskPath)) return null;
        var image = new Image();
        return image.Load(diskPath) == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
    }
}

/// <summary>Binds the shared <see cref="ContentRoots"/> order (project Resources, then the reference dump) to the <see cref="ITextureSource"/> API.</summary>
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

/// <summary>Loads textures on demand from the reference dump (see <see cref="ContentRoots.ReferenceTree"/>).</summary>
public sealed class DumpTextureSource : ITextureSource
{
    private readonly string _packagesRoot;
    private readonly Dictionary<string, Texture2D?> _textures = new();
    private readonly Dictionary<string, Vector2I> _sizes = new();

    public DumpTextureSource(string? referenceRoot = null)
        => _packagesRoot = referenceRoot is null
            ? ContentRoots.ReferenceTree(ContentKind.Texture)
            : Path.Combine(referenceRoot, "packages");

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

/// <summary>
/// Builds the production texture source: the one binding of <see cref="ContentRoots"/>'s
/// project-first, dump-second order to the <see cref="ITextureSource"/> API.
/// </summary>
public static class TextureSources
{
    /// <summary>Project textures first, then the reference dump.</summary>
    public static ITextureSource CreateDefault() =>
        new FallbackTextureSource(new ResourceTextureSource(), new DumpTextureSource());
}
