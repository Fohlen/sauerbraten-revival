using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

/// <summary>Checks linked hudgun parts load and follow the imported hand attachment.</summary>
public class HudgunModelTest : GoTestBase
{
    public HudgunModelTest(Node testScene) : base(testScene) { }

    [Test]
    public void LoadsRiflePartsAndAttachesWeaponToHand()
    {
        TestAssert.True(HudgunModel.CubeVectorToCamera(new Vector3(0f, 0f, 1f)).IsEqualApprox(Vector3.Up),
            "Cube2 +Z up maps to camera-local +Y up");
        TestAssert.True(HudgunModel.CubeVectorToCamera(new Vector3(0f, 1f, 0f))
                .IsEqualApprox(Vector3.Forward),
            "Cube2 +Y forward maps to camera-local -Z forward");

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
        Basis originalBasis = hudgun.Basis;
        hudgun.HudgunRotationDegrees = new Vector3(30f, 20f, 10f);
        TestAssert.True(!hudgun.Basis.IsEqualApprox(originalBasis),
            "changing HUD rotation updates the live model transform");
        TestScene.RemoveChild(hudgun);
        hudgun.Free();
    }
}
