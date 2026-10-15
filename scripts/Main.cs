using Godot;
using SauerbratenRevival.World.Weapons;

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
        // Deferred: the tree is busy adding children during _Ready.
        CallDeferred(nameof(StartGame));
    }

    private void StartGame()
    {
        // User settings are applied once at startup; the world layer reads its own
        // (lighting) settings when it builds.
        UserSettings settings = UserSettings.Load();
        GameAudio.Apply(settings.SoundEnabled, settings.SoundVolume);
        GetTree().ChangeSceneToFile("res://scenes/player_scene.tscn");
    }

#if DEBUG
    private void RunTests() => _ = GoTest.RunTests(Assembly.GetExecutingAssembly(), this, _environment!);
#endif
}
