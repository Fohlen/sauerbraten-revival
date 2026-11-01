using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Demo;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game.Ctf;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Meshing;
using SauerbratenRevival.World.Models;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.Tracing;

/// <summary>
/// Diagnostic ablation controller and frame-time / CPU profiler.
/// Activated via <c>--ablation*</c> CLI flags or <c>ABLATION_*</c> environment variables.
/// Switches off requested scene components by zeroing their visibility and processing,
/// and records frame stutter statistics and process CPU utilization across demo playback or idle runs.
/// </summary>
public partial class AblationProbe : Node
{
    public static readonly string[] AllComponents =
    {
        "menu",
        "hud",
        "playermodel",
        "hudgun",
        "weaponeffects",
        "particles",
        "lights",
        "envmaps",
        "mapmodels",
        "items",
        "flags",
        "triggers",
        "sounds",
        "worldmesh",
        "worldenv",
        "playerview",
        "worldprocess",
    };

    private HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    private string _label = "baseline";
    private string? _outputPath;
    private bool _quitOnFinish;
    private bool _idleMenu;
    private double _durationSeconds;
    private double _idleAfterSeconds;
    private int? _overrideMaxFps;

    private Main? _main;
    private OgzMapViewer? _viewer;
    private MapWorld? _world;
    private MapPlayer? _player;
    private bool _switchesApplied;
    private int _warmupFramesRemaining = 5;
    private double _firstFrameMs;
    private double _warmupMaxMs;

    private ulong _lastFrameTickUsec;
    private ulong _phaseStartUsec;
    private TimeSpan _phaseStartCpu;
    private long _phaseStartAllocBytes;
    private int _phaseStartGc0;
    private int _phaseStartGc1;
    private int _phaseStartGc2;

    private bool _inPostDemoIdle;
    private double _phaseElapsedSeconds;
    private bool _finished;

    private readonly List<double> _frameDeltasMs = new(8192);
    private readonly List<double> _processTimesMs = new(8192);
    private readonly List<double> _physicsTimesMs = new(8192);
    private readonly List<double> _drawCalls = new(8192);
    private readonly List<double> _primitives = new(8192);
    private readonly List<double> _objects = new(8192);

    private readonly List<Node3D> _forcedHidden3D = new();
    private readonly List<CanvasItem> _forcedHidden2D = new();
    private readonly List<CanvasLayer> _forcedHiddenLayers = new();

    private PhaseSummary? _activeDemoSummary;
    private PhaseSummary? _idleSummary;

    /// <summary>
    /// Inspects CLI arguments and environment variables; attaches an <see cref="AblationProbe"/>
    /// to <paramref name="main"/> only when ablation flags are present.
    /// </summary>
    public static AblationProbe? AttachIfConfigured(Main main)
    {
        var probe = TryParseConfig();
        if (probe == null) return null;

        probe.Name = "AblationProbe";
        probe.ProcessMode = ProcessModeEnum.Always;
        probe.ProcessPriority = 10000;
        probe._main = main;
        main.AddChild(probe);
        return probe;
    }

    public bool IsIdleMenuRequested => _idleMenu;

