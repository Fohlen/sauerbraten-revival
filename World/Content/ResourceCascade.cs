using System;
using System.Collections.Generic;
using Godot;

namespace SauerbratenRevival.World.Content;

/// <summary>
/// Identifies an authored or modded game object definition that maps to a typed enum ID and name.
/// </summary>
public interface IResourceDefinition<TEnum> where TEnum : struct, Enum
{
    TEnum Id { get; set; }
    string Name { get; set; }
}

/// <summary>
/// A collection resource containing multiple definitions of <typeparamref name="TDef"/>.
/// </summary>
public interface IResourceCatalog<TDef>
{
    IEnumerable<TDef> Definitions { get; }
}

/// <summary>
/// Implements the ADR 0010 3-tier modding priority cascade:
/// Tier 1: Core Base (catalog file or directory of definitions, falling back to C# defaults)
/// Tier 2: Mod Overrides (res://resources/.../overrides or mounted .pck)
/// Tier 3: User Overrides (user://.../overrides)
/// </summary>
public static class ResourceCascade
{
    /// <summary>
    /// Resolves an integer index from an enum ID, string name, or filename.
    /// Returns -1 if no valid index could be determined.
    /// </summary>
    public static int ResolveIndex<TEnum>(TEnum id, string? name, string? fileName = null) where TEnum : struct, Enum
    {
        int idx = Convert.ToInt32(id);
        if (idx >= 0) return idx;

        string? candidate = !string.IsNullOrWhiteSpace(name)
            ? name
            : (!string.IsNullOrWhiteSpace(fileName) ? System.IO.Path.GetFileNameWithoutExtension(fileName) : null);

        if (!string.IsNullOrEmpty(candidate))
        {
            if (Enum.TryParse<TEnum>(candidate, ignoreCase: true, out var parsed))
                return Convert.ToInt32(parsed);

            string normalized = candidate.Replace("_", "").Replace(" ", "");
            if (Enum.TryParse<TEnum>(normalized, ignoreCase: true, out parsed))
                return Convert.ToInt32(parsed);
        }

        return -1;
    }

    /// <summary>
    /// Applies a definition to the given list, expanding the list if necessary.
    /// </summary>
    public static void ApplyDefinition<TItem, TRes, TEnum>(
        this List<TItem> list,
        TRes def,
        string fileName,
        Func<TRes, TItem> convert,
        Func<TItem>? emptyFactory = null)
        where TEnum : struct, Enum
        where TRes : Resource
    {
        int idx = -1;
        if (def is IResourceDefinition<TEnum> iDef)
        {
            idx = ResolveIndex(iDef.Id, iDef.Name, fileName);
            if (idx >= 0 && Convert.ToInt32(iDef.Id) < 0)
            {
                iDef.Id = (TEnum)Enum.ToObject(typeof(TEnum), idx);
            }
        }

        if (idx >= 0)
        {
            if (emptyFactory != null)
            {
                while (list.Count <= idx)
                    list.Add(emptyFactory());
            }
            else
            {
                while (list.Count <= idx)
                    list.Add(default!);
            }

            list[idx] = convert(def);
        }
    }

    /// <summary>
    /// Applies a resource (either a standalone definition or a catalog container) to the list.
    /// </summary>
    public static void ApplyResource<TItem, TRes, TEnum>(
        this List<TItem> list,
        Resource res,
        string fileName,
        Func<TRes, TItem> convert,
        Func<TItem>? emptyFactory = null)
        where TEnum : struct, Enum
        where TRes : Resource
    {
        if (res is TRes def)
        {
            list.ApplyDefinition<TItem, TRes, TEnum>(def, fileName, convert, emptyFactory);
        }
        else if (res is IResourceCatalog<TRes> catalog)
        {
            foreach (var catDef in catalog.Definitions)
            {
                list.ApplyDefinition<TItem, TRes, TEnum>(catDef, "", convert, emptyFactory);
            }
        }
    }

