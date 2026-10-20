using System;
using System.IO;
using Godot;

namespace SauerbratenRevival.World.Ogz;

/// <summary>
/// Resolves a map name to an OGZ file, honouring the two source trees the import
/// pipeline uses, in priority order:
///
/// <list type="number">
///   <item><description>
///     <b><c>res://assets/packages/</c></b> — the project's own converted copies.
///     Maps normalised to v32+ by <c>tools/ogztool/convert-all-maps.sh</c> land
///     here (see docs/adr/0006-ogz-import.md). This is the canonical tree: it
///     always holds the newest importable format, so it wins over the dump. It is
///     a Godot virtual path, so it is read from the packed PCK in an export.
///   </description></item>
///   <item><description>
///     <b><c>$SAUER_REF/packages/</c></b> — the read-only reference dump. Used for
///     maps already at v32+ (the majority) and as a fallback when a map has not
///     been converted into <c>assets/packages/</c>.
///   </description></item>
/// </list>
///
/// Every caller that reads an OGZ should go through here rather than assembling
/// a path by hand, so the priority lives in one place. The returned path is
/// openable with <see cref="Godot.FileAccess"/>, whether it is a project
/// <c>res://</c> path or a dump OS path.
/// </summary>
public static class MapRoots
{
    public const string ProjectSettingKey = "sauerbraten/reference_path";
    public const string DefaultReferenceRoot = "/Users/fohlen/Downloads/sauerbraten-code-r6889";

    /// <summary>Reference dump root (<c>$SAUER_REF</c>, else ProjectSetting <c>sauerbraten/reference_path</c>, else the default).</summary>
    public static string ReferenceRoot
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

            return DefaultReferenceRoot;
        }
    }

    /// <summary>
    /// Project-owned converted maps (<c>res://assets/packages</c>), as a Godot
    /// virtual path so it resolves through the VFS and reads from a packed PCK.
    /// </summary>
    public const string ProjectPackages = "res://assets/packages";

    /// <summary>Reference dump packages (<c>$SAUER_REF/packages</c>), as an OS path.</summary>
    public static string ReferencePackages => Path.Combine(ReferenceRoot, "packages");

    /// <summary>
    /// Candidates for a package-relative map path, in priority order: the project
    /// copy (a <c>res://</c> path, readable from a packed PCK) then the dump (an OS
    /// path). <paramref name="relative"/> must already be normalised (see
    /// <see cref="Normalize"/>).
    /// </summary>
    public static string[] CandidatePaths(string relative)
    {
        return new[]
        {
            $"{ProjectPackages}/{relative}",
            Path.Combine(ReferencePackages, relative),
        };
    }

    /// <summary>
    /// Resolve a map to a <see cref="Godot.FileAccess"/>-openable OGZ path.
    /// <paramref name="map"/> may be: an absolute path (returned as-is), a
    /// package-relative path (<c>base/church51.ogz</c>), or a bare name
    /// (<c>church51</c>, resolved to <c>base/church51.ogz</c>). Returns the first
    /// candidate that exists, else the canonical (project) path when its directory
    /// is present, otherwise the reference-dump path so the caller's not-found
    /// error names the dump.
    /// </summary>
    public static string Resolve(string map)
    {
        if (map.IsAbsolutePath()) return map;

        string relative = Normalize(map);
        foreach (var candidate in CandidatePaths(relative))
            if (Godot.FileAccess.FileExists(candidate)) return candidate;

        // Nothing exists: return a stable, explainable path for the error message.
        return DirAccess.DirExistsAbsolute(ProjectPackages)
            ? $"{ProjectPackages}/{relative}"
            : Path.Combine(ReferencePackages, relative);
    }

    /// <summary>
    /// Normalise a bare name or relative path to a package-relative *.ogz path:
    /// forward slashes, no leading slash, a <c>.ogz</c> extension, and a
    /// <c>base/</c> prefix when no package is given. <see cref="StringExtensions.SimplifyPath"/>
    /// also collapses <c>.</c>/<c>..</c> segments.
    /// </summary>
    public static string Normalize(string map)
    {
        map = map.SimplifyPath().TrimStart('/');
        if (!map.EndsWith(".ogz", StringComparison.OrdinalIgnoreCase)) map += ".ogz";
        if (!map.Contains('/')) map = "base/" + map;
        return map;
    }
}
