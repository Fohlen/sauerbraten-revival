using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Models;
using HudgunModel = SauerbratenRevival.World.Models.HudgunModel;

namespace SauerbratenRevival.Tests;

/// <summary>Checks linked hudgun parts load and follow the imported hand attachment.</summary>
public class HudgunModelTest : GoTestBase
{
    public HudgunModelTest(Node testScene) : base(testScene) { }

    [Test]
    public void LoadsRiflePartsAndAttachesWeaponToHand()
    {
        // Cube 2 model translation axes map directly to Godot camera space:
        // tx (model forward) -> camera forward (-Z)
        // ty (model right)   -> camera right (+X)
        // tz (model up)      -> camera up (+Y)
        TestAssert.True(HudgunModel.CubeVectorToCamera(new Vector3(0f, 0f, 1f)).IsEqualApprox(Vector3.Up),
            "Cube2 model +tz up maps to camera-local +Y up");
        TestAssert.True(HudgunModel.CubeVectorToCamera(new Vector3(1f, 0f, 0f)).IsEqualApprox(Vector3.Forward),
            "Cube2 model +tx forward maps to camera-local -Z forward");
        TestAssert.True(HudgunModel.CubeVectorToCamera(new Vector3(0f, 1f, 0f)).IsEqualApprox(Vector3.Right),
            "Cube2 model +ty right maps to camera-local +X right");

        string dir = ProjectSettings.GlobalizePath("res://assets/models");
        var catalog = ModelCatalog.Load(Path.Combine(dir, "models.json"), dir, "res://assets/models");
        if (catalog?.Get("mrfixit/hudguns/rifle") is not { } info)
        {
            GD.Print("[hudgun] converted rifle model missing; skipping");
            return;
        }

        TestAssert.Equal(2, info.Parts.Count, "rifle has hand and weapon parts");
        TestAssert.Equal(0, info.Parts[1].LinkParent, "weapon links to hand part");
        TestAssert.Equal("tag_bicep", info.Parts[1].LinkTag!, "weapon link tag");

        var hudgun = new HudgunModel();
        hudgun.Configure(catalog, null, 0, "rifle");
        TestScene.AddChild(hudgun);

        TestAssert.True(hudgun.ModelLoaded, "hudgun parts loaded");
        TestAssert.Equal(2, hudgun.Parts.Count, "both hudgun parts instantiated");
        TestAssert.True(hudgun.Parts[1].GetParent() != hudgun,
            "weapon part attaches beneath the hand model's link bone");

        // Verify that default offsets are zero and match the engine camera-space placement
        TestAssert.Equal(Vector3.Zero, hudgun.HudgunOffset, "default HudgunOffset is zero");
        TestAssert.Equal(Vector3.Zero, hudgun.HudgunRotationDegrees, "default HudgunRotationDegrees is zero");

        Vector3 expectedPosition = HudgunModel.CubeVectorToCamera(info.Translation * info.Scale);
        TestAssert.True(hudgun.Position.IsEqualApprox(expectedPosition),
            $"hudgun position matches original camera-space transform ({expectedPosition})");
        Basis expectedBasis = new Basis(Vector3.Up, Mathf.Pi / 2f);
        TestAssert.True(hudgun.Basis.IsEqualApprox(expectedBasis),
            "hudgun base orientation matches engine forward view orientation");

        Basis originalBasis = hudgun.Basis;
        hudgun.HudgunRotationDegrees = new Vector3(30f, 20f, 10f);
        TestAssert.True(!hudgun.Basis.IsEqualApprox(originalBasis),
            "changing HUD rotation updates the live model transform");

        TestScene.RemoveChild(hudgun);
        hudgun.Free();
    }

    [Test]
    public void PrebuildsWeaponsAndSwapsZeroCost()
    {
        string dir = ProjectSettings.GlobalizePath("res://assets/models");
        var catalog = ModelCatalog.Load(Path.Combine(dir, "models.json"), dir, "res://assets/models");
        if (catalog?.Get("mrfixit/hudguns/rifle") == null || catalog?.Get("mrfixit/hudguns/shotg") == null)
        {
            GD.Print("[hudgun] converted weapon models missing; skipping");
            return;
        }

        var hudgun = new HudgunModel();
        hudgun.Configure(catalog, null, 0, "rifle");
        TestScene.AddChild(hudgun);

        TestAssert.True(hudgun.CachedWeaponCount > 1, "multiple weapons prebuilt in bank");
        TestAssert.Equal("rifle", hudgun.CurrentGun, "starts with rifle");

        var riflePart0 = hudgun.Parts[0];
        var riflePart1 = hudgun.Parts[1];
        var rifleContainer = riflePart0.GetParent() as Node3D;
        TestAssert.True(rifleContainer != null, "rifle container exists");
        TestAssert.True(rifleContainer!.Visible, "rifle container is visible");
        TestAssert.Equal((int)Node.ProcessModeEnum.Inherit, (int)rifleContainer.ProcessMode, "rifle container is processing");

        // Swap to shotg
        hudgun.SetGun("shotg");
        TestAssert.Equal("shotg", hudgun.CurrentGun, "switched to shotg");
        TestAssert.True(hudgun.ModelLoaded, "shotgun model loaded");

        var shotgPart0 = hudgun.Parts[0];
        var shotgPart1 = hudgun.Parts[1];
        TestAssert.True(!object.ReferenceEquals(shotgPart0, riflePart0), "shotg has distinct parts from rifle");

        // Verify rifle parts were NOT freed, but hidden & disabled
        TestAssert.True(GodotObject.IsInstanceValid(riflePart0), "rifle Part 0 retained in memory");
        TestAssert.True(GodotObject.IsInstanceValid(riflePart1), "rifle Part 1 retained in memory");
        TestAssert.False(rifleContainer.Visible, "rifle container is hidden");
        TestAssert.Equal((int)Node.ProcessModeEnum.Disabled, (int)rifleContainer.ProcessMode, "rifle container is disabled");

        // Swap back to rifle
        hudgun.SetGun("rifle");
        TestAssert.Equal("rifle", hudgun.CurrentGun, "switched back to rifle");
        TestAssert.True(object.ReferenceEquals(hudgun.Parts[0], riflePart0), "exact same rifle instance reused without reallocating");
        TestAssert.True(object.ReferenceEquals(hudgun.Parts[1], riflePart1), "exact same rifle weapon part reused without reallocating");
        TestAssert.True(rifleContainer.Visible, "rifle container restored to visible");
        TestAssert.Equal((int)Node.ProcessModeEnum.Inherit, (int)rifleContainer.ProcessMode, "rifle container restored to processing");

        TestScene.RemoveChild(hudgun);
        hudgun.Free();
    }

