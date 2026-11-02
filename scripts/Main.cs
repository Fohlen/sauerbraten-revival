using System;
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
    private MapLoad? _mapLoad;
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
        _mapLoad = GetNodeOrNull<MapLoad>("MapLoad");
        _menu.StartMatch += StartMatch;
        _menu.ResumeRequested += ResumeGame;
        _menu.QuitRequested += () =>
        {
            EndMatch();
            GetTree().Quit();
        };

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

        string[] rawArgs = OS.GetCmdlineUserArgs().Length > 0 ? OS.GetCmdlineUserArgs() : OS.GetCmdlineArgs();
        var cli = SauerbratenRevival.World.Demo.DemoCommandLine.Parse(rawArgs);
        if (!string.IsNullOrEmpty(cli.DemoPath))
        {
            string? resolved = SauerbratenRevival.World.Content.ContentRoots.TryResolveDemo(cli.DemoPath);
            if (resolved != null)
            {
                try
                {
                    var demo = DemoPackage.Load(resolved);
                    PlayDemo(demo, resolved);
                    return;
                }
                catch (Exception ex)
                {
                    GD.PushError($"[main] Failed to load demo '{cli.DemoPath}' (resolved '{resolved}'): {ex.Message}");
                }
            }
            else
            {
                GD.PushError($"[main] Could not find demo '{cli.DemoPath}' (searched ContentRoots and filesystem)");
            }
        }
        else if (cli.Record || !string.IsNullOrEmpty(cli.Map) || !string.IsNullOrEmpty(cli.Mode))
        {
            string map = !string.IsNullOrWhiteSpace(cli.Map) ? cli.Map : "base/church51.ogz";
            string mode = "ffa";
            if (!string.IsNullOrWhiteSpace(cli.Mode))
            {
                var modeInfo = GameModeCatalog.TryGet(cli.Mode);
                if (modeInfo != null)
                {
                    mode = modeInfo.Id;
                }
                else
                {
                    GD.PushWarning($"[main] Unknown game mode '{cli.Mode}', falling back to '{mode}'");
                }
            }
            StartMatch(map, mode, team: 1, playerModel: 0, playerName: "unnamed");
            return;
        }

        ShowMenu();
    }

    /// <summary>
    /// Play back a recorded demo package directly through the play scene.
    /// </summary>
    private void PlayDemo(DemoPackage demo, string demoPath)
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
            viewer.OgzPath = string.IsNullOrWhiteSpace(demo.MapName) ? "base/church51.ogz" : demo.MapName;
            viewer.GameMode = string.IsNullOrWhiteSpace(demo.GameMode) ? "ffa" : demo.GameMode;
            viewer.EditMode = false;
            viewer.DemoToPlay = demo;
        }

        _gameHost.AddChild(game);
        _game = game;
        GD.Print($"[main] demo playback started: demo='{demoPath}' map='{demo.MapName}' mode='{demo.GameMode}' ({demo.TotalTicks} ticks, {demo.DurationSeconds:F1}s)");
        ResumeGame();
    }

    /// <summary>
    /// Instantiate the play scene with the menu's choices. The menu is only the
    /// selector; the shell owns the match lifetime, so leaving a match is just
    /// freeing this node.
    /// </summary>
    private async void StartMatch(string map, string mode, int team, int playerModel, string playerName)
    {
        EndMatch();

        var packed = GD.Load<PackedScene>("res://scenes/player_scene.tscn");
        if (packed == null)
        {
            GD.PushError("[main] could not load res://scenes/player_scene.tscn");
            return;
        }

        Node game = packed.Instantiate();
        OgzMapViewer? viewer = game as OgzMapViewer;
        if (viewer != null)
        {
            viewer.OgzPath = map;
            viewer.GameMode = mode;
            viewer.PlayerModelIndex = playerModel;
            // EDIT is one of the menu's objectives but a plain mode to the world
            // (`coop_edit`, ModeFlags.Edit). It maps onto the editor's entry point,
            // the same one scenes/editor_scene.tscn uses through its EditMode export:
            // spawn floating with a live EditorController instead of a match player.
            viewer.EditMode = GameModeCatalog.TryGet(mode)?.Has(ModeFlags.Edit) ?? false;

            var cli = SauerbratenRevival.World.Demo.DemoCommandLine.Parse(OS.GetCmdlineUserArgs().Length > 0 ? OS.GetCmdlineUserArgs() : OS.GetCmdlineArgs());
            if (cli.Record)
            {
                viewer.AutoRecordDemo = true;
                viewer.AutoRecordDemoPath = cli.RecordPath ?? "";
            }

            if (!viewer.EditMode && DisplayServer.GetName() != "headless" && _mapLoad != null)
            {
                _menu.Close();
                _mapLoad.StartLoading(map, mode);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                viewer.ProgressCallback = _mapLoad.StepProgressAsync;
                viewer.MatchReady += async () =>
                {
                    await ToSignal(GetTree().CreateTimer(0.1f), SceneTreeTimer.SignalName.Timeout);
                    _mapLoad.Dismiss();
                    GD.Print($"[main] match started: map={map} mode={mode} team={team} model={playerModel} name='{playerName}' edit={viewer.EditMode}");
                    ResumeGame();
                };
            }
        }
        // Set before the viewer's _Ready runs (on AddChild), so spawn/visuals use it.
        if (game.GetNodeOrNull<MapPlayer>("Player") is { } player) player.Team = team;

        _gameHost.AddChild(game);
        _game = game;

        if (viewer == null || viewer.ProgressCallback == null)
        {
            _mapLoad?.Dismiss();
            GD.Print($"[main] match started: map={map} mode={mode} team={team} model={playerModel} name='{playerName}' edit={(viewer?.EditMode ?? false)}");
            ResumeGame();
        }
    }

    private void EndMatch()
    {
        _mapLoad?.Dismiss();
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

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == Window.NotificationWMCloseRequest)
        {
            EndMatch();
        }
    }

#if DEBUG
    private void RunTests() => _ = GoTest.RunTests(Assembly.GetExecutingAssembly(), this, _environment!);
#endif
}
