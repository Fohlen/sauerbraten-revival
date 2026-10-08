using Chickensoft.GoDotTest;
using Godot;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies the editor's keybinds are <see cref="InputMap"/> actions rather than
/// hardcoded keys, so they can be remapped in Project Settings. Defaults match
/// Cube2's <c>editbind</c>s.
/// </summary>
public class EditorInputMapTest : GoTestBase
{
    public EditorInputMapTest(Node testScene) : base(testScene) { }

    [Test]
    public void EditorActionsExist()
    {
        string[] actions =
        {
            "editor_cancel", "editor_flip", "editor_delete", "editor_copy",
            "editor_paste", "editor_cut", "editor_undo", "editor_redo",
            "editor_mod_grid", "editor_mod_face", "editor_mod_corner", "editor_mod_rotate",
            "editor_mod_texture", "editor_mod_attr1", "editor_mod_attr2",
            "editor_mod_attr3", "editor_mod_attr4",
            "editor_grid_shift", "editor_sel_left", "editor_sel_right",
            "editor_sel_fwd", "editor_sel_back", "editor_sel_up", "editor_sel_down",
            "editor_save", "editor_load",
            "editor_allfaces", "editor_selcorners", "editor_remip", "editor_tex",
            "editor_pastehilite", "editor_pushsel_fwd", "editor_pushsel_back",
            "editor_mat_air", "editor_mat_clip", "editor_mat_death",
        };
        foreach (var a in actions)
            TestAssert.True(InputMap.HasAction(a), $"action '{a}' is registered");
    }

    [Test]
    public void EditorActionsAreRemappable()
    {
        // The whole point: rebinding an action changes what it matches, with no code
        // change. Rebind editor_flip to J, fire a J key event, and confirm.
        const string action = "editor_flip";
        TestAssert.True(InputMap.HasAction(action), "precondition");

        var original = InputMap.ActionGetEvents(action);
        InputMap.ActionEraseEvents(action);
        var key = new InputEventKey { PhysicalKeycode = Key.J };
        InputMap.ActionAddEvent(action, key);

        var ev = new InputEventKey { PhysicalKeycode = Key.J, Pressed = true };
        TestAssert.True(ev.IsActionPressed(action), "rebound action matches the new key");
        var x = new InputEventKey { PhysicalKeycode = Key.X, Pressed = true };
        TestAssert.True(!x.IsActionPressed(action), "old key no longer matches");

        // Restore so other tests see the default.
        InputMap.ActionEraseEvents(action);
        foreach (var e in original) InputMap.ActionAddEvent(action, e);
    }
}
