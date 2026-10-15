using System;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Covers persistent key rebinds (<c>user://input.cfg</c>, docs/vision.md §11.1):
/// round-tripping <see cref="InputEvent"/>s through the file, applying them to
/// <see cref="InputMap"/>, capturing a live rebind, and unbinding.
/// </summary>
public class InputBindingsTest : GoTestBase
{
    public InputBindingsTest(Node testScene) : base(testScene) { }

    private static string AbsolutePath => ProjectSettings.GlobalizePath(InputBindings.SettingsPath);

    /// <summary>Snapshot <c>user://input.cfg</c> and restore it when disposed.</summary>
    private sealed class RestoreFile : IDisposable
    {
        private readonly byte[]? _previous = File.Exists(AbsolutePath) ? File.ReadAllBytes(AbsolutePath) : null;

        public void Dispose()
        {
            if (_previous != null) File.WriteAllBytes(AbsolutePath, _previous);
            else if (File.Exists(AbsolutePath)) File.Delete(AbsolutePath);
        }
    }

    private static InputEvent[] SnapshotAction(string action) => InputBindings.CaptureAction(action);

    private static void RestoreAction(string action, InputEvent[] events)
    {
        if (!InputMap.HasAction(action)) return;
        InputMap.ActionEraseEvents(action);
        foreach (var ev in events) InputMap.ActionAddEvent(action, ev);
    }

    private static readonly InputEvent[] KeyJ = { new InputEventKey { PhysicalKeycode = Key.J } };

    [Test]
    public void MissingFileYieldsNoRebinds()
    {
        using var _ = new RestoreFile();
        if (File.Exists(AbsolutePath)) File.Delete(AbsolutePath);
        TestAssert.Equal(0, InputBindings.Load().Actions.Count, "no rebinds when the file is absent");
    }

    [Test]
    public void RoundTripsThroughInputCfg()
    {
        using var _ = new RestoreFile();
        new InputBindings { Actions = new() { ["editor_flip"] = KeyJ } }.Save();

        InputBindings loaded = InputBindings.Load();
        TestAssert.True(loaded.Actions.ContainsKey("editor_flip"), "action persisted");
        TestAssert.Equal(1, loaded.Actions["editor_flip"].Length, "one event persisted");
        var key = loaded.Actions["editor_flip"][0] as InputEventKey;
        TestAssert.True(key != null, "event deserialized as a key event");
        TestAssert.Equal(Key.J, key!.PhysicalKeycode, "keycode persisted");
    }

    [Test]
    public void ApplyPushesRebindsOntoInputMap()
    {
        using var _ = new RestoreFile();
        InputEvent[] original = SnapshotAction("editor_flip");
        try
        {
            new InputBindings { Actions = new() { ["editor_flip"] = KeyJ } }.Save();
            InputBindings.Load().Apply();

            TestAssert.True(new InputEventKey { PhysicalKeycode = Key.J, Pressed = true }.IsActionPressed("editor_flip"),
                "rebound key matches");
            TestAssert.True(!new InputEventKey { PhysicalKeycode = Key.X, Pressed = true }.IsActionPressed("editor_flip"),
                "old key no longer matches");
        }
        finally
        {
            RestoreAction("editor_flip", original);
        }
    }

    [Test]
    public void SaveActionCapturesLiveRebind()
    {
        using var _ = new RestoreFile();
        InputEvent[] original = SnapshotAction("editor_flip");
        try
        {
            InputMap.ActionEraseEvents("editor_flip");
            InputMap.ActionAddEvent("editor_flip", new InputEventKey { PhysicalKeycode = Key.K });
            InputBindings.SaveAction("editor_flip");

            var key = InputBindings.Load().Actions["editor_flip"][0] as InputEventKey;
            TestAssert.Equal(Key.K, key!.PhysicalKeycode, "live rebind captured");
        }
        finally
        {
            RestoreAction("editor_flip", original);
        }
    }

    [Test]
    public void EmptyEventListUnbindsAction()
    {
        using var _ = new RestoreFile();
        InputEvent[] original = SnapshotAction("editor_flip");
        try
        {
            new InputBindings { Actions = new() { ["editor_flip"] = Array.Empty<InputEvent>() } }.Save();
            InputBindings.Load().Apply();
            TestAssert.Equal(0, InputMap.ActionGetEvents("editor_flip").Count, "action is unbound");
        }
        finally
        {
            RestoreAction("editor_flip", original);
        }
    }

    [Test]
    public void UnknownActionsAreIgnoredOnApply()
    {
        using var _ = new RestoreFile();
        new InputBindings { Actions = new() { ["not_a_real_action"] = KeyJ } }.Save();
        InputBindings.Load().Apply(); // must not throw
        TestAssert.True(!InputMap.HasAction("not_a_real_action"), "unknown action is not created");
    }
}
