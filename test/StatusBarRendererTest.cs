using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.Tests;

public class StatusBarRendererTest : GoTestBase
{
    public StatusBarRendererTest(Node testScene) : base(testScene) { }

    [Test]
    public void HealthColorMatchesEngineThresholds()
    {
        // Dead -> grey (0x808080)
        Color deadColor = StatusBarRenderer.GetHealthColor(0, isDead: true);
        TestAssert.True(deadColor == new Color(0.5f, 0.5f, 0.5f), "dead is grey");

        // Health <= 25 -> red (0xFF0000)
        Color redColor = StatusBarRenderer.GetHealthColor(25, isDead: false);
        TestAssert.True(redColor == new Color(1f, 0f, 0f), "health <= 25 is red");

        // Health <= 50 -> orange (0xFF8000)
        Color orangeColor = StatusBarRenderer.GetHealthColor(50, isDead: false);
        TestAssert.True(orangeColor == new Color(1f, 0.5f, 0f), "health <= 50 is orange");

        // Health <= 100 -> white (0xFFFFFF)
        Color whiteColor = StatusBarRenderer.GetHealthColor(100, isDead: false);
        TestAssert.True(whiteColor == Colors.White, "health <= 100 is white");

        // Health > 100 -> blue / cyan megahealth boost (0x40C0FF)
        Color megaColor = StatusBarRenderer.GetHealthColor(150, isDead: false);
        TestAssert.True(megaColor == new Color(0.25f, 0.75f, 1f), "health > 100 is cyan boost");
    }

    [Test]
    public void InstaModeDisablesHealthColoring()
    {
        Color color = StatusBarRenderer.GetHealthColor(20, isDead: false, isInsta: true);
        TestAssert.True(color == Colors.White, "insta mode forces white health text");
    }

    [Test]
    public void IconUvAtlasCoordinatesMatch4x4Grid()
    {
        // Health is icon index 3 -> row 0, col 3 -> tx=0.75, ty=0.0
        Rect2 healthUv = StatusBarRenderer.GetIconUv(HudIcon.Health);
        TestAssert.True(Mathf.IsEqualApprox(healthUv.Position.X, 0.75f), "health col 3");
        TestAssert.True(Mathf.IsEqualApprox(healthUv.Position.Y, 0.0f), "health row 0");
        TestAssert.True(Mathf.IsEqualApprox(healthUv.Size.X, 0.25f), "health width 0.25");
        TestAssert.True(Mathf.IsEqualApprox(healthUv.Size.Y, 0.25f), "health height 0.25");

        // Fist is icon index 4 -> row 1, col 0 -> tx=0.0, ty=0.25
        Rect2 fistUv = StatusBarRenderer.GetIconUv(HudIcon.Fist);
        TestAssert.True(Mathf.IsEqualApprox(fistUv.Position.X, 0.0f), "fist col 0");
        TestAssert.True(Mathf.IsEqualApprox(fistUv.Position.Y, 0.25f), "fist row 1");

        // Token is icon index 15 -> row 3, col 3 -> tx=0.75, ty=0.75
        Rect2 tokenUv = StatusBarRenderer.GetIconUv(HudIcon.Token);
        TestAssert.True(Mathf.IsEqualApprox(tokenUv.Position.X, 0.75f), "token col 3");
        TestAssert.True(Mathf.IsEqualApprox(tokenUv.Position.Y, 0.75f), "token row 3");
    }

    [Test]
    public void ArmorIconMapsArmorType()
    {
        TestAssert.True(StatusBarRenderer.GetArmorIcon(ArmourType.Blue) == HudIcon.BlueArmour, "blue armor");
        TestAssert.True(StatusBarRenderer.GetArmorIcon(ArmourType.Green) == HudIcon.GreenArmour, "green armor");
        TestAssert.True(StatusBarRenderer.GetArmorIcon(ArmourType.Yellow) == HudIcon.YellowArmour, "yellow armor");
    }

    [Test]
    public void HudControllerInitializesTextureAndChildren()
    {
        var hud = new HudController { Player = new MapPlayer() };
        var canvas = new CanvasLayer();
        TestScene.AddChild(canvas);
        canvas.AddChild(hud);

        var texSource = new SauerbratenRevival.World.Materials.ResourceTextureSource();
        hud.SetupTextures(texSource);

        var icon = hud.GetNodeOrNull<TextureRect>("StatusBar/HealthIcon");
        TestAssert.True(icon != null, "health icon node exists");
        TestAssert.True(icon!.Texture != null, $"health icon has texture: {icon.Texture}");

        var label = hud.GetNodeOrNull<Label>("StatusBar/HealthLabel");
        TestAssert.True(label != null, "health label node exists");

        hud.UpdateStatusBar();
        TestAssert.True(label!.Text == "100", $"health text is 100, got {label.Text}");

        canvas.Free();
    }
}
