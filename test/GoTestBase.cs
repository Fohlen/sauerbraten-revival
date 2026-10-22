using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Chickensoft.GoDotTest;
using Godot;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Marks a test method or test suite class as skipped during standard test suite runs
/// unless explicitly targeted by name via <c>--run-tests=NAME</c> or enabled by an
/// environment variable (<c>RUN_PROBES=1</c> by default).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipAttribute : Attribute
{
    public string Reason { get; }
    public string? EnvVarOverride { get; set; } = "RUN_PROBES";

    public SkipAttribute(string reason = "") => Reason = reason;
}

/// <summary>
/// Base for the suites that create Godot objects (meshes, materials, nodes), so
/// their native resources are released between suites instead of being reported
/// as leaked when GoDotTest force-exits.
/// </summary>
public abstract class GoTestBase : TestClass
{
    protected GoTestBase(Node testScene) : base(testScene) { }

    /// <summary>
    /// Checks if the calling test or test class is marked with <see cref="SkipAttribute"/>.
    /// Returns true if it should be skipped, logging the reason.
    /// Does not skip if overridden by <see cref="SkipAttribute.EnvVarOverride"/> or if
    /// explicitly targeted via <c>--run-tests</c>.
    /// </summary>
    protected bool ShouldSkip([CallerMemberName] string caller = "")
    {
        var method = GetType().GetMethod(caller);
        var methodSkip = method?.GetCustomAttribute<SkipAttribute>();
        var classSkip = GetType().GetCustomAttribute<SkipAttribute>();
        var skip = methodSkip ?? classSkip;
        if (skip == null) return false;

        if (System.Environment.GetEnvironmentVariable("RUN_PROBES") == "1")
            return false;
        if (!string.IsNullOrEmpty(skip.EnvVarOverride) &&
            System.Environment.GetEnvironmentVariable(skip.EnvVarOverride) == "1")
            return false;

        foreach (string arg in OS.GetCmdlineArgs())
        {
            if (arg.StartsWith("--run-tests="))
            {
                string pattern = arg.Substring("--run-tests=".Length);
                if (pattern != "*" && (pattern.Contains(GetType().Name) || pattern.Contains(caller)))
                    return false;
            }
        }

        GD.Print($"[skip] {GetType().Name}.{caller}: {skip.Reason}");
        return true;
    }

    [SetupAll]
    public void EnableWorldSeedCache()
    {
        // Rebuild the canonical EmptyMap once and clone it per test instead of
        // re-building + re-meshing the whole world for every fixture. See
        // EmptyMap.UseSeedCache.
        SauerbratenRevival.World.Editor.EmptyMap.UseSeedCache = true;
        SauerbratenRevival.World.Ogz.MapWorld.UseMapCache = true;
    }

    [CleanupAll]
    public void ReleaseGodotObjects()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
