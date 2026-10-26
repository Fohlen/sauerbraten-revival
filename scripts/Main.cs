using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Runtime.Formats.Ogz;
using SauerbratenRevival.World.Weapons;

#if DEBUG
using System.Reflection;
using Chickensoft.GoDotTest;
#endif

namespace SauerbratenRevival;

/// <summary>
/// Persistent application shell. In debug builds it lets GoDotTest decide whether
/// to run the unit tests (via <c>--run-tests</c>) or to start the game normally;
/// release builds always start the game.
///
/// The shell owns the two halves of the front-end: <c>GameHost</c> holds the
/// instantiated play scene (or nothing, when only the menu is up) and
/// <see cref="MainMenu"/> is the menu overlay. ESC pauses a running match and
/// shows the menu on top of it; starting a match (re)instantiates the play scene
/// with the menu's selections. See <c>scenes/main_menu.tscn</c>.
/// </summary>
public partial class Main : Node
{
    private Node _gameHost = null!;
    private MainMenu _menu = null!;
    private Node? _game;

#if DEBUG
    private TestEnvironment? _environment;
#endif

    /// <summary>True while a play scene is instantiated under <see cref="_gameHost"/>.</summary>
    private bool InMatch => _game != null && IsInstanceValid(_game) && _game.IsInsideTree();

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
        // The shell must receive ESC while the menu pauses the tree.
        ProcessMode = ProcessModeEnum.Always;

        _gameHost = GetNode<Node>("GameHost");
        _menu = GetNode<MainMenu>("MainMenu");
        _menu.StartMatch += StartMatch;
        _menu.ResumeRequested += ResumeGame;
        _menu.QuitRequested += () => GetTree().Quit();

        // Deferred: the tree is busy adding children during _Ready.
        CallDeferred(nameof(StartGame));
    }

    private void StartGame()
    {
        // User settings are applied once at startup; the world layer reads its own
        // (lighting) settings when it builds.
        UserSettings settings = UserSettings.Load();
        ScreenSettings.Apply(settings);
        GameAudio.Apply(settings.SoundEnabled, settings.SoundVolume);
        InputBindings.Load().Apply();
        ShowMenu();
    }

    /// <summary>
    /// Instantiate the play scene with the menu's choices. The menu is only the
    /// selector; the shell owns the match lifetime, so leaving a match is just
    /// freeing this node.
    /// </summary>
    private void StartMatch(string map, string mode, int team, int playerModel, string playerName)
    {
        EndMatch();

        var packed = GD.Load<PackedScene>("res://scenes/player_scene.tscn");
        if (packed == null)
        {
            GD.PushError("[main] could not load res://scenes/player_scene.tscn");
            return;
        }

        Node game = packed.Instantiate();
        if (game is OgzMapViewer viewer)
        {
            viewer.OgzPath = map;
            viewer.GameMode = mode;
            viewer.PlayerModelIndex = playerModel;
            // EDIT is one of the menu's objectives but a plain mode to the world
            // (`coop_edit`, ModeFlags.Edit). It maps onto the editor's entry point,
            // the same one scenes/editor_scene.tscn uses through its EditMode export:
            // spawn floating with a live EditorController instead of a match player.
            viewer.EditMode = GameModeCatalog.TryGet(mode)?.Has(ModeFlags.Edit) ?? false;
        }
        // Set before the viewer's _Ready runs (on AddChild), so spawn/visuals use it.
        if (game.GetNodeOrNull<MapPlayer>("Player") is { } player) player.Team = team;

        _gameHost.AddChild(game);
        _game = game;
        GD.Print($"[main] match started: map={map} mode={mode} team={team} model={playerModel} name='{playerName}' edit={(game is OgzMapViewer { EditMode: true })}");
        ResumeGame();
    }

    private void EndMatch()
    {
        if (_game != null && IsInstanceValid(_game)) _game.QueueFree();
        _game = null;
    }

    /// <summary>Hide the menu and hand control back to the match (if any).</summary>
    private void ResumeGame()
    {
        _menu.Close();
        GetTree().Paused = false;
        // The player recaptures the mouse on its next click (MapPlayer), matching
        // the engine's front-end/play transition.
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// <summary>Show the menu; pause a running match so it freezes behind the overlay.</summary>
    private void ShowMenu()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GetTree().Paused = InMatch;
        _menu.Open(InMatch);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!@event.IsActionPressed("ui_cancel") || @event.IsEcho()) return;

        if (_menu.Visible)
        {
            // ESC in the menu resumes a paused match (at startup there is none).
            if (InMatch) ResumeGame();
        }
        else
        {
            // ESC in a match opens the menu over the frozen game.
            ShowMenu();
        }

        GetViewport().SetInputAsHandled();
    }

#if DEBUG
    private void RunTests() => _ = GoTest.RunTests(Assembly.GetExecutingAssembly(), this, _environment!);
#endif
}
