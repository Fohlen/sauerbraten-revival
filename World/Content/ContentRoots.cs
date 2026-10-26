using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace SauerbratenRevival.World.Content;

/// <summary>
/// The shipped content types, each of which is resolved through the same two
/// source trees (the project's converted copies and the read-only reference
/// dump). See <see cref="ContentRoots"/>.
/// </summary>
public enum ContentKind
{
    /// <summary>Classic <c>.ogz</c> maps (<c>assets/packages</c> / <c>packages</c>).</summary>
    Map,

    /// <summary>World/model textures (<c>assets/textures</c> / <c>packages</c>).</summary>
    Texture,

    /// <summary>Sound samples (<c>assets/sounds</c> / <c>packages/sounds</c>).</summary>
    Sound,

    /// <summary>Converted glTF models and their manifest (<c>assets/models</c> / <c>packages</c>).</summary>
    Model,
}

/// <summary>
/// The single asset resolver. Every reader of a shipped asset — maps, textures,
/// sounds, models — goes through here instead of assembling a path by hand, so
/// the source-tree priority lives in exactly one place:
///
/// <list type="number">
///   <item><description>
///     <b><c>res://assets/…</c></b> — the project's own converted/imported copies
///     (maps normalised by <c>tools/ogztool</c>, textures by <c>tools/texture-import</c>,
///     models by <c>tools/model-import</c>). These are Godot virtual paths, so they
///     are read from the packed PCK in an export. This is the canonical tree and
///     always wins.
///   </description></item>
///   <item><description>
///     <b><c>$SAUER_REF/packages/…</c></b> — the read-only reference dump, used as a
///     dev-only fallback for anything not imported into the project. Strictly
///     opt-in: the dump is only consulted when a developer configures one (see
///     <see cref="ReferenceRoot"/>) — there is no implicit default location, so an
///     unconfigured checkout never probes a protected folder (macOS prompts for
///     Downloads access the first time a process touches it).
///   </description></item>
/// </list>
///
/// Each <see cref="ContentKind"/> maps to a project subdirectory and (optionally) a
/// subdirectory of the dump's <c>packages/</c> tree; see <see cref="ProjectDir"/>
/// and <see cref="ReferenceDir"/>. The returned paths are openable with
/// <see cref="Godot.FileAccess"/>, whether a project <c>res://</c> path or a dump
/// OS path. Imported Godot Resources additionally test true under
/// <see cref="ResourceLoader.Exists"/>.
/// </summary>
public static class ContentRoots
{
    public const string ProjectSettingKey = "sauerbraten/reference_path";

    /// <summary>Project-owned content root, a Godot virtual path so it reads from a packed PCK.</summary>
    public const string ProjectRoot = "res://assets";

    /// <summary>
    /// Reference dump root (<c>$SAUER_REF</c>, else ProjectSetting
    /// <c>sauerbraten/reference_path</c>), or <c>null</c> when no dump is configured.
    /// Nothing falls back to a guessed location: reading a dump is a developer
    /// opt-in, so a checkout that never sets this never touches one (and never
    /// triggers an OS permission prompt for it).
    /// </summary>
    public static string? ReferenceRoot
    {
        get
        {
            string? env = System.Environment.GetEnvironmentVariable("SAUER_REF");
            if (!string.IsNullOrWhiteSpace(env)) return env;

            if (ProjectSettings.HasSetting(ProjectSettingKey))
            {
                string setting = ProjectSettings.GetSetting(ProjectSettingKey).AsString();
                if (!string.IsNullOrWhiteSpace(setting))
                    return ProjectSettings.GlobalizePath(setting.Trim());
            }

            return null;
        }
    }

    /// <summary>Whether a reference dump is configured at all (see <see cref="ReferenceRoot"/>).</summary>
    public static bool HasReferenceDump => ReferenceRoot != null;

    /// <summary>Project-owned converted maps (<c>res://assets/packages</c>), a Godot virtual path so it reads from a packed PCK.</summary>
    public static string ProjectPackages => $"{ProjectRoot}/packages";

    /// <summary>Reference dump packages (<c>$SAUER_REF/packages</c>), as an OS path, or null when no dump is configured.</summary>
    public static string? ReferencePackages =>
        ReferenceRoot is { } root ? Path.Combine(root, "packages") : null;

