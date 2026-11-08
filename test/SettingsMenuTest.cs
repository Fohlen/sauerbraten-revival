using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Audio;
using SauerbratenRevival.World.Lighting;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Unit tests for <see cref="SettingsMenu"/> and its interaction with <see cref="UserSettings"/>:
/// verifies node resolution, UI initialization from disk settings, live round-trip mutation,
/// restore-to-defaults functionality, and close signal emission.
/// </summary>
public class SettingsMenuTest : GoTestBase
{
    public SettingsMenuTest(Node testScene) : base(testScene) { }

    private SettingsMenu InstantiateSettingsMenu()
    {
        var packed = GD.Load<PackedScene>("res://scenes/settings_menu.tscn");
        TestAssert.True(packed != null, "settings_menu.tscn loads");
        var menu = packed!.Instantiate<SettingsMenu>();
        TestAssert.True(menu != null, "settings_menu.tscn instantiates");
        TestScene.AddChild(menu);
        return menu!;
    }

    [Test]
    public void SceneLoadsAndResolvesAllNodes()
    {
        SettingsMenu menu = InstantiateSettingsMenu();
        try
        {
            TestAssert.True(menu.GetNodeOrNull("%ControlsTab") != null, "ControlsTab resolved");
            TestAssert.True(menu.GetNodeOrNull("%VideoTab") != null, "VideoTab resolved");
            TestAssert.True(menu.GetNodeOrNull("%AudioTab") != null, "AudioTab resolved");
            TestAssert.True(menu.GetNodeOrNull("%SettingsScroll") != null, "SettingsScroll resolved");
            TestAssert.True(menu.GetNodeOrNull("%SensitivitySlider") != null, "SensitivitySlider resolved");
            TestAssert.True(menu.GetNodeOrNull("%ScreenModeOption") != null, "ScreenModeOption resolved");
            TestAssert.True(menu.GetNodeOrNull("%SoundVolumeSlider") != null, "SoundVolumeSlider resolved");
            TestAssert.True(menu.GetNodeOrNull("%TooltipLabel") != null, "TooltipLabel resolved");
            TestAssert.True(menu.GetNodeOrNull("%BackButton") != null, "BackButton resolved");
            TestAssert.True(menu.GetNodeOrNull("%RestoreDefaultsButton") != null, "RestoreDefaultsButton resolved");
            TestAssert.False(menu.Visible, "initially hidden");
        }
        finally
        {
            menu.QueueFree();
        }
    }

    [Test]
    public void OpenPopulatesControlsFromDisk()
    {
        UserSettings backup = UserSettings.Load();
        SettingsMenu menu = InstantiateSettingsMenu();
        try
        {
            (backup with
            {
                Sensitivity = 5.25f,
                InvertMouse = true,
                Vsync = true,
                MaxFps = 144,
                Fov = 110f,
                SoundEnabled = false,
                SoundVolume = 120,
            }).Save();

            menu.Open();

            TestAssert.True(menu.Visible, "menu is visible after Open");

            var sensSlider = menu.GetNode<HSlider>("%SensitivitySlider");
            TestAssert.Near(5.25f, (float)sensSlider.Value, 1e-3f, "sensitivity matches disk");

            var invertCheck = menu.GetNode<CheckBox>("%InvertMouseCheck");
            TestAssert.True(invertCheck.ButtonPressed, "invert mouse matches disk");

            var vsyncCheck = menu.GetNode<CheckBox>("%VsyncCheck");
            TestAssert.True(vsyncCheck.ButtonPressed, "vsync matches disk");

            var maxFpsSlider = menu.GetNode<HSlider>("%MaxFpsSlider");
            TestAssert.Near(144f, (float)maxFpsSlider.Value, 1e-3f, "max fps matches disk");

            var fovSlider = menu.GetNode<HSlider>("%FovSlider");
            TestAssert.Near(110f, (float)fovSlider.Value, 1e-3f, "fov matches disk");

            var soundCheck = menu.GetNode<CheckBox>("%SoundEnabledCheck");
            TestAssert.False(soundCheck.ButtonPressed, "sound enabled matches disk");

            var soundVolSlider = menu.GetNode<HSlider>("%SoundVolumeSlider");
            TestAssert.Near(120f, (float)soundVolSlider.Value, 1e-3f, "sound volume matches disk");
        }
        finally
        {
            backup.Save();
            menu.QueueFree();
        }
    }