    /// <summary>
    /// Loads a catalog file from <paramref name="filePath"/> and applies its definitions to the list.
    /// </summary>
    public static void ApplyCatalogFile<TItem, TRes, TEnum>(
        this List<TItem> list,
        string? filePath,
        Func<TRes, TItem> convert,
        Func<TItem>? emptyFactory = null)
        where TEnum : struct, Enum
        where TRes : Resource
    {
        if (string.IsNullOrEmpty(filePath) || !ResourceLoader.Exists(filePath))
            return;

        if (ResourceLoader.Load(filePath) is Resource res)
        {
            list.ApplyResource<TItem, TRes, TEnum>(res, System.IO.Path.GetFileName(filePath), convert, emptyFactory);
        }
    }

    /// <summary>
    /// Scans a directory for .tres files and applies them to the list.
    /// </summary>
    public static void ApplyDirectoryOverrides<TItem, TRes, TEnum>(
        this List<TItem> list,
        string? dir,
        Func<TRes, TItem> convert,
        Func<TItem>? emptyFactory = null)
        where TEnum : struct, Enum
        where TRes : Resource
    {
        if (string.IsNullOrEmpty(dir) || !DirAccess.DirExistsAbsolute(dir))
            return;

        using var da = DirAccess.Open(dir);
        if (da == null) return;

        da.ListDirBegin();
        string fileName = da.GetNext();
        while (!string.IsNullOrEmpty(fileName))
        {
            if (!da.CurrentIsDir() && fileName.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
            {
                string fullPath = $"{dir.TrimEnd('/')}/{fileName}";
                if (ResourceLoader.Load(fullPath) is Resource res)
                {
                    list.ApplyResource<TItem, TRes, TEnum>(res, fileName, convert, emptyFactory);
                }
            }
            fileName = da.GetNext();
        }
    }

    /// <summary>
    /// Loads a baked flat array following the 3-tier cascade:
    /// 1. Seed with defaults
    /// 2. Apply base catalog file or directory
    /// 3. Apply mod overrides directory
    /// 4. Apply user overrides directory
    /// </summary>
    public static TItem[] LoadCascade<TItem, TRes, TEnum>(
        IEnumerable<TItem> defaults,
        string? basePath,
        string? modDir,
        string? userDir,
        Func<TRes, TItem> convert,
        Func<TItem>? emptyFactory = null)
        where TEnum : struct, Enum
        where TRes : Resource
    {
        var list = new List<TItem>(defaults);

        if (!string.IsNullOrEmpty(basePath))
        {
            if (basePath.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
                list.ApplyCatalogFile<TItem, TRes, TEnum>(basePath, convert, emptyFactory);
            else
                list.ApplyDirectoryOverrides<TItem, TRes, TEnum>(basePath, convert, emptyFactory);
        }

        list.ApplyDirectoryOverrides<TItem, TRes, TEnum>(modDir, convert, emptyFactory);
        list.ApplyDirectoryOverrides<TItem, TRes, TEnum>(userDir, convert, emptyFactory);

        return list.ToArray();
    }
}

/// <summary>
/// Abstract base class for typed resource catalogs adhering to the ADR 0010 3-tier cascade architecture.
/// </summary>
public abstract class ResourceCatalogBase<TItem, TRes, TEnum>
    where TEnum : struct, Enum
    where TRes : Resource
{
    public abstract TItem[] Defaults { get; }
    public abstract string BasePath { get; }
    public virtual string? ModOverrideDir => null;
    public virtual string? UserOverrideDir => null;

    protected abstract TItem Convert(TRes res);
    protected virtual TItem CreateEmpty() => default!;

    private TItem[]? _activeTable;
    public TItem[] All => _activeTable ??= Load();
    public int Count => All.Length;

    public virtual void Override(TItem[] table) => _activeTable = table;

    public virtual void Reload(string? basePath = null, string? modDir = null, string? userDir = null)
    {
        _activeTable = Load(basePath, modDir, userDir);
    }

    public virtual TItem[] Load(string? basePath = null, string? modDir = null, string? userDir = null)
    {
        return ResourceCascade.LoadCascade<TItem, TRes, TEnum>(
            Defaults,
            basePath ?? BasePath,
            modDir ?? ModOverrideDir,
            userDir ?? UserOverrideDir,
            Convert,
            CreateEmpty);
    }
}