    /// <summary>Project subdirectory a kind is imported into (a <c>res://</c> path).</summary>
    public static string ProjectDir(ContentKind kind) => kind switch
    {
        ContentKind.Map => ProjectPackages,
        ContentKind.Texture => $"{ProjectRoot}/textures",
        ContentKind.Sound => $"{ProjectRoot}/sounds",
        ContentKind.Model => $"{ProjectRoot}/models",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Kind subdirectory under the dump's <c>packages/</c> tree (empty for package-relative kinds).</summary>
    public static string ReferenceDir(ContentKind kind) => kind switch
    {
        ContentKind.Sound => "sounds",
        _ => "",
    };

    /// <summary>The project tree a kind resolves against (a <c>res://</c> path).</summary>
    public static string ProjectTree(ContentKind kind) => ProjectDir(kind);

    /// <summary>The reference-dump tree a kind resolves against (an OS path), or null when no dump is configured.</summary>
    public static string? ReferenceTree(ContentKind kind)
    {
        if (ReferencePackages is not { } packages) return null;
        string sub = ReferenceDir(kind);
        return sub.Length == 0 ? packages : Path.Combine(packages, sub);
    }

    /// <summary>The project path for a kind-relative asset (existence not checked).</summary>
    public static string ProjectPath(ContentKind kind, string relative) => $"{ProjectDir(kind)}/{relative}";

    /// <summary>
    /// Candidates for a kind-relative asset, in priority order: the project copy
    /// (a <c>res://</c> path, readable from a packed PCK) then the dump (an OS path,
    /// only when one is configured).
    /// </summary>
    public static string[] CandidatePaths(ContentKind kind, string relative)
    {
        var candidates = new List<string> { ProjectPath(kind, relative) };
        if (ReferenceTree(kind) is { } dump)
            candidates.Add(Path.Combine(dump, relative));
        return candidates.ToArray();
    }

    /// <summary>The first existing candidate for a kind-relative asset, or null.</summary>
    public static string? TryResolve(ContentKind kind, string relative)
    {
        foreach (string candidate in CandidatePaths(kind, relative))
            if (Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>
    /// <see cref="TryResolve(ContentKind, string)"/> for several relative variants
    /// (e.g. a path probed with more than one extension). The whole project tree is
    /// scanned before the dump, so a project variant always beats a dump one.
    /// </summary>
    public static string? TryResolve(ContentKind kind, IEnumerable<string> relatives)
    {
        var trees = new List<string> { ProjectTree(kind) };
        if (ReferenceTree(kind) is { } dump) trees.Add(dump);

        foreach (string tree in trees)
            foreach (string relative in relatives)
            {
                string candidate = $"{tree}/{relative}";
                if (Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>
    /// First existing candidate, else a stable, explainable path for the caller's
    /// not-found error: the project path when its tree exists (or when no dump is
    /// configured), otherwise the dump.
    /// </summary>
    public static string Resolve(ContentKind kind, string relative)
    {
        if (TryResolve(kind, relative) is { } found) return found;
        if (DirAccess.DirExistsAbsolute(ProjectTree(kind))) return ProjectPath(kind, relative);
        return ReferenceTree(kind) is { } dump ? Path.Combine(dump, relative) : ProjectPath(kind, relative);
    }

    /// <summary>
    /// Resolve a map to a <see cref="Godot.FileAccess"/>-openable OGZ path.
    /// <paramref name="map"/> may be: an absolute path (returned as-is), a
    /// package-relative path (<c>base/church51.ogz</c>), or a bare name
    /// (<c>church51</c>).
    /// </summary>
    public static string ResolveMap(string map)
    {
        if (map.IsAbsolutePath()) return map;
        return Resolve(ContentKind.Map, NormalizeMap(map));
    }

    /// <summary>
    /// Normalise a bare name or relative path to a package-relative *.ogz path:
    /// forward slashes, no leading slash, a <c>.ogz</c> extension, and a
    /// <c>base/</c> prefix when no package is given. <see cref="StringExtensions.SimplifyPath"/>
    /// also collapses <c>.</c>/<c>..</c> segments.
    /// </summary>
    public static string NormalizeMap(string map)
    {
        map = map.SimplifyPath().TrimStart('/');
        if (!map.EndsWith(".ogz", StringComparison.OrdinalIgnoreCase)) map += ".ogz";
        if (!map.Contains('/')) map = "base/" + map;
        return map;
    }

    /// <summary>
    /// A file exists if it is a raw file (<c>FileAccess</c>) or an imported Godot
    /// Resource (<c>ResourceLoader</c>). The latter matters for exported builds,
    /// where the source image of an imported texture may be absent from the PCK
    /// while the imported resource is present.
    /// </summary>
    private static bool Exists(string path) =>
        Godot.FileAccess.FileExists(path) || ResourceLoader.Exists(path);
}