    [Test]
    public void ChangingControlMutatesAndSavesSettings()
    {
        UserSettings backup = UserSettings.Load();
        SettingsMenu menu = InstantiateSettingsMenu();
        try
        {
            menu.Open();

            var sensSlider = menu.GetNode<HSlider>("%SensitivitySlider");
            sensSlider.Value = 7.8f;

            UserSettings saved = UserSettings.Load();
            TestAssert.Near(7.8f, saved.Sensitivity, 1e-3f, "mutated sensitivity persisted");

            var invertCheck = menu.GetNode<CheckBox>("%InvertMouseCheck");
            invertCheck.ButtonPressed = !saved.InvertMouse;

            saved = UserSettings.Load();
            TestAssert.Equal(invertCheck.ButtonPressed, saved.InvertMouse, "mutated invert mouse persisted");
        }
        finally
        {
            backup.Save();
            menu.QueueFree();
        }
    }

    [Test]
    public void RestoreDefaultsResetsSettings()
    {
        UserSettings backup = UserSettings.Load();
        SettingsMenu menu = InstantiateSettingsMenu();
        try
        {
            (backup with
            {
                Sensitivity = 12.5f,
                Fov = 75f,
                SoundVolume = 50,
            }).Save();

            menu.Open();
            menu.RestoreDefaults();

            UserSettings loaded = UserSettings.Load();
            var defaults = new UserSettings();

            TestAssert.Near(defaults.Sensitivity, loaded.Sensitivity, 1e-3f, "sensitivity restored to default");
            TestAssert.Near(defaults.Fov, loaded.Fov, 1e-3f, "fov restored to default");
            TestAssert.Equal(defaults.SoundVolume, loaded.SoundVolume, "sound volume restored to default");

            var fovSlider = menu.GetNode<HSlider>("%FovSlider");
            TestAssert.Near(defaults.Fov, (float)fovSlider.Value, 1e-3f, "UI control updated to default");
        }
        finally
        {
            backup.Save();
            menu.QueueFree();
        }
    }

    [Test]
    public void CloseEmitsSignalAndHidesMenu()
    {
        SettingsMenu menu = InstantiateSettingsMenu();
        try
        {
            menu.Open();
            TestAssert.True(menu.Visible, "menu visible");

            bool signalFired = false;
            menu.CloseRequested += () => signalFired = true;

            menu.Close();

            TestAssert.False(menu.Visible, "menu hidden after Close");
            TestAssert.True(signalFired, "CloseRequested emitted");
        }
        finally
        {
            menu.QueueFree();
        }
    }

    [Test]
    public void MainMenuSettingsButtonOpensSettingsMenu()
    {
        var packed = GD.Load<PackedScene>("res://scenes/main_menu.tscn");
        TestAssert.True(packed != null, "main_menu.tscn loads");
        var mainMenu = packed!.Instantiate<MainMenu>()!;
        TestScene.AddChild(mainMenu);
        try
        {
            mainMenu.Open(false);

            var settingsBtn = mainMenu.GetNode<Button>("%SettingsButton");
            var settingsMenu = mainMenu.GetNode<SettingsMenu>("%SettingsMenu");

            TestAssert.False(settingsMenu.Visible, "SettingsMenu initially hidden in MainMenu");

            settingsBtn.EmitSignal(Button.SignalName.Pressed);
            TestAssert.True(settingsMenu.Visible, "SettingsButton opened SettingsMenu");

            var backBtn = settingsMenu.GetNode<Button>("%BackButton");
            backBtn.EmitSignal(Button.SignalName.Pressed);
            TestAssert.False(settingsMenu.Visible, "BackButton closed SettingsMenu");
        }
        finally
        {
            mainMenu.QueueFree();
        }
    }
}