    private static AblationProbe? TryParseConfig()
    {
        string[] cmdArgs = OS.GetCmdlineArgs();
        string[] userArgs = OS.GetCmdlineUserArgs();
        var allArgs = new List<string>(cmdArgs.Length + userArgs.Length);
        allArgs.AddRange(cmdArgs);
        allArgs.AddRange(userArgs);

        bool enabled = EnvBool("ABLATION_ENABLE");
        string? disableRaw = System.Environment.GetEnvironmentVariable("ABLATION_DISABLE");
        string? onlyRaw = System.Environment.GetEnvironmentVariable("ABLATION_ONLY");
        string? outPath = System.Environment.GetEnvironmentVariable("ABLATION_OUT");
        string? label = System.Environment.GetEnvironmentVariable("ABLATION_LABEL");
        bool quit = EnvBool("ABLATION_QUIT");
        bool idleMenu = EnvBool("ABLATION_IDLE_MENU");
        double duration = EnvDouble("ABLATION_DURATION", 0.0);
        double idleAfter = EnvDouble("ABLATION_IDLE_AFTER", 0.0);
        int? maxFps = EnvInt("ABLATION_MAX_FPS");

        foreach (string arg in allArgs)
        {
            if (arg.Equals("--ablation-enable", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--ablation", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
            }
            else if (TryMatchOption(arg, "--ablation-disable=", out string? val))
            {
                enabled = true;
                disableRaw = string.IsNullOrEmpty(disableRaw) ? val : $"{disableRaw},{val}";
            }
            else if (TryMatchOption(arg, "--ablation-only=", out val))
            {
                enabled = true;
                onlyRaw = val;
            }
            else if (TryMatchOption(arg, "--ablation-out=", out val))
            {
                enabled = true;
                outPath = val;
            }
            else if (TryMatchOption(arg, "--ablation-label=", out val))
            {
                enabled = true;
                label = val;
            }
            else if (arg.Equals("--ablation-quit", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--ablation-quit-on-finish", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
                quit = true;
            }
            else if (arg.Equals("--ablation-idle-menu", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
                idleMenu = true;
            }
            else if (TryMatchOption(arg, "--ablation-duration=", out val) &&
                     double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                enabled = true;
                duration = d;
            }
            else if (TryMatchOption(arg, "--ablation-idle-after=", out val) &&
                     double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double ia))
            {
                enabled = true;
                idleAfter = ia;
            }
            else if (TryMatchOption(arg, "--ablation-max-fps=", out val) &&
                     int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps))
            {
                enabled = true;
                maxFps = fps;
            }
        }

        if (!enabled && string.IsNullOrEmpty(disableRaw) && string.IsNullOrEmpty(onlyRaw) && string.IsNullOrEmpty(outPath))
        {
            return null;
        }

        var probe = new AblationProbe
        {
            _outputPath = outPath,
            _quitOnFinish = quit,
            _idleMenu = idleMenu,
            _durationSeconds = duration,
            _idleAfterSeconds = idleAfter,
            _overrideMaxFps = maxFps,
        };

        if (!string.IsNullOrWhiteSpace(onlyRaw))
        {
            var keep = ParseList(onlyRaw);
            foreach (string comp in AllComponents)
            {
                if (!keep.Contains(comp))
                {
                    probe._disabled.Add(comp);
                }
            }
            probe._label = label ?? $"only:{string.Join("+", keep)}";
        }
        else if (!string.IsNullOrWhiteSpace(disableRaw))
        {
            var dis = ParseList(disableRaw);
            if (dis.Contains("all"))
            {
                foreach (string comp in AllComponents) probe._disabled.Add(comp);
                foreach (string extra in dis)
                {
                    if (!extra.Equals("all", StringComparison.OrdinalIgnoreCase))
                        probe._disabled.Add(extra);
                }
            }
            else if (!dis.Contains("none"))
            {
                foreach (string comp in dis) probe._disabled.Add(comp);
            }
            probe._label = label ?? (probe._disabled.Count == 0 ? "baseline" : $"no-{string.Join("-", probe._disabled)}");
        }
        else
        {
            probe._label = label ?? (idleMenu ? "menu-idle" : "baseline");
        }

        return probe;
    }

    public override void _Ready()
    {
        if (_overrideMaxFps.HasValue)
        {
            Engine.MaxFps = _overrideMaxFps.Value;
        }
        GD.Print($"[ablation] initialized label='{_label}' disabled=[{string.Join(", ", _disabled)}] duration={_durationSeconds:F1}s idleAfter={_idleAfterSeconds:F1}s quit={_quitOnFinish}");
    }

    public override void _Process(double delta)
    {
        if (_finished) return;

        ulong nowUsec = Time.GetTicksUsec();

        if (!_switchesApplied)
        {
            if (!TryDiscoverAndApplySwitches())
            {
                return;
            }
            _switchesApplied = true;
            _lastFrameTickUsec = nowUsec;
            return;
        }

        EnforceDisabledState();

        double frameMs = (nowUsec - _lastFrameTickUsec) / 1000.0;
        _lastFrameTickUsec = nowUsec;

        if (_warmupFramesRemaining > 0)
        {
            if (_firstFrameMs <= 0.0) _firstFrameMs = frameMs;
            if (frameMs > _warmupMaxMs) _warmupMaxMs = frameMs;
            _warmupFramesRemaining--;
            if (_warmupFramesRemaining == 0)
            {
                BeginMeasurementPhase(nowUsec);
            }
            return;
        }

        RecordFrameSample(frameMs);
        _phaseElapsedSeconds = (nowUsec - _phaseStartUsec) / 1_000_000.0;

        bool demoDone = !_idleMenu && !_inPostDemoIdle && _viewer?.ActiveReplayer is { IsFinished: true };
        bool durationDone = _durationSeconds > 0.0 && _phaseElapsedSeconds >= _durationSeconds;
        bool idleAfterDone = _inPostDemoIdle && _phaseElapsedSeconds >= _idleAfterSeconds;

        if (!_inPostDemoIdle && (demoDone || durationDone))
        {
            _activeDemoSummary = CompleteCurrentPhase(_idleMenu ? "menu_idle" : "active_demo", nowUsec);
            if (_idleAfterSeconds > 0.0 && !_idleMenu)
            {
                _inPostDemoIdle = true;
                GD.Print($"[ablation] active demo finished; measuring post-demo idle for {_idleAfterSeconds:F1}s...");
                BeginMeasurementPhase(nowUsec);
                return;
            }
            FinishAndReport();
        }
        else if (_inPostDemoIdle && idleAfterDone)
        {
            _idleSummary = CompleteCurrentPhase("post_demo_idle", nowUsec);
            FinishAndReport();
        }
    }

    private bool TryDiscoverAndApplySwitches()
    {
        if (_main == null) return false;

        var menu = _main.GetNodeOrNull<Node>("MainMenu");
        if (_idleMenu)
        {
            if (menu == null) return false;
            if (_disabled.Contains("menu"))
            {
                DisableNodeZero(menu);
            }
            return true;
        }

        var gameHost = _main.GetNodeOrNull<Node>("GameHost");
        if (gameHost == null || gameHost.GetChildCount() == 0) return false;

        _viewer = gameHost.GetChild(0) as OgzMapViewer;
        if (_viewer == null || _viewer.World == null || !_viewer.World.Built) return false;

        _world = _viewer.World;
        _player = _world.LocalPlayer ?? _world.GetNodeOrNull<MapPlayer>("Player");

        // 1. MainMenu
        if (_disabled.Contains("menu") && menu != null)
        {
            DisableNodeZero(menu);
        }

        // 2. HUD (HudCanvas & HudController)
        if (_disabled.Contains("hud"))
        {
            var hudCanvas = _viewer.GetNodeOrNull<CanvasLayer>("HudCanvas");
            if (hudCanvas != null)
            {
                if (hudCanvas.GetNodeOrNull<HudController>("HUD") is { } hud)
                {
                    hud.Player = null;
                }
                DisableNodeZero(hudCanvas);
            }
        }

        // 3. PlayerModel (3rd-person skeletal mesh + AnimationPlayer)
        if (_disabled.Contains("playermodel"))
        {
            var pm = _player?.PlayerModel ?? _world.GetNodeOrNull<PlayerModel>("PlayerModel");
            if (_player != null)
            {
                _player.PlayerModel = null;
                _player.HidePlaceholderBody = true;
            }
            if (pm != null) DisableNodeZero(pm);
            if (_player?.GetNodeOrNull<Node3D>("Body") is { } body) DisableNodeZero(body);
        }

        // 4. Hudgun (1st-person skeletal hands/weapon + AnimationPlayer)
        if (_disabled.Contains("hudgun"))
        {
            var hudgun = _player?.Hudgun ?? _player?.GetNodeOrNull<Node3D>("Camera3D/Hudgun");
            if (_player != null)
            {
                _player.Hudgun = null;
            }
            if (hudgun != null) DisableNodeZero(hudgun);
        }

        // 5. WeaponEffects (tracers, decals, rocket visuals, explosions)
        if (_disabled.Contains("weaponeffects"))
        {
            var effects = _world.WeaponEffects ?? _world.GetNodeOrNull<Node3D>("WeaponEffects");
            _world.WeaponEffects = null;
            if (_player != null) _player.WeaponEffects = null;
            if (effects != null) DisableNodeZero(effects);
        }

        // 6. Particles (GpuParticles3D emitters)
        if (_disabled.Contains("particles"))
        {
            if (_world.GetNodeOrNull<Node3D>("Particles") is { } particles)
                DisableNodeZero(particles);
        }

        // 7. Lights (OmniLight3D / SpotLight3D)
        if (_disabled.Contains("lights"))
        {
            if (_world.GetNodeOrNull<Node3D>("Lights") is { } lights)
                DisableNodeZero(lights);
        }

        // 8. EnvMaps (ReflectionProbe)
        if (_disabled.Contains("envmaps"))
        {
            if (_world.EnvMaps != null)
                DisableNodeZero(_world.EnvMaps);
            else if (_world.GetNodeOrNull<Node3D>("EnvMaps") is { } envMaps)
                DisableNodeZero(envMaps);
        }

        // 9. MapModels (static glTF models)
        if (_disabled.Contains("mapmodels"))
        {
            if (_world.GetNodeOrNull<Node3D>("MapModels") is { } mapModels)
                DisableNodeZero(mapModels);
        }

        // 10. Items (pickup models + bobbing/spinning)
        if (_disabled.Contains("items"))
        {
            if (_world.Items != null)
            {
                DisableNodeZero(_world.Items);
            }
        }

        // 11. Flags (CTF flag models + FlagNode.UpdateVisual)
        if (_disabled.Contains("flags"))
        {
            if (_world.GetNodeOrNull<Node3D>("Flags") is { } flagsRoot)
                DisableNodeZero(flagsRoot);
        }

        // 12. Triggers, ModeEntities, Monsters, PlayerStarts
        if (_disabled.Contains("triggers"))
        {
            if (_world.GetNodeOrNull<Node3D>("Triggers") is { } triggers)
                DisableNodeZero(triggers);
            if (_world.GetNodeOrNull<Node3D>("ModeEntities") is { } modeEnts)
                DisableNodeZero(modeEnts);
            if (_world.GetNodeOrNull<Node3D>("Monsters") is { } monsters)
                DisableNodeZero(monsters);
            if (_world.GetNodeOrNull<Node3D>("PlayerStarts") is { } starts)
                DisableNodeZero(starts);

            if (_world.Teleports != null)
            {
                for (int i = 0; i < _world.Teleports.Count; i++)
                {
                    _world.Teleports[i].ModelIndex = -1;
                    DisableNodeZero(_world.Teleports[i]);
                }
            }
        }

        // 13. Sounds (ambient map sounds + player audio players)
        if (_disabled.Contains("sounds"))
        {
            if (_world.GetNodeOrNull<Node3D>("Sounds") is { } sounds)
                DisableNodeZero(sounds);
            if (_player != null)
            {
                if (_player.AudioPlayer != null) DisableNodeZero(_player.AudioPlayer);
                if (_player.AudioPlayer2D != null) DisableNodeZero(_player.AudioPlayer2D);
                _player.AudioPlayer = null;
                _player.AudioPlayer2D = null;
            }
            GameAudio.Apply(false, 0);
        }

        // 14. WorldMesh (WorldChunkRenderer + water/lava/glass Material_* surfaces)
        if (_disabled.Contains("worldmesh"))
        {
            if (_world.Renderer != null)
            {
                DisableNodeZero(_world.Renderer);
            }
            foreach (Node child in _world.GetChildren())
            {
                if (child is MeshInstance3D mi && ((string)child.Name).StartsWith("Material_", StringComparison.Ordinal))
                {
                    DisableNodeZero(child);
                }
            }
        }

        // 15. WorldEnvironment + Sun
        if (_disabled.Contains("worldenv"))
        {
            if (_viewer.GetNodeOrNull<WorldEnvironment>("WorldEnvironment") is { } we)
            {
                we.Environment = null;
                DisableNodeZero(we);
            }
            if (_world.GetNodeOrNull<Node3D>("Sun") is { } sun)
            {
                DisableNodeZero(sun);
            }
        }

        // 16. PlayerView (per-frame presentation sync on MapPlayer & PlayerView)
        if (_disabled.Contains("playerview") && _player != null)
        {
            _player.SetProcess(false);
            if (_player.GetNodeOrNull<PlayerView>("PlayerView") is { } pv)
            {
                pv.SetProcess(false);
                pv.ProcessMode = ProcessModeEnum.Disabled;
            }
        }

        // 17. MapWorld._Process (teleports, flags, items visual loop)
        if (_disabled.Contains("worldprocess") ||
            (_disabled.Contains("items") && _disabled.Contains("flags") && _disabled.Contains("triggers")))
        {
            _world.SetProcess(false);
        }

        // 18. MapWorld._PhysicsProcess (fixed 5 ms WorldSimulation step)
        if (_disabled.Contains("worldphysics"))
        {
            _world.SetPhysicsProcess(false);
        }

        GD.Print($"[ablation] applied switches: disabled={_disabled.Count} components");
        return true;
    }

    /// <summary>
    /// Recursively turns off a node and all of its descendants by setting visibility to false
    /// and disabling all processing, animations, particles, viewports, and audio streams.
    /// </summary>
    public void DisableNodeZero(Node root)
    {
        DisableNodeRecursive(root);
        if (root is Node3D n3d) _forcedHidden3D.Add(n3d);
        else if (root is CanvasItem ci) _forcedHidden2D.Add(ci);
        else if (root is CanvasLayer cl) _forcedHiddenLayers.Add(cl);
    }

    private static void DisableNodeRecursive(Node node)
    {
        node.ProcessMode = ProcessModeEnum.Disabled;
        node.SetProcess(false);
        node.SetPhysicsProcess(false);
        node.SetProcessInput(false);
        node.SetProcessUnhandledInput(false);
        node.SetProcessUnhandledKeyInput(false);
        node.SetProcessShortcutInput(false);

        if (node is Node3D n3d)
        {
            n3d.Visible = false;
        }
        else if (node is CanvasItem ci)
        {
            ci.Visible = false;
        }
        else if (node is CanvasLayer cl)
        {
            cl.Visible = false;
        }
        else if (node is SubViewport sv)
        {
            sv.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }

        if (node is GpuParticles3D gpuParticles)
        {
            gpuParticles.Emitting = false;
        }
        else if (node is CpuParticles3D cpuParticles)
        {
            cpuParticles.Emitting = false;
        }
        else if (node is AnimationPlayer animPlayer)
        {
            animPlayer.Stop();
            animPlayer.Active = false;
        }
        else if (node is AudioStreamPlayer audio2d)
        {
            audio2d.Stop();
            audio2d.StreamPaused = true;
        }
        else if (node is AudioStreamPlayer3D audio3d)
        {
            audio3d.Stop();
            audio3d.StreamPaused = true;
        }

        foreach (Node child in node.GetChildren())
        {
            DisableNodeRecursive(child);
        }
    }

    private void EnforceDisabledState()
    {
        for (int i = 0; i < _forcedHidden3D.Count; i++)
        {
            var n = _forcedHidden3D[i];
            if (IsInstanceValid(n) && n.Visible) n.Visible = false;
        }
        for (int i = 0; i < _forcedHidden2D.Count; i++)
        {
            var c = _forcedHidden2D[i];
            if (IsInstanceValid(c) && c.Visible) c.Visible = false;
        }
        for (int i = 0; i < _forcedHiddenLayers.Count; i++)
        {
            var l = _forcedHiddenLayers[i];
            if (IsInstanceValid(l) && l.Visible) l.Visible = false;
        }
    }

    private void BeginMeasurementPhase(ulong nowUsec)
    {
        _frameDeltasMs.Clear();
        _processTimesMs.Clear();
        _physicsTimesMs.Clear();
        _drawCalls.Clear();
        _primitives.Clear();
        _objects.Clear();

        _phaseStartUsec = nowUsec;
        _lastFrameTickUsec = nowUsec;
        _phaseElapsedSeconds = 0.0;

        using var proc = Process.GetCurrentProcess();
        _phaseStartCpu = proc.TotalProcessorTime;
        _phaseStartAllocBytes = GC.GetTotalAllocatedBytes(false);
        _phaseStartGc0 = GC.CollectionCount(0);
        _phaseStartGc1 = GC.CollectionCount(1);
        _phaseStartGc2 = GC.CollectionCount(2);
    }

    private void RecordFrameSample(double frameMs)
    {
        _frameDeltasMs.Add(frameMs);
        _processTimesMs.Add(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0);
        _physicsTimesMs.Add(Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0);
        _drawCalls.Add(Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame));
        _primitives.Add(Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame));
        _objects.Add(Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame));
    }

    private PhaseSummary CompleteCurrentPhase(string phaseName, ulong nowUsec)
    {
        using var proc = Process.GetCurrentProcess();
        TimeSpan cpuDelta = proc.TotalProcessorTime - _phaseStartCpu;
        double wallSeconds = Math.Max(1e-6, (nowUsec - _phaseStartUsec) / 1_000_000.0);
        long allocDelta = GC.GetTotalAllocatedBytes(false) - _phaseStartAllocBytes;
        int gc0 = GC.CollectionCount(0) - _phaseStartGc0;
        int gc1 = GC.CollectionCount(1) - _phaseStartGc1;
        int gc2 = GC.CollectionCount(2) - _phaseStartGc2;

        int n = _frameDeltasMs.Count;
        var sorted = new List<double>(_frameDeltasMs);
        sorted.Sort();

        var jitters = new List<double>(Math.Max(0, n - 1));
        int over8_3 = 0;
        int over16_7 = 0;
        int over33_3 = 0;
        double excess16_7 = 0.0;
        double sumMs = 0.0;

        for (int i = 0; i < n; i++)
        {
            double dt = _frameDeltasMs[i];
            sumMs += dt;
            if (dt > 8.333) over8_3++;
            if (dt > 16.667)
            {
                over16_7++;
                excess16_7 += dt - 16.667;
            }
            if (dt > 33.333) over33_3++;
            if (i > 0) jitters.Add(Math.Abs(dt - _frameDeltasMs[i - 1]));
        }
        jitters.Sort();

        double meanMs = n > 0 ? sumMs / n : 0.0;
        double fps = wallSeconds > 0 ? n / wallSeconds : 0.0;
        double cpuTotalMs = cpuDelta.TotalMilliseconds;
        double cpuPercent = (cpuTotalMs / (wallSeconds * 1000.0)) * 100.0;
        double cpuMsPerFrame = n > 0 ? cpuTotalMs / n : 0.0;

        var summary = new PhaseSummary
        {
            Phase = phaseName,
            Frames = n,
            WallSeconds = Math.Round(wallSeconds, 4),
            EffectiveFps = Math.Round(fps, 2),
            MeanMs = Math.Round(meanMs, 3),
            P50Ms = Math.Round(Percentile(sorted, 0.50), 3),
            P90Ms = Math.Round(Percentile(sorted, 0.90), 3),
            P95Ms = Math.Round(Percentile(sorted, 0.95), 3),
            P99Ms = Math.Round(Percentile(sorted, 0.99), 3),
            P999Ms = Math.Round(Percentile(sorted, 0.999), 3),
            MaxMs = Math.Round(n > 0 ? sorted[^1] : 0.0, 3),
            JitterP95Ms = Math.Round(Percentile(jitters, 0.95), 3),
            JitterP99Ms = Math.Round(Percentile(jitters, 0.99), 3),
            JitterMaxMs = Math.Round(jitters.Count > 0 ? jitters[^1] : 0.0, 3),
            FramesOver8_3Ms = over8_3,
            FramesOver16_7Ms = over16_7,
            FramesOver33_3Ms = over33_3,
            ExcessStutterMs = Math.Round(excess16_7, 3),
            CpuTotalMs = Math.Round(cpuTotalMs, 2),
            CpuPercent = Math.Round(cpuPercent, 2),
            CpuMsPerFrame = Math.Round(cpuMsPerFrame, 3),
            ProcessMsMean = Math.Round(Mean(_processTimesMs), 3),
            ProcessMsMax = Math.Round(Max(_processTimesMs), 3),
            PhysicsMsMean = Math.Round(Mean(_physicsTimesMs), 3),
            PhysicsMsMax = Math.Round(Max(_physicsTimesMs), 3),
            DrawCallsMean = Math.Round(Mean(_drawCalls), 1),
            PrimitivesMean = Math.Round(Mean(_primitives), 1),
            ObjectsMean = Math.Round(Mean(_objects), 1),
            AllocKbPerFrame = Math.Round(n > 0 ? (allocDelta / 1024.0) / n : 0.0, 3),
            GcGen0 = gc0,
            GcGen1 = gc1,
            GcGen2 = gc2,
        };

        GD.Print(
            $"[ablation-result] label={_label} phase={phaseName} frames={n} fps={summary.EffectiveFps:F1} " +
            $"mean={summary.MeanMs:F2}ms p50={summary.P50Ms:F2}ms p95={summary.P95Ms:F2}ms p99={summary.P99Ms:F2}ms max={summary.MaxMs:F2}ms " +
            $"jitterP99={summary.JitterP99Ms:F2}ms >8.3ms={over8_3} >16.7ms={over16_7} excess={summary.ExcessStutterMs:F1}ms " +
            $"cpu={summary.CpuPercent:F1}% ({summary.CpuMsPerFrame:F2}ms/f) proc={summary.ProcessMsMean:F2}ms phys={summary.PhysicsMsMean:F2}ms " +
            $"draws={summary.DrawCallsMean:F0} alloc={summary.AllocKbPerFrame:F2}KB/f gc={gc0}/{gc1}/{gc2}");

        return summary;
    }

    private void FinishAndReport()
    {
        _finished = true;

        if (!string.IsNullOrWhiteSpace(_outputPath))
        {
            try
            {
                string fullPath = Path.GetFullPath(_outputPath);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var report = new AblationReport
                {
                    Label = _label,
                    DisabledComponents = _disabled.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
                    FirstFrameMs = Math.Round(_firstFrameMs, 3),
                    WarmupMaxMs = Math.Round(_warmupMaxMs, 3),
                    ActiveDemo = _activeDemoSummary,
                    PostDemoIdle = _idleSummary,
                };

                string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(fullPath, json, Encoding.UTF8);
                GD.Print($"[ablation] wrote report to '{fullPath}'");
            }
            catch (Exception ex)
            {
                GD.PushError($"[ablation] failed to write report to '{_outputPath}': {ex.Message}");
            }
        }

        if (_quitOnFinish)
        {
            GetTree().Quit(0);
        }
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0.0;
        if (p <= 0.0) return sorted[0];
        if (p >= 1.0) return sorted[^1];
        double idx = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        double frac = idx - lo;
        return sorted[lo] * (1.0 - frac) + sorted[hi] * frac;
    }

    private static double Mean(List<double> values)
    {
        if (values.Count == 0) return 0.0;
        double sum = 0.0;
        for (int i = 0; i < values.Count; i++) sum += values[i];
        return sum / values.Count;
    }

    private static double Max(List<double> values)
    {
        if (values.Count == 0) return 0.0;
        double max = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] > max) max = values[i];
        }
        return max;
    }

    private static HashSet<string> ParseList(string raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(part.ToLowerInvariant());
        }
        return set;
    }

    private static bool TryMatchOption(string arg, string prefix, out string? value)
    {
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg.Substring(prefix.Length);
            return true;
        }
        value = null;
        return false;
    }

    private static bool EnvBool(string name)
    {
        string? val = System.Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(val) &&
               (val.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                val.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static double EnvDouble(string name, double fallback)
    {
        string? val = System.Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(val) &&
               double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : fallback;
    }

    private static int? EnvInt(string name)
    {
        string? val = System.Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(val) &&
               int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : null;
    }

    public sealed class AblationReport
    {
        public string Label { get; set; } = "";
        public string[] DisabledComponents { get; set; } = Array.Empty<string>();
        public double FirstFrameMs { get; set; }
        public double WarmupMaxMs { get; set; }
        public PhaseSummary? ActiveDemo { get; set; }
        public PhaseSummary? PostDemoIdle { get; set; }
    }

    public sealed class PhaseSummary
    {
        public string Phase { get; set; } = "";
        public int Frames { get; set; }
        public double WallSeconds { get; set; }
        public double EffectiveFps { get; set; }
        public double MeanMs { get; set; }
        public double P50Ms { get; set; }
        public double P90Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public double P999Ms { get; set; }
        public double MaxMs { get; set; }
        public double JitterP95Ms { get; set; }
        public double JitterP99Ms { get; set; }
        public double JitterMaxMs { get; set; }
        public int FramesOver8_3Ms { get; set; }
        public int FramesOver16_7Ms { get; set; }
        public int FramesOver33_3Ms { get; set; }
        public double ExcessStutterMs { get; set; }
        public double CpuTotalMs { get; set; }
        public double CpuPercent { get; set; }
        public double CpuMsPerFrame { get; set; }
        public double ProcessMsMean { get; set; }
        public double ProcessMsMax { get; set; }
        public double PhysicsMsMean { get; set; }
        public double PhysicsMsMax { get; set; }
        public double DrawCallsMean { get; set; }
        public double PrimitivesMean { get; set; }
        public double ObjectsMean { get; set; }
        public double AllocKbPerFrame { get; set; }
        public int GcGen0 { get; set; }
        public int GcGen1 { get; set; }
        public int GcGen2 { get; set; }
    }
}
