using System.IO;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies <see cref="PlayerModel"/> attaches a third-person view weapon to the
/// body skeleton's <c>Weapon</c> bone (render.cpp <c>modelattach("tag_weapon", vweps[…])</c>).
/// Skips if the converted models aren't present (git-ignored).
/// </summary>
public class PlayerViewWeaponTest : GoTestBase
{
    public PlayerViewWeaponTest(Node testScene) : base(testScene) { }

    private static ModelCatalog? LoadCatalog()
    {
        string dir = ProjectSettings.GlobalizePath("res://assets/models");
        return ModelCatalog.Load(Path.Combine(dir, "models.json"), dir, "res://assets/models");
    }

    [Test]
    public void AttachesWeaponToWeaponBone()
    {
        var catalog = LoadCatalog();
        if (catalog == null) { GD.Print("[vwep] no converted models; skipping"); return; }
        if (catalog.Get("vwep/rifle") == null) { GD.Print("[vwep] vwep/rifle missing; skipping"); return; }

        var holder = new Node3D();
        TestScene.AddChild(holder);
        var body = new PlayerModel { Name = "PlayerModel" };
        holder.AddChild(body);
        body.Configure(catalog, null, 0, 1, viewWeapon: "vwep/rifle");

        if (!body.ModelLoaded) { GD.Print("[vwep] player body glb missing; skipping"); return; }

        TestAssert.True(body.ViewWeapon != null, "view weapon node created");
        TestAssert.Equal("vwep/rifle", body.ViewWeaponPath, "view weapon path recorded");
        TestAssert.True(body.ViewWeapon!.ModelLoaded, "view weapon glb loaded");

        var skeleton = body.Skeleton;
        TestAssert.True(skeleton != null, "body skeleton present");

        // `Weapon` is a tag bone with no skin weights; Godot imports it as a plain
        // Node3D under the parent joint. The weapon must hang off that node.
        var anchor = body.FindChild("Weapon", recursive: true, owned: false) as Node3D;
        TestAssert.True(anchor != null, "body exposes the Weapon attachment node");
        TestAssert.True(anchor!.GetChildren().Contains(body.ViewWeapon),
            "weapon parented under the Weapon node");
        holder.QueueFree();
    }

    [Test]
    public void NoWeaponWhenUnset()
    {
        var catalog = LoadCatalog();
        if (catalog == null) { GD.Print("[vwep] no converted models; skipping"); return; }
        if (catalog.Get("mrfixit/blue") == null) { GD.Print("[vwep] mrfixit/blue missing; skipping"); return; }

        var holder = new Node3D();
        TestScene.AddChild(holder);
        var body = new PlayerModel { Name = "PlayerModel" };
        holder.AddChild(body);
        body.Configure(catalog, null, 0, 1);

        if (!body.ModelLoaded) { GD.Print("[vwep] player body glb missing; skipping"); return; }

        TestAssert.True(body.ViewWeapon == null, "no view weapon by default");
        TestAssert.True(body.Skeleton?.GetNodeOrNull<BoneAttachment3D>("WeaponBone") == null,
            "no bone attachment by default");
        holder.QueueFree();
    }
}