    private sealed class DummyTextureSource : ITextureSource
    {
        private readonly Dictionary<string, Texture2D> _textures = new();

        public DummyTextureSource(params string[] paths)
        {
            var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            foreach (var p in paths)
                _textures[p] = ImageTexture.CreateFromImage(img);
        }

        public Texture2D? Get(string path) => _textures.GetValueOrDefault(path);
        public Vector2I Size(string path) => new(4, 4);
        public Image? GetImage(string path) => null;
    }

    [Test]
    public void TeamSkinAppliedInFirstPerson()
    {
        string dir = ProjectSettings.GlobalizePath("res://assets/models");
        var catalog = ModelCatalog.Load(Path.Combine(dir, "models.json"), dir, "res://assets/models");
        if (catalog?.Get("mrfixit/hudguns/rifle") == null)
        {
            GD.Print("[hudgun] converted rifle model missing; skipping");
            return;
        }

        var textures = new DummyTextureSource(
            "models/mrfixit/hudguns/fixit_hands_blue.png",
            "models/mrfixit/hudguns/fixit_hands_red.png",
            "models/mrfixit/hudguns/fixit_hands.png",
            "models/hudguns/rifle/sniper.png"
        );

        var skinCache = new Dictionary<string, Material>();
        var hudgun = new HudgunModel();
        hudgun.Configure(catalog, textures, 0, "rifle", team: 1, skinCache: skinCache);
        TestScene.AddChild(hudgun);

        TestAssert.Equal(1, hudgun.Team, "initial team is blue (1)");
        TestAssert.True(hudgun.Parts.Count >= 2, "hudgun has hands and rifle parts");

        var handsPart = hudgun.Parts[0];
        var weaponPart = hudgun.Parts[1];

        var blueMat = handsPart.SkinMaterial as StandardMaterial3D;
        TestAssert.True(blueMat != null, "blue hands material created");
        TestAssert.Equal(textures.Get("models/mrfixit/hudguns/fixit_hands_blue.png"), blueMat!.AlbedoTexture,
            "team 1 uses blue hands skin in 1st person");

        var weaponMat = weaponPart.SkinMaterial as StandardMaterial3D;
        TestAssert.True(weaponMat != null, "weapon material created");
        TestAssert.Equal(textures.Get("models/hudguns/rifle/sniper.png"), weaponMat!.AlbedoTexture,
            "weapon skin remains sniper texture");

        // Change team to red (2)
        hudgun.Team = 2;
        var redMat = handsPart.SkinMaterial as StandardMaterial3D;
        TestAssert.True(redMat != null, "red hands material created");
        TestAssert.Equal(textures.Get("models/mrfixit/hudguns/fixit_hands_red.png"), redMat!.AlbedoTexture,
            "team 2 switches hands to red skin in 1st person");
        TestAssert.Equal(textures.Get("models/hudguns/rifle/sniper.png"), (weaponPart.SkinMaterial as StandardMaterial3D)?.AlbedoTexture,
            "weapon skin unaffected by team switch to red");

        // Change team to neutral (0)
        hudgun.Team = 0;
        var neutralMat = handsPart.SkinMaterial as StandardMaterial3D;
        TestAssert.True(neutralMat != null, "neutral hands material created");
        TestAssert.Equal(textures.Get("models/mrfixit/hudguns/fixit_hands.png"), neutralMat!.AlbedoTexture,
            "team 0 switches hands to neutral skin in 1st person");
        TestAssert.Equal(textures.Get("models/hudguns/rifle/sniper.png"), (weaponPart.SkinMaterial as StandardMaterial3D)?.AlbedoTexture,
            "weapon skin unaffected by team switch to neutral");

        TestScene.RemoveChild(hudgun);
        hudgun.Free();
    }
}
