using Godot;

#if DEBUG
using System.Reflection;
using Chickensoft.GoDotTest;
#endif

namespace SauerbratenRevival;

/// <summary>
/// Entry point. In debug builds it lets GoDotTest decide whether to run the
/// unit tests (via <c>--run-tests</c>) or to start the game normally. Release
/// builds always start the game.
/// </summary>
public partial class Main : Node
{
#if DEBUG
    private TestEnvironment? _environment;
#endif

    public override void _Ready()
    {
#if DEBUG
        _environment = TestEnvironment.From(OS.GetCmdlineArgs());
        if (_environment.ShouldRunTests)
        {
            CallDeferred(nameof(RunTests));
            return;
        }
#endif
        GetTree().ChangeSceneToFile("res://scenes/test_level.tscn");
    }

#if DEBUG
    private void RunTests() => _ = GoTest.RunTests(Assembly.GetExecutingAssembly(), this, _environment!);
#endif
}
