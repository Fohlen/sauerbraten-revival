using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World;
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

    [Test]
    public void HudControllerUpdatesQuadDamageAndFlagStatusCorrectly()
    {
        var player = new MapPlayer();
        var hud = new HudController { Player = player };
        var canvas = new CanvasLayer();
        TestScene.AddChild(canvas);
        canvas.AddChild(hud);

        var texSource = new SauerbratenRevival.World.Materials.ResourceTextureSource();
        hud.SetupTextures(texSource);

        var quadIcon = hud.GetNodeOrNull<TextureRect>("StatusBar/QuadIcon");
        var flagIcon = hud.GetNodeOrNull<TextureRect>("StatusBar/FlagIcon");
        var flagLabel = hud.GetNodeOrNull<Label>("StatusBar/FlagLabel");

        TestAssert.True(quadIcon != null, "quad icon node exists");
        TestAssert.True(flagIcon != null, "flag icon node exists");
        TestAssert.True(flagLabel != null, "flag label node exists");

        // Initially no quad and no flag
        player.Combat.QuadMillis = 0;
        hud.HasFlag = false;
        hud.UpdateStatusBar();

        TestAssert.False(quadIcon!.Visible, "quad icon hidden when quadmillis is 0");
        TestAssert.False(flagIcon!.Visible, "flag icon hidden when hasflag is false");
        TestAssert.False(flagLabel!.Visible, "flag label hidden when hasflag is false");

        // Activate Quad Damage
        player.Combat.QuadMillis = 15000;
        hud.UpdateStatusBar();

        TestAssert.True(quadIcon.Visible, "quad icon visible when quadmillis > 0");
        var quadAtlas = (AtlasTexture)quadIcon.Texture;
        Rect2 quadUv = StatusBarRenderer.GetIconUv(HudIcon.Quad);
        Vector2 texSize = quadAtlas.Atlas?.GetSize() ?? Vector2.Zero;
        if (texSize.X > 0 && texSize.Y > 0)
        {
            TestAssert.True(Mathf.IsEqualApprox(quadAtlas.Region.Position.X, quadUv.Position.X * texSize.X), "correct atlas X offset for quad");
            TestAssert.True(Mathf.IsEqualApprox(quadAtlas.Region.Position.Y, quadUv.Position.Y * texSize.Y), "correct atlas Y offset for quad");
        }

        Vector2 viewportSize = hud.GetViewportRect().Size;
        float scale = viewportSize.Y / StatusBarRenderer.ReferenceHeight;
        float expectedQuadX = (StatusBarRenderer.IconX + 3 * StatusBarRenderer.IconStep) * scale;
        TestAssert.True(Mathf.IsEqualApprox(quadIcon.Position.X, expectedQuadX), $"quad X matches 3 * HICON_STEP, got {quadIcon.Position.X} expected {expectedQuadX}");

        // CTF Flag Carrier: blue team holding enemy red flag (flagTeam 2, playerTeam 1)
        player.Team = 1;
        hud.HeldFlagTeam = 2;
        hud.HasFlag = true;
        hud.IsHoldMode = false;
        hud.UpdateStatusBar();

        TestAssert.True(flagIcon.Visible, "flag icon visible when carrying flag");
        TestAssert.False(flagLabel.Visible, "flag label hidden in standard CTF");

        var flagAtlas = (AtlasTexture)flagIcon.Texture;
        Rect2 redFlagUv = StatusBarRenderer.GetIconUv(HudIcon.RedFlag);
        if (texSize.X > 0 && texSize.Y > 0)
        {
            TestAssert.True(Mathf.IsEqualApprox(flagAtlas.Region.Position.X, redFlagUv.Position.X * texSize.X), "enemy flag shows red flag icon");
            TestAssert.True(Mathf.IsEqualApprox(flagAtlas.Region.Position.Y, redFlagUv.Position.Y * texSize.Y), "enemy flag shows red flag icon Y");
        }

        // Flag position with quad active: offset by HICON_SIZE + HICON_SPACE
        float expectedFlagXWithQuad = (StatusBarRenderer.IconX + 3 * StatusBarRenderer.IconStep + StatusBarRenderer.IconSize + StatusBarRenderer.IconSpace) * scale;
        TestAssert.True(Mathf.IsEqualApprox(flagIcon.Position.X, expectedFlagXWithQuad), $"flag X offset by quad size + space when quad is active");

        // Player team holding own blue flag (e.g. dropped & returned / protect mode)
        hud.HeldFlagTeam = 1;
        hud.UpdateStatusBar();
        flagAtlas = (AtlasTexture)flagIcon.Texture;
        Rect2 blueFlagUv = StatusBarRenderer.GetIconUv(HudIcon.BlueFlag);
        if (texSize.X > 0 && texSize.Y > 0)
        {
            TestAssert.True(Mathf.IsEqualApprox(flagAtlas.Region.Position.X, blueFlagUv.Position.X * texSize.X), "own flag shows blue flag icon");
            TestAssert.True(Mathf.IsEqualApprox(flagAtlas.Region.Position.Y, blueFlagUv.Position.Y * texSize.Y), "own flag shows blue flag icon Y");
        }

        // Hold Mode: neutral flag with hold counter countdown
        hud.IsHoldMode = true;
        hud.HoldRemainingSeconds = 14;
        hud.UpdateStatusBar();

        TestAssert.True(flagLabel.Visible, "flag label visible in hold mode");
        TestAssert.True(flagLabel.Text == "14", $"flag label shows remaining hold seconds, got {flagLabel.Text}");
        flagAtlas = (AtlasTexture)flagIcon.Texture;
        Rect2 neutralFlagUv = StatusBarRenderer.GetIconUv(HudIcon.NeutralFlag);
        if (texSize.X > 0 && texSize.Y > 0)
        {
            TestAssert.True(Mathf.IsEqualApprox(flagAtlas.Region.Position.X, neutralFlagUv.Position.X * texSize.X), "hold mode shows neutral flag icon");
            TestAssert.True(Mathf.IsEqualApprox(flagAtlas.Region.Position.Y, neutralFlagUv.Position.Y * texSize.Y), "hold mode shows neutral flag icon Y");
        }

        // When quad expires, flag position shifts left to 3 * HICON_STEP
        player.Combat.QuadMillis = 0;
        hud.UpdateStatusBar();
        TestAssert.False(quadIcon.Visible, "quad icon hidden when quad expired");
        float expectedFlagXNoQuad = (StatusBarRenderer.IconX + 3 * StatusBarRenderer.IconStep) * scale;
        TestAssert.True(Mathf.IsEqualApprox(flagIcon.Position.X, expectedFlagXNoQuad), "flag X shifts to 3 * HICON_STEP when no quad");

        // When dead, both quad and flag are hidden
        player.Entity.Dead = true;
        player.Combat.QuadMillis = 10000;
        hud.UpdateStatusBar();
        TestAssert.False(quadIcon.Visible, "quad hidden when dead");
        TestAssert.False(flagIcon.Visible, "flag icon hidden when dead");
        TestAssert.False(flagLabel.Visible, "flag label hidden when dead");

        canvas.Free();
    }

    [Test]
    public void AmmoBarGunVisibleFiltersByGameMode()
    {
        // Selected gun is always visible
        TestAssert.True(StatusBarRenderer.IsAmmoBarGunVisible(0, (int)Gun.Shotgun, (int)Gun.Shotgun, hideEmpty: true, isInsta: false, isEfficiency: false), "selected gun visible even if 0 ammo");

        // Guns with ammo are always visible
        TestAssert.True(StatusBarRenderer.IsAmmoBarGunVisible(10, (int)Gun.Rifle, (int)Gun.Shotgun, hideEmpty: true, isInsta: false, isEfficiency: false), "gun with ammo visible");

        // HideEmpty hides guns with 0 ammo if not selected
        TestAssert.False(StatusBarRenderer.IsAmmoBarGunVisible(0, (int)Gun.Rifle, (int)Gun.Shotgun, hideEmpty: true, isInsta: false, isEfficiency: false), "0 ammo hidden when hideEmpty=true");

        // Efficiency mode: hides pistol (even if hideEmpty=false)
        TestAssert.False(StatusBarRenderer.IsAmmoBarGunVisible(0, (int)Gun.Rifle, (int)Gun.Pistol, hideEmpty: false, isInsta: false, isEfficiency: true), "pistol hidden in efficiency mode");
        TestAssert.True(StatusBarRenderer.IsAmmoBarGunVisible(0, (int)Gun.Rifle, (int)Gun.RocketLauncher, hideEmpty: false, isInsta: false, isEfficiency: true), "rocket launcher visible in efficiency mode");

        // Insta mode: only rifle is visible
        TestAssert.True(StatusBarRenderer.IsAmmoBarGunVisible(0, (int)Gun.Shotgun, (int)Gun.Rifle, hideEmpty: false, isInsta: true, isEfficiency: false), "rifle visible in insta mode");
        TestAssert.False(StatusBarRenderer.IsAmmoBarGunVisible(0, (int)Gun.Rifle, (int)Gun.Shotgun, hideEmpty: false, isInsta: true, isEfficiency: false), "shotgun hidden in insta mode");
    }

    [Test]
    public void AmmoBarColorMatchesAmmoRatios()
    {
        // 0 ammo -> white
        Color emptyColor = StatusBarRenderer.GetAmmoBarColor(0, 10);
        TestAssert.True(emptyColor == Colors.White, "0 ammo is white");

        // Full ammo (>= add stat, i.e. ammoratio >= 1.0f) -> white
        Color fullColor = StatusBarRenderer.GetAmmoBarColor(10, 10);
        TestAssert.True(fullColor == Colors.White, "ammo >= add is white");

        // Half ammo (ammoratio >= 0.5f) -> yellow / gold (0xFFC040)
        Color halfColor = StatusBarRenderer.GetAmmoBarColor(5, 10);
        TestAssert.True(halfColor == new Color(1f, 0.75294f, 0.25098f), "half ammo is yellow 0xFFC040");

        // Low ammo (ammoratio < 0.5f) -> red (0xFF0000)
        Color lowColor = StatusBarRenderer.GetAmmoBarColor(4, 10);
        TestAssert.True(lowColor == new Color(1f, 0f, 0f), "low ammo is red 0xFF0000");
    }

    [Test]
    public void AmmoBarLayoutMatchesEngineMath()
    {
        int[] ammo = new int[Guns.Count];
        ammo[(int)Gun.Shotgun] = 10;
        ammo[(int)Gun.Chaingun] = 20;

        var items = StatusBarRenderer.LayoutAmmoBar(
            ammo,
            (int)Gun.Shotgun,
            1920,
            1080,
            isInsta: false,
            isEfficiency: false,
            align: 0,
            horizontal: false,
            flip: false,
            hideEmpty: true,
            sep: 20f,
            countSep: 20f,
            countScale: 1.5f,
            barX: 0.025f,
            barY: 0.5f,
            barScale: 0.5f);

        TestAssert.True(items.Count == 2, $"two visible guns in ammo bar, got {items.Count}");
        TestAssert.True(items[0].Gun == Gun.Shotgun, "first item is shotgun");
        TestAssert.True(items[1].Gun == Gun.Chaingun, "second item is chaingun");
        TestAssert.True(items[0].Color == Colors.White, "shotgun has full ammo -> white");
        TestAssert.True(Mathf.IsEqualApprox(items[0].Alpha, 1.0f), "shotgun with ammo -> alpha 1.0");

        // Insta mode returns empty list
        var instaItems = StatusBarRenderer.LayoutAmmoBar(
            ammo,
            (int)Gun.Shotgun,
            1920,
            1080,
            isInsta: true,
            isEfficiency: false);
        TestAssert.True(instaItems.Count == 0, "ammobar empty in insta mode");
    }

    [Test]
    public void AmmoHudQuickSelectorLayoutCalculatesCorrectPositions()
    {
        int[] ammo = new int[Guns.Count];
        ammo[(int)Gun.Chaingun] = 20;
        ammo[(int)Gun.Rifle] = 5;

        // Up stack has chaingun (GUN_CG); down stack has rifle (GUN_RIFLE)
        var items = StatusBarRenderer.LayoutAmmoHud(
            ammo,
            selectedGun: (int)Gun.Shotgun,
            upConfig: new int[] { (int)Gun.Chaingun, (int)Gun.RocketLauncher, (int)Gun.GrenadeLauncher },
            downConfig: new int[] { (int)Gun.Rifle, (int)Gun.Shotgun, (int)Gun.Pistol });

        TestAssert.True(items.Count == 2, $"2 items in quick ammo hud, got {items.Count}");
        TestAssert.True(items[0].Gun == Gun.Chaingun, "first item is CG in up stack");
        TestAssert.True(items[1].Gun == Gun.Rifle, "second item is rifle in down stack");

        // Verify size is HICON_SIZE / 3.2f
        float expectedSize = StatusBarRenderer.IconSize / 3.2f;
        TestAssert.True(Mathf.IsEqualApprox(items[0].Size, expectedSize), "icon size scaled by 1/3.2");
    }

    [Test]
    public void HudControllerUpdatesAmmoBarAndAmmoHudNodes()
    {
        var player = new MapPlayer();
        player.Combat.GunSelect = (int)Gun.Shotgun;
        player.Combat.Ammo[(int)Gun.Shotgun] = 10;
        player.Combat.Ammo[(int)Gun.Chaingun] = 5; // Half of 20 -> low (< 0.5f add) -> red
        player.Combat.Ammo[(int)Gun.RocketLauncher] = 0;

        var hud = new HudController
        {
            Player = player,
            AmmoHud = true,
            AmmoBar = true,
            AmmoBarHideEmpty = true
        };
        var canvas = new CanvasLayer();
        TestScene.AddChild(canvas);
        canvas.AddChild(hud);

        var texSource = new SauerbratenRevival.World.Materials.ResourceTextureSource();
        hud.SetupTextures(texSource);

        hud.UpdateStatusBar();

        var ammoHudRoot = hud.GetNodeOrNull<Control>("AmmoHud");
        TestAssert.True(ammoHudRoot != null, "AmmoHud root exists");
        TestAssert.True(ammoHudRoot!.Visible, "AmmoHud is visible");

        var ammoBarRoot = hud.GetNodeOrNull<Control>("AmmoBar");
        TestAssert.True(ammoBarRoot != null, "AmmoBar root exists");
        TestAssert.True(ammoBarRoot!.Visible, "AmmoBar is visible");

        var barIcon0 = hud.GetNodeOrNull<TextureRect>("AmmoBar/AmmoBarIcon_0");
        var barLabel0 = hud.GetNodeOrNull<Label>("AmmoBar/AmmoBarLabel_0");
        TestAssert.True(barIcon0 != null && barIcon0.Visible, "AmmoBar icon 0 visible");
        TestAssert.True(barLabel0 != null && barLabel0.Visible, "AmmoBar label 0 visible");
        TestAssert.True(barLabel0!.Text == "10", $"AmmoBar label 0 text is 10, got {barLabel0.Text}");

        var barLabel1 = hud.GetNodeOrNull<Label>("AmmoBar/AmmoBarLabel_1");
        TestAssert.True(barLabel1 != null && barLabel1.Visible, "AmmoBar label 1 visible");
        TestAssert.True(barLabel1!.Text == "5", $"AmmoBar label 1 text is 5, got {barLabel1.Text}");
        TestAssert.True(barLabel1.Modulate == new Color(1f, 0f, 0f), "AmmoBar label 1 is red for low ammo (< 0.5f)");

        // Insta mode disables ammo bar
        hud.IsInsta = true;
        hud.UpdateStatusBar();
        TestAssert.False(ammoBarRoot.Visible, "AmmoBar hidden in insta mode");

        canvas.Free();
    }

    [Test]
    public void GameClockFormatMatchesEngine()
    {
        // 10 minutes remaining: maplimit = 600,000, lastmillis = 0 -> secs = (600000 + 999)/1000 = 600 -> 10:00
        var (mins10, secs10, str10) = StatusBarRenderer.FormatGameClock(600000, 0);
        TestAssert.True(mins10 == 10 && secs10 == 0, "10 mins 0 secs");
        TestAssert.True(str10 == "10:00", $"Expected 10:00, got {str10}");

        // 1 minute remaining: 60,000 millis -> 1:00
        var (mins1, secs1, str1) = StatusBarRenderer.FormatGameClock(60000, 0);
        TestAssert.True(mins1 == 1 && secs1 == 0, "1 min 0 secs");
        TestAssert.True(str1 == "1:00", $"Expected 1:00, got {str1}");

        // 59 seconds remaining: 59,000 millis -> 0:59
        var (mins0, secs59, str59) = StatusBarRenderer.FormatGameClock(59000, 0);
        TestAssert.True(mins0 == 0 && secs59 == 59, "0 min 59 secs");
        TestAssert.True(str59 == "0:59", $"Expected 0:59, got {str59}");

        // Overtime / expired (lastmillis > maplimit) clamps to 0:00
        var (mins00, secs00, str00) = StatusBarRenderer.FormatGameClock(10000, 20000);
        TestAssert.True(mins00 == 0 && secs00 == 0, "expired is 0 mins 0 secs");
        TestAssert.True(str00 == "0:00", $"Expected 0:00, got {str00}");
    }

    [Test]
    public void GameClockColorWarningUnderOneMinute()
    {
        // >= 1 min: normal colour (default white 0xFFFFFF)
        Color normalColor = StatusBarRenderer.GetGameClockColor(1);
        TestAssert.True(normalColor == Colors.White, "1 min remaining is white");

        Color normal10Color = StatusBarRenderer.GetGameClockColor(10);
        TestAssert.True(normal10Color == Colors.White, "10 mins remaining is white");

        // < 1 min: low colour (default 0xFFA050 orange warning)
        Color lowColor = StatusBarRenderer.GetGameClockColor(0);
        Color expectedLow = new Color(1f, 160f / 255f, 80f / 255f, 1f);
        TestAssert.True(Mathf.IsEqualApprox(lowColor.R, expectedLow.R) &&
                        Mathf.IsEqualApprox(lowColor.G, expectedLow.G) &&
                        Mathf.IsEqualApprox(lowColor.B, expectedLow.B),
                        $"0 mins is orange warning color (0xFFA050), got {lowColor}");
    }

    [Test]
    public void GameClockLayoutCalculatesTopCenterOffset()
    {
        // 1920x1080 screen, text "10:00", scale 1.0, align 0 (center), x=0.50, y=0.03
        var (pos, size) = StatusBarRenderer.LayoutGameClock(1920f, 1080f, "10:00", 0.50f, 0.03f, 1.0f, 0);

        // Text bounds: '10:00' -> 30 + 30 + 16 + 30 + 30 = 136, height = 64
        TestAssert.True(Mathf.IsEqualApprox(size.X, 136f), $"size X is 136, got {size.X}");
        TestAssert.True(Mathf.IsEqualApprox(size.Y, 64f), $"size Y is 64, got {size.Y}");

        // Center X = 0.50 * 1920 = 960 -> pos.X = 960 - 136 / 2 = 892
        TestAssert.Near(892f, pos.X, 0.01f, "pos X is 892");
        // Center Y = 0.03 * 1080 = 32.4 -> pos.Y = 32.4 - 64 / 2 = 0.4
        TestAssert.Near(0.4f, pos.Y, 0.01f, "pos Y is 0.4");
    }

    [Test]
    public void HudControllerUpdatesGameClockNode()
    {
        var player = new MapPlayer();
        var hud = new HudController
        {
            Player = player,
            GameClock = true,
            MapLimit = 600000, // 10 minutes
            LastMillis = 0
        };
        var canvas = new CanvasLayer();
        TestScene.AddChild(canvas);
        canvas.AddChild(hud);

        hud.UpdateGameClock();

        var clockLabel = hud.GetNodeOrNull<Label>("GameClockLabel");
        TestAssert.True(clockLabel != null, "GameClockLabel node exists");
        TestAssert.True(clockLabel!.Visible, "GameClockLabel is visible when MapLimit >= 0");
        TestAssert.True(clockLabel.Text == "10:00", $"Clock text is 10:00, got {clockLabel.Text}");
        TestAssert.True(clockLabel.Modulate == Colors.White, "Clock text is white for >= 1 min");

        // Fast forward to 30 seconds remaining (570,000 millis passed)
        hud.LastMillis = 570000;
        hud.UpdateGameClock();
        TestAssert.True(clockLabel.Text == "0:30", $"Clock text is 0:30, got {clockLabel.Text}");
        Color expectedLow = StatusBarRenderer.GetGameClockColor(0);
        TestAssert.True(Mathf.IsEqualApprox(clockLabel.Modulate.R, expectedLow.R) &&
                        Mathf.IsEqualApprox(clockLabel.Modulate.G, expectedLow.G) &&
                        Mathf.IsEqualApprox(clockLabel.Modulate.B, expectedLow.B),
                        "Clock text turns orange warning color under 1 minute");

        // Negative MapLimit hides the game clock (untimed mode)
        hud.MapLimit = -1;
        hud.UpdateGameClock();
        TestAssert.False(clockLabel.Visible, "Game clock hidden when MapLimit is negative");

        // Disabling GameClock hides the label even if MapLimit is set
        hud.MapLimit = 600000;
        hud.GameClock = false;
        hud.UpdateGameClock();
        TestAssert.False(clockLabel.Visible, "Game clock hidden when GameClock is false");

        canvas.Free();
    }

    [Test]
    public void StatusColorMatchesEnginePrivilegeAndState()
    {
        // Normal alive player (privilege 0) -> 0xFFFFFF (white)
        Color normal = StatusBarRenderer.GetStatusColor(0, isDead: false);
        TestAssert.Equal(Colors.White, normal, "Normal player is white");

        // Normal dead player -> 0x606060 (dim gray)
        Color deadNormal = StatusBarRenderer.GetStatusColor(0, isDead: true);
        Color expectedDead = new Color(0x60 / 255f, 0x60 / 255f, 0x60 / 255f);
        TestAssert.True(Mathf.IsEqualApprox(deadNormal.R, expectedDead.R), "Dead normal red matches 0x60");

        // Master alive (privilege 1) -> 0x40FF80 (green)
        Color master = StatusBarRenderer.GetStatusColor(1, isDead: false);
        Color expectedMaster = new Color(0x40 / 255f, 0xFF / 255f, 0x80 / 255f);
        TestAssert.True(Mathf.IsEqualApprox(master.G, expectedMaster.G), "Master green matches 0xFF");

        // Auth alive (privilege 2) -> 0xC040C0 (purple)
        Color auth = StatusBarRenderer.GetStatusColor(2, isDead: false);
        Color expectedAuth = new Color(0xC0 / 255f, 0x40 / 255f, 0xC0 / 255f);
        TestAssert.True(Mathf.IsEqualApprox(auth.R, expectedAuth.R), "Auth red matches 0xC0");

        // Admin alive (privilege 3) -> 0xFF8000 (orange)
        Color admin = StatusBarRenderer.GetStatusColor(3, isDead: false);
        Color expectedAdmin = new Color(0xFF / 255f, 0x80 / 255f, 0x00 / 255f);
        TestAssert.True(Mathf.IsEqualApprox(admin.R, expectedAdmin.R), "Admin red matches 0xFF");

        // Admin dead -> ((0xFF8000 >> 1) & 0x7F7F7F) == 0x7F4000
        Color adminDead = StatusBarRenderer.GetStatusColor(3, isDead: true);
        Color expectedAdminDead = new Color(0x7F / 255f, 0x40 / 255f, 0x00 / 255f);
        TestAssert.True(Mathf.IsEqualApprox(adminDead.R, expectedAdminDead.R), "Admin dead dim red matches 0x7F");
    }

    [Test]
    public void LayoutSpectatorOverlayPositionsTextCorrectly()
    {
        // 1920x1080 resolution
        // scale = 1080 / 1800 = 0.6
        // refW = 1920 / 0.6 = 3200
        // pw = 60, ph = 64
        // tw = 9 * 30 = 270, th = 64
        // target = "unnamed" (7 chars) -> fw = 7 * 30 = 210, fh = 64
        // specX = (3200 - 270 - 60) * 0.6 = 2870 * 0.6 = 1722
        // specY = (1650 - 64 - 64) * 0.6 = 1522 * 0.6 = 913.2
        // followX = (3200 - 210 - 60) * 0.6 = 2930 * 0.6 = 1758
        // followY = (1650 - 64) * 0.6 = 1586 * 0.6 = 951.6
        var (specPos, specSize, followPos, followSize) = StatusBarRenderer.LayoutSpectatorOverlay(
            1920f, 1080f, "unnamed");

        TestAssert.Near(1722f, specPos.X, 0.05f, "specPos.X is 1722");
        TestAssert.Near(913.2f, specPos.Y, 0.05f, "specPos.Y is 913.2");
        TestAssert.Near(1758f, followPos.X, 0.05f, "followPos.X is 1758");
        TestAssert.Near(951.6f, followPos.Y, 0.05f, "followPos.Y is 951.6");
    }

    [Test]
    public void FormatEditCubeAndMaterialDescriptionMatchEngine()
    {
        // Air / no material
        string descAir = StatusBarRenderer.GetMaterialDesc(Octa.MAT_AIR, ": ");
        TestAssert.Equal("", descAir, "Air returns empty description");

        // Water
        string descWater = StatusBarRenderer.GetMaterialDesc(Octa.MAT_WATER, ": ");
        TestAssert.Equal(": water", descWater, "Water returns ': water'");

        // Lava + Clip + Death
        ushort combo = (ushort)(Octa.MAT_LAVA | Octa.MAT_CLIP | Octa.MAT_DEATH);
        string descCombo = StatusBarRenderer.GetMaterialDesc(combo, ": ");
        TestAssert.Equal(": lava, clip, death", descCombo, "Multi-material returns comma-separated list");

        // FormatEditCube single child
        string cube1 = StatusBarRenderer.FormatEditCube(1, Octa.MAT_AIR);
        TestAssert.Equal("cube 1", cube1, "Single child cube text is 'cube 1'");

        // FormatEditCube subdivided (negative count from mag > 1)
        string cubeSub = StatusBarRenderer.FormatEditCube(-4, Octa.MAT_WATER);
        TestAssert.Equal("cube 1/4: water", cubeSub, "Subdivided cube text is 'cube 1/4: water'");

        // Quick edit stats
        string stats = StatusBarRenderer.FormatEditQuickStats(16, 4, true, new Vector3I(128, 256, 512));
        TestAssert.True(stats.Contains("grid 16 (power 4)"), "Stats contain grid and power");
        TestAssert.True(stats.Contains("allfaces"), "Stats contain allfaces");
        TestAssert.True(stats.Contains("pos (128 256 512)"), "Stats contain pos");
    }

    [Test]
    public void HudControllerUpdatesSpectatorAndEditHudNodes()
    {
        var player = new MapPlayer();
        var hud = new HudController
        {
            Player = player,
            IsSpectator = true,
            FollowingPlayerName = "CubeHero",
            FollowingPlayerPrivilege = 3, // Admin
            FollowingPlayerDead = false
        };
        var canvas = new CanvasLayer();
        TestScene.AddChild(canvas);
        canvas.AddChild(hud);

        hud.UpdateSpectatorOverlay();

        var specRoot = hud.GetNodeOrNull<Control>("SpectatorHud");
        var specLabel = hud.GetNodeOrNull<Label>("SpectatorHud/SpectatorLabel");
        var followLabel = hud.GetNodeOrNull<Label>("SpectatorHud/FollowingLabel");

        TestAssert.True(specRoot != null && specRoot.Visible, "Spectator HUD root is visible");
        TestAssert.True(specLabel != null && specLabel.Text == "SPECTATOR", "Spectator label text is SPECTATOR");
        TestAssert.True(followLabel != null && followLabel.Text == "CubeHero", "Following player label shows CubeHero");
        TestAssert.True(followLabel!.Visible, "Following player label is visible");

        Color expectedAdmin = StatusBarRenderer.GetStatusColor(3, isDead: false);
        TestAssert.True(Mathf.IsEqualApprox(followLabel.Modulate.R, expectedAdmin.R), "Followed player color is admin orange");

        // Turn off spectator, enable edit mode
        hud.IsSpectator = false;
        hud.UpdateSpectatorOverlay();
        TestAssert.False(specRoot!.Visible, "Spectator HUD hidden when not spectating");

        hud.IsEditMode = true;
        hud.EditSelChildCount = -2;
        hud.EditSelChildMat = Octa.MAT_GLASS;
        hud.EditGridSize = 32;
        hud.EditGridPower = 5;
        hud.EditAllFaces = true;
        hud.UpdateEditHud();

        var editRoot = hud.GetNodeOrNull<Control>("EditHud");
        var cubeLabel = hud.GetNodeOrNull<Label>("EditHud/EditCubeLabel");
        var statsLabel = hud.GetNodeOrNull<Label>("EditHud/EditStatsLabel");

        TestAssert.True(editRoot != null && editRoot.Visible, "Edit HUD root is visible");
        TestAssert.True(cubeLabel != null && cubeLabel.Text == "cube 1/2: glass", $"Cube label matches 'cube 1/2: glass', got '{cubeLabel?.Text}'");
        TestAssert.True(statsLabel != null && statsLabel.Text.Contains("grid 32 (power 5)"), "Stats label contains grid power info");
        TestAssert.True(statsLabel!.Text.Contains("allfaces"), "Stats label contains allfaces");

        canvas.Free();
    }
}

