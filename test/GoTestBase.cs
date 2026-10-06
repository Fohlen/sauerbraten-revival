using System;
using Chickensoft.GoDotTest;
using Godot;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Base for the suites that create Godot objects (meshes, materials, nodes), so
/// their native resources are released between suites instead of being reported
/// as leaked when GoDotTest force-exits.
/// </summary>
public abstract class GoTestBase : TestClass
{
    protected GoTestBase(Node testScene) : base(testScene) { }

    [CleanupAll]
    public void ReleaseGodotObjects()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
