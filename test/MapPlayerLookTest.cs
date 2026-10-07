using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the mouse-look sign convention: moving the cursor up/right must look
/// up/right, on screen. Guards against the up=down / left=right inversion.
/// </summary>
public class MapPlayerLookTest : TestClass
{
    public MapPlayerLookTest(Node testScene) : base(testScene) { }

    [Test]
    public void MouseLookMatchesScreenDirections()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = new MapPlayer();
        player.Setup(octa, new Vector3(3000, 946, 3040), 0f);
        var cam = new Camera3D();
        player.AddChild(cam);
        var world = new Node3D { RotationDegrees = new Vector3(-90, 0, 0) }; // Z-up -> Y-up
        world.AddChild(player);
        TestScene.AddChild(world);

        // Godot convention: right = +X, down = +Y.
        Vector3 LookAfter(Vector2 delta)
        {
            player.SetView(0f, 0f);
            player.ApplyMouseDelta(delta);
            return -cam.GlobalTransform.Basis.Z; // world-space forward
        }

        Vector3 up = LookAfter(new Vector2(0f, -20f));   // cursor up
        Vector3 down = LookAfter(new Vector2(0f, 20f));  // cursor down
        Vector3 right = LookAfter(new Vector2(20f, 0f)); // cursor right
        Vector3 left = LookAfter(new Vector2(-20f, 0f)); // cursor left

        GD.Print($"[look] up={up} down={down} right={right} left={left}");
        TestAssert.True(up.Y > 1e-3f, "cursor up looks up");
        TestAssert.True(down.Y < -1e-3f, "cursor down looks down");
        TestAssert.True(right.X > 1e-3f, "cursor right looks screen-right (+X)");
        TestAssert.True(left.X < -1e-3f, "cursor left looks screen-left (-X)");

        world.QueueFree();
    }
}
