using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Weapons;

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

    [Test]
    public void HudControllerUpdatesArmorIndicatorCorrectly()
    {
        var player = new MapPlayer();
        var hud = new HudController { Player = player };
        var canvas = new CanvasLayer();
        TestScene.AddChild(canvas);
        canvas.AddChild(hud);

        var texSource = new SauerbratenRevival.World.Materials.ResourceTextureSource();
        hud.SetupTextures(texSource);

        var armorIcon = hud.GetNodeOrNull<TextureRect>("StatusBar/ArmorIcon");
        var armorLabel = hud.GetNodeOrNull<Label>("StatusBar/ArmorLabel");
        TestAssert.True(armorIcon != null, "armor icon node exists");
        TestAssert.True(armorLabel != null, "armor label node exists");

        // By default player has 0 armor -> hidden
        player.Combat.Armour = 0;
        hud.UpdateStatusBar();
        TestAssert.False(armorIcon!.Visible, "armor icon hidden when 0 armor");
        TestAssert.False(armorLabel!.Visible, "armor label hidden when 0 armor");

        // Set green armor
        player.Combat.Armour = 100;
        player.Combat.ArmourType = ArmourType.Green;
        hud.UpdateStatusBar();
        TestAssert.True(armorIcon.Visible, "armor icon visible when > 0 armor");
        TestAssert.True(armorLabel.Visible, "armor label visible when > 0 armor");
        TestAssert.True(armorLabel.Text == "100", $"armor text is 100, got {armorLabel.Text}");
        TestAssert.True(armorIcon.Texture is AtlasTexture, "armor icon texture is atlas texture");

        var atlas = (AtlasTexture)armorIcon.Texture;
        Rect2 greenUv = StatusBarRenderer.GetIconUv(HudIcon.GreenArmour);
        Vector2 texSize = atlas.Atlas?.GetSize() ?? Vector2.Zero;
        if (texSize.X > 0 && texSize.Y > 0)
        {
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.X, greenUv.Position.X * texSize.X), "correct atlas X offset for green armor");
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.Y, greenUv.Position.Y * texSize.Y), "correct atlas Y offset for green armor");
        }

        // Switch to yellow armor
        player.Combat.Armour = 50;
        player.Combat.ArmourType = ArmourType.Yellow;
        hud.UpdateStatusBar();
        TestAssert.True(armorLabel.Text == "50", $"armor text updated to 50, got {armorLabel.Text}");
        atlas = (AtlasTexture)armorIcon.Texture;
        Rect2 yellowUv = StatusBarRenderer.GetIconUv(HudIcon.YellowArmour);
        if (texSize.X > 0 && texSize.Y > 0)
        {
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.X, yellowUv.Position.X * texSize.X), "correct atlas X offset for yellow armor");
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.Y, yellowUv.Position.Y * texSize.Y), "correct atlas Y offset for yellow armor");
        }

        // Switch to blue armor
        player.Combat.Armour = 25;
        player.Combat.ArmourType = ArmourType.Blue;
        hud.UpdateStatusBar();
        TestAssert.True(armorLabel.Text == "25", $"armor text updated to 25, got {armorLabel.Text}");
        atlas = (AtlasTexture)armorIcon.Texture;
        Rect2 blueUv = StatusBarRenderer.GetIconUv(HudIcon.BlueArmour);
        if (texSize.X > 0 && texSize.Y > 0)
        {
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.X, blueUv.Position.X * texSize.X), "correct atlas X offset for blue armor");
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.Y, blueUv.Position.Y * texSize.Y), "correct atlas Y offset for blue armor");
        }

        // If player is dead, armor indicator is hidden
        player.Combat.Armour = 100;
        player.Entity.Dead = true;
        hud.UpdateStatusBar();
        TestAssert.False(armorIcon.Visible, "armor icon hidden when dead");
        TestAssert.False(armorLabel.Visible, "armor label hidden when dead");

        canvas.Free();
    }

    [Test]
    public void HudControllerUpdatesWeaponAndAmmoIndicatorCorrectly()
    {
        var player = new MapPlayer();
        var hud = new HudController { Player = player };
        var canvas = new CanvasLayer();
        TestScene.AddChild(canvas);
        canvas.AddChild(hud);

        var texSource = new SauerbratenRevival.World.Materials.ResourceTextureSource();
        hud.SetupTextures(texSource);

        var weaponIcon = hud.GetNodeOrNull<TextureRect>("StatusBar/WeaponIcon");
        var ammoLabel = hud.GetNodeOrNull<Label>("StatusBar/AmmoLabel");
        TestAssert.True(weaponIcon != null, "weapon icon node exists");
        TestAssert.True(ammoLabel != null, "ammo label node exists");

        // Pistol by default with ammo
        player.Combat.GunSelect = (int)Gun.Pistol;
        player.Combat.Ammo[(int)Gun.Pistol] = 88;
        hud.UpdateStatusBar();

        TestAssert.True(weaponIcon!.Visible, "weapon icon visible when alive");
        TestAssert.True(ammoLabel!.Visible, "ammo label visible when alive");
        TestAssert.True(ammoLabel.Text == "88", $"ammo text is 88, got {ammoLabel.Text}");
        TestAssert.True(weaponIcon.Texture is AtlasTexture, "weapon icon texture is atlas texture");

        var atlas = (AtlasTexture)weaponIcon.Texture;
        Rect2 pistolUv = StatusBarRenderer.GetIconUv(HudIcon.Pistol);
        Vector2 texSize = atlas.Atlas?.GetSize() ?? Vector2.Zero;
        if (texSize.X > 0 && texSize.Y > 0)
        {
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.X, pistolUv.Position.X * texSize.X), "correct atlas X offset for pistol");
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.Y, pistolUv.Position.Y * texSize.Y), "correct atlas Y offset for pistol");
        }

        // Switch to Rocket Launcher
        player.Combat.GunSelect = (int)Gun.RocketLauncher;
        player.Combat.Ammo[(int)Gun.RocketLauncher] = 15;
        hud.UpdateStatusBar();

        TestAssert.True(ammoLabel.Text == "15", $"ammo text is 15, got {ammoLabel.Text}");
        atlas = (AtlasTexture)weaponIcon.Texture;
        Rect2 rlUv = StatusBarRenderer.GetIconUv(HudIcon.Rl);
        if (texSize.X > 0 && texSize.Y > 0)
        {
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.X, rlUv.Position.X * texSize.X), "correct atlas X offset for rocket launcher");
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.Y, rlUv.Position.Y * texSize.Y), "correct atlas Y offset for rocket launcher");
        }

        // Switch to Fist
        player.Combat.GunSelect = (int)Gun.Fist;
        player.Combat.Ammo[(int)Gun.Fist] = 0;
        hud.UpdateStatusBar();

        TestAssert.True(ammoLabel.Text == "0", $"ammo text is 0 for fist, got {ammoLabel.Text}");
        atlas = (AtlasTexture)weaponIcon.Texture;
        Rect2 fistUv = StatusBarRenderer.GetIconUv(HudIcon.Fist);
        if (texSize.X > 0 && texSize.Y > 0)
        {
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.X, fistUv.Position.X * texSize.X), "correct atlas X offset for fist");
            TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.Y, fistUv.Position.Y * texSize.Y), "correct atlas Y offset for fist");
        }

        // Test other weapons: Shotgun, Chaingun, Rifle, GrenadeLauncher
        Gun[] guns = { Gun.Shotgun, Gun.Chaingun, Gun.Rifle, Gun.GrenadeLauncher };
        HudIcon[] icons = { HudIcon.Sg, HudIcon.Cg, HudIcon.Rifle, HudIcon.Gl };
        for (int i = 0; i < guns.Length; i++)
        {
            player.Combat.GunSelect = (int)guns[i];
            player.Combat.Ammo[(int)guns[i]] = 20 + i;
            hud.UpdateStatusBar();
            TestAssert.True(ammoLabel.Text == (20 + i).ToString(), $"ammo text matches for {guns[i]}");
            atlas = (AtlasTexture)weaponIcon.Texture;
            Rect2 uv = StatusBarRenderer.GetIconUv(icons[i]);
            if (texSize.X > 0 && texSize.Y > 0)
            {
                TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.X, uv.Position.X * texSize.X), $"correct atlas X offset for {guns[i]}");
                TestAssert.True(Mathf.IsEqualApprox(atlas.Region.Position.Y, uv.Position.Y * texSize.Y), $"correct atlas Y offset for {guns[i]}");
            }
        }

        // When dead, weapon icon and ammo label are hidden
        player.Entity.Dead = true;
        hud.UpdateStatusBar();
        TestAssert.False(weaponIcon.Visible, "weapon icon hidden when dead");
        TestAssert.False(ammoLabel.Visible, "ammo label hidden when dead");

        canvas.Free();
    }
}
