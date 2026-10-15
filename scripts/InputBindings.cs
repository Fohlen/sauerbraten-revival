using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SauerbratenRevival;

/// <summary>
/// Persistent user key rebinds, stored Godot-natively in <c>user://input.cfg</c>
/// (docs/vision.md §11.1: <see cref="InputMap"/> actions are remappable; the user
/// remaps are a <see cref="ConfigFile"/> under <c>user://</c>; no CubeScript).
///
/// The record maps action name to the <see cref="InputEvent"/>s bound to it.
/// Actions absent from the record keep their <c>project.godot</c> defaults; an
/// action present with an empty array is deliberately unbound.
///
/// <see cref="Apply"/> pushes the record onto <see cref="InputMap"/> (once at
/// startup); <see cref="SaveAction"/> captures one action's live events after a
/// rebind (the hook the settings UI will call).
/// </summary>
public sealed record InputBindings
{
    /// <summary>Config file under <c>user://</c> holding the user's key rebinds.</summary>
    public const string SettingsPath = "user://input.cfg";

    /// <summary>Section holding the rebinds, keyed by action name.</summary>
    public const string Section = "input";

    /// <summary>Saved events per action name. An empty array means the action is unbound.</summary>
    public Dictionary<string, InputEvent[]> Actions { get; init; } = new();

    /// <summary>Read the saved rebinds; a missing file yields an empty record (all defaults).</summary>
    public static InputBindings Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) != Error.Ok) return new InputBindings();

        var actions = new Dictionary<string, InputEvent[]>();
        foreach (string action in cfg.GetSectionKeys(Section))
        {
            var saved = cfg.GetValue(Section, action).AsGodotArray();
            var events = new List<InputEvent>(saved.Count);
            foreach (var value in saved)
                if (value.As<InputEvent>() is { } ev) events.Add(ev);
            actions[action] = events.ToArray();
        }
        return new InputBindings { Actions = actions };
    }

    /// <summary>Push the rebinds onto <see cref="InputMap"/>, replacing each listed action's events.</summary>
    public void Apply()
    {
        foreach (var (action, events) in Actions)
        {
            if (!InputMap.HasAction(action)) continue; // ignore stale/unknown actions
            InputMap.ActionEraseEvents(action);
            foreach (var ev in events) InputMap.ActionAddEvent(action, ev);
        }
    }

    /// <summary>Rewrite <see cref="SettingsPath"/> from the record (drops keys not in it).</summary>
    public void Save()
    {
        var cfg = new ConfigFile();
        foreach (var (action, events) in Actions)
        {
            var array = new Godot.Collections.Array();
            foreach (var ev in events) array.Add(ev);
            cfg.SetValue(Section, action, array);
        }
        cfg.Save(SettingsPath);
    }

    /// <summary>The live events currently bound to <paramref name="action"/> (empty when unknown).</summary>
    public static InputEvent[] CaptureAction(string action) =>
        InputMap.HasAction(action) ? InputMap.ActionGetEvents(action).ToArray() : Array.Empty<InputEvent>();

    /// <summary>
    /// Persist one action's live <see cref="InputMap"/> events (the rebind hook for
    /// the settings UI). Writes an empty entry when the action was unbound.
    /// </summary>
    public static void SaveAction(string action)
    {
        if (!InputMap.HasAction(action)) return;
        InputBindings bindings = Load();
        bindings.Actions[action] = CaptureAction(action);
        bindings.Save();
    }

    /// <summary>Forget all user rebinds; <c>project.godot</c> defaults apply on the next run.</summary>
    public static void Reset()
    {
        if (FileAccess.FileExists(SettingsPath))
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(SettingsPath));
    }
}
