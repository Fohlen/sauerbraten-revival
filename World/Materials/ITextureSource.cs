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
