using Chickensoft.GoDotTest;
using Godot;

namespace SauerbratenRevival.Tests;

/// <summary>Smoke test that the GoDotTest runner is wired up.</summary>
public class SanityTest : GoTestBase
{
    public SanityTest(Node testScene) : base(testScene) { }

    [Test]
    public void RunnerIsAlive() => TestAssert.True(true, "trivially true");
}
