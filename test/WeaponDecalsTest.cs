using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Effects;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

public class WeaponDecalsTest : GoTestBase
{
    public WeaponDecalsTest(Node testScene) : base(testScene) { }

    [Test]
    public void WeaponEffectsRendererSpawnsAndPoolsBulletDecals()
    {
        var renderer = new WeaponEffectsRenderer();
        TestScene.AddChild(renderer);

        Vector3 hitPos = new(50f, 10f, 20f);
        Vector3 surfaceNormal = Vector3.Up;
        float radius = 2.0f;

        renderer.AddBulletHole(hitPos, surfaceNormal, radius);

        var container = renderer.GetNodeOrNull<Node3D>("DecalContainer");
        TestAssert.True(container != null, "DecalContainer exists");
        TestAssert.Equal(256, container!.GetChildCount(), "Pool has 256 decal nodes");

        var firstDecal = container.GetChild<MeshInstance3D>(0);
        TestAssert.True(firstDecal.Visible, "First decal is visible after AddBulletHole");
        TestAssert.True(firstDecal.MaterialOverride is StandardMaterial3D, "Decal has StandardMaterial3D");
        TestAssert.True(firstDecal.GlobalPosition.DistanceTo(hitPos) < 0.1f, "Decal global position matches hit position (within normal offset)");
        TestAssert.True(Mathf.IsEqualApprox(firstDecal.Basis.Scale.X, 4f) && Mathf.IsEqualApprox(firstDecal.Basis.Scale.Y, 4f), "Decal basis scale matches diameter");

        TestScene.RemoveChild(renderer);
        renderer.Free();
    }
}
