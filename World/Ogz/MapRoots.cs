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
///     <b><c>assets/packages/</c></b> — the project's own converted copies. Maps
///     normalised to v32+ by <c>tools/ogztool/convert-all-maps.sh</c> land here
///     (see docs/ogz-import.md). This is the canonical tree: it always holds the
///     newest importable format, so it wins over the dump.
///   </description></item>
///   <item><description>
///     <b><c>$SAUER_REF/packages/</c></b> — the read-only reference dump. Used for
///     maps already at v32+ (the majority) and as a fallback when a map has not
///     been converted into <c>assets/packages/</c>.
///   </description></item>
/// </list>
///
/// Every caller that reads an OGZ should go through here rather than assembling
/// a path by hand, so the priority lives in one place.
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

    /// <summary>Project-owned converted maps (<c>res://assets/packages</c>), as an OS path.</summary>
    public static string ProjectPackages => ProjectSettings.GlobalizePath("res://assets/packages");

    /// <summary>Reference dump packages (<c>$SAUER_REF/packages</c>), as an OS path.</summary>
    public static string ReferencePackages => Path.Combine(ReferenceRoot, "packages");

    /// <summary>Candidates for a package-relative map path, in priority order.</summary>
    public static string[] CandidatePaths(string relative)
    {
        relative = relative.Replace('\\', '/').TrimStart('/');
        return new[]
        {
            Path.Combine(ProjectPackages, relative),
            Path.Combine(ReferencePackages, relative),
        };
    }

    /// <summary>
    /// Resolve a map to an OGZ path. <paramref name="map"/> may be:
    /// an absolute path (returned as-is), a package-relative path
    /// (<c>base/church51.ogz</c>), or a bare name (<c>church51</c>, resolved to
    /// <c>base/church51.ogz</c>). Returns the first candidate that exists, else
    /// the reference-dump path so the caller's not-found error names the dump.
    /// </summary>
    public static string Resolve(string map)
    {
        if (Path.IsPathRooted(map)) return map;

        string relative = Normalize(map);
        foreach (var candidate in CandidatePaths(relative))
            if (File.Exists(candidate)) return candidate;

        // Nothing exists: return the canonical (project) path if the directory is
        // present, otherwise the dump path — either way a stable, explainable path.
        return Directory.Exists(ProjectPackages)
            ? Path.Combine(ProjectPackages, relative)
            : Path.Combine(ReferencePackages, relative);
    }

    /// <summary>Normalise a bare name or relative path to a package-relative *.ogz path.</summary>
    public static string Normalize(string map)
    {
        map = map.Replace('\\', '/').TrimStart('/');
        if (!map.EndsWith(".ogz", StringComparison.OrdinalIgnoreCase)) map += ".ogz";
        if (!map.Contains('/')) map = "base/" + map;
        return map;
    }
}
