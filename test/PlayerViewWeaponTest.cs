using System.IO;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Models;
using PlayerModel = SauerbratenRevival.World.Models.PlayerModel;

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

        // View weapons carry an extra -90° X in their GLB from Assimp's Z-up to Y-up
        // conversion; a universal +90° X base rotation cancels this duplicate transform.
        var baseRot = new Vector3(90f, 0f, 0f);
        TestAssert.Equal(baseRot, body.ViewWeapon!.ModelRotationDegrees, "rifle has universal +90° X base rotation");
        TestAssert.Equal(Vector3.Zero, body.ViewWeapon!.PositionOffset, "rifle has zero position offset");

        // In models where `Weapon` is an unweighted tag joint (mrfixit), GetOrCreateWeaponAnchor
        // reparents `Weapon` under a BoneAttachment3D bound to the parent bone (`HandGoal.R`)
        // so third-person view weapons dynamically follow the character's hand animations.
        TestAssert.True(anchor.GetParent() is BoneAttachment3D, "Weapon node parented under BoneAttachment3D");
        var boneAtt = (BoneAttachment3D)anchor.GetParent()!;
        TestAssert.Equal("HandGoal.R", boneAtt.BoneName, "BoneAttachment3D tracks HandGoal.R bone");

        body.SetViewWeapon("vwep/rocket");
        TestAssert.True(body.ViewWeapon != null, "rocket launcher view weapon attached");
        TestAssert.Equal(baseRot, body.ViewWeapon!.ModelRotationDegrees, "rocket launcher has universal +90° X base rotation");
        TestAssert.Equal(Vector3.Zero, body.ViewWeapon!.PositionOffset, "rocket launcher has zero position offset");

        body.SetViewWeapon("vwep/shotg");
        TestAssert.True(body.ViewWeapon != null, "shotgun view weapon attached");
        TestAssert.Equal(baseRot, body.ViewWeapon!.ModelRotationDegrees, "shotgun has universal +90° X base rotation");
        TestAssert.Equal(Vector3.Zero, body.ViewWeapon!.PositionOffset, "shotgun has zero position offset");

        // Verify ViewWeaponOffset property updates all cached weapons live
        body.ViewWeaponOffset = new Vector3(0.1f, 0.2f, 0.3f);
        TestAssert.Equal(new Vector3(0.1f, 0.2f, 0.3f), body.ViewWeapon!.PositionOffset, "shotgun reflects custom offset");
        body.SetViewWeapon("vwep/rocket");
        TestAssert.Equal(new Vector3(0.1f, 0.2f, 0.3f), body.ViewWeapon!.PositionOffset, "rocket launcher combines base + custom offset");

        // Reset offset
        body.ViewWeaponOffset = Vector3.Zero;
        TestAssert.Equal(Vector3.Zero, body.ViewWeapon!.PositionOffset, "rocket launcher restored to base offset");

        holder.QueueFree();
    }

    [Test]
    public void AttachesWeaponToOgro()
    {
        var catalog = LoadCatalog();
        if (catalog == null) { GD.Print("[vwep] no converted models; skipping"); return; }
        if (catalog.Get("vwep/rifle") == null || catalog.Get("ogro2/blue") == null)
        {
            GD.Print("[vwep] ogro2/blue or vwep/rifle missing; skipping");
            return;
        }

        var holder = new Node3D();
        TestScene.AddChild(holder);
        var body = new PlayerModel { Name = "PlayerModel_Ogro" };
        holder.AddChild(body);
        // Ogro2 is playermodel index 2
        body.Configure(catalog, null, 2, 1, viewWeapon: "vwep/rifle");

        if (!body.ModelLoaded) { GD.Print("[vwep] ogro2 body glb missing; skipping"); return; }

        TestAssert.True(body.ViewWeapon != null, "ogro has view weapon attached");
        TestAssert.Equal("vwep/rifle", body.ViewWeaponPath, "ogro view weapon path recorded");
        TestAssert.True(body.ViewWeapon!.ModelLoaded, "ogro view weapon glb loaded");
        TestAssert.True(body.ViewWeapon!.Visible, "ogro view weapon visible");

        // Can swap weapons on ogro as well
        body.SetViewWeapon("vwep/rocket");
        TestAssert.True(body.ViewWeapon != null, "ogro swapped to rocket");
        TestAssert.Equal("vwep/rocket", body.ViewWeaponPath, "ogro rocket path");

        holder.QueueFree();
    }

    [Test]
    public void SwapsAndClearsWeaponAtRuntime()
    {
        var catalog = LoadCatalog();
        if (catalog == null) { GD.Print("[vwep] no converted models; skipping"); return; }
        if (catalog.Get("vwep/rifle") == null || catalog.Get("vwep/shotg") == null)
        { GD.Print("[vwep] vwep models missing; skipping"); return; }

        var holder = new Node3D();
        TestScene.AddChild(holder);
        var body = new PlayerModel { Name = "PlayerModel" };
        holder.AddChild(body);
        body.Configure(catalog, null, 0, 1, viewWeapon: "vwep/rifle");
        if (!body.ModelLoaded) { GD.Print("[vwep] player body glb missing; skipping"); return; }

        var first = body.ViewWeapon;
        TestAssert.True(first != null, "initial weapon attached");

        // SetViewWeapon is what the remote inspector's ViewWeapon property drives,
        // so it must swap the rigid model without reloading the body.
        body.SetViewWeapon("vwep/shotg");
        TestAssert.Equal("vwep/shotg", body.ViewWeaponPath, "weapon path swapped");
        TestAssert.True(body.ViewWeapon != null, "new weapon attached");
        TestAssert.True(body.ViewWeapon != first, "weapon node switched");
        TestAssert.True(GodotObject.IsInstanceValid(first), "old weapon node retained in bank");
        TestAssert.False(first!.Visible, "old weapon node hidden");
        TestAssert.True(body.ViewWeapon!.Visible, "new weapon node visible");

        body.SetViewWeapon("");
        TestAssert.True(body.ViewWeapon == null, "weapon cleared");
        TestAssert.True(body.ViewWeaponPath == null, "weapon path cleared");
        TestAssert.False(first.Visible, "weapon remains hidden when cleared");

        body.SetViewWeapon("vwep/rifle");
        TestAssert.True(object.ReferenceEquals(body.ViewWeapon, first), "exact same weapon node reused without reinstantiating");
        TestAssert.True(first.Visible, "weapon visible again");
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
