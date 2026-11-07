using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Models;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies mapmodel collision (<c>mmcollide</c> and <c>ellipseboxcollide</c> ported from
/// src/engine/physics.cpp). Confirms collidable models (such as lamp-posts like objects/lantern01)
/// block the player and act as walkable/jumpable floors, while walk-through models (such as
/// objects/lamp01) do not collide.
/// </summary>
public class MapModelCollisionTest : GoTestBase
{
    public MapModelCollisionTest(Node testScene) : base(testScene) { }

    private static PhysEnt NewPlayer(Vector3 pos) => new()
    {
        O = pos,
        Vel = Vector3.Zero,
        Type = Phys.EntPlayer,
        PhysState = Phys.PhysFloor,
        Radius = 4.1f,
        XRadius = 4.1f,
        YRadius = 4.1f,
        EyeHeight = 14.0f,
        AboveEye = 1.0f,
        Yaw = 0f,
        NewPos = pos,
    };

    [Test]
    public void WalkThroughModelAllowsPassingThrough()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        // objects/lamp01 config: Collide = false
        var lampInfo = new ModelInfo
        {
            Name = "objects/lamp01",
            Collide = false,
            CollideCenter = new Vector3(-0.1f, 0.0f, -5.5f),
            CollideRadius = new Vector3(7.2f, 2.2f, 10.7f),
            RejectRadius = 18.0f,
        };

        var modelPos = new Vector3(256, 256, 520);
        phys.AddMapModel(modelPos, 0f, lampInfo);

        // Position player right where the lamp is
        var player = NewPlayer(modelPos with { Z = modelPos.Z + 14.0f });

        bool hit = phys.Collide(player, new Vector3(1, 0, 0));
        TestAssert.False(hit, "player walks right through lamp01");
        TestAssert.Equal(0, phys.CollideInside, "no internal collision on walk-through model");
    }

    [Test]
    public void CollidableBoxModelBlocksSideMovement()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        // objects/lantern01 config: Collide = true, EllipseCollide = false (OBB)
        // radius = [2, 2, 13.24], center = [0, 0, 12.47]
        var lanternInfo = new ModelInfo
        {
            Name = "objects/lantern01",
            Collide = true,
            EllipseCollide = false,
            CollideCenter = new Vector3(0.0f, 0.0f, 12.4687f),
            CollideRadius = new Vector3(2.0f, 2.0f, 13.2408f),
            RejectRadius = 25.8647f,
        };

        var modelPos = new Vector3(256, 256, 512); // placed on empty-map floor (Z=512)
        phys.AddMapModel(modelPos, 0f, lanternInfo);

        // Player placed to the right (+X) of the lantern base, moving left (-X) towards it.
        // Lantern X extent: [-2, 2]. Player radius: 4.1. Contact at X = 256 + 2 + 4.1 = 262.1
        var player = NewPlayer(new Vector3(262.0f, 256, 520));

        bool hit = phys.Collide(player, new Vector3(-1, 0, 0));
        TestAssert.True(hit, "player collides horizontally with lantern post");
        TestAssert.True(phys.CollideWall.X > 0.9f, $"collidewall faces outward (+X): {phys.CollideWall}");
    }

    [Test]
    public void PlayerCanLandAndStandOnTopOfLampPost()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        // objects/lantern01: base at 512, height extent center.z + radius.z = 12.47 + 13.24 = 25.71
        // top of lantern is at 512 + 25.71 = 537.71
        var lanternInfo = new ModelInfo
        {
            Name = "objects/lantern01",
            Collide = true,
            EllipseCollide = false,
            CollideCenter = new Vector3(0.0f, 0.0f, 12.4687f),
            CollideRadius = new Vector3(2.0f, 2.0f, 13.2408f),
            RejectRadius = 25.8647f,
        };

        var modelPos = new Vector3(256, 256, 512);
        phys.AddMapModel(modelPos, 0f, lanternInfo);

        float topZ = modelPos.Z + lanternInfo.CollideCenter.Z + lanternInfo.CollideRadius.Z;

        // Player resting directly on top of lantern post: feet at topZ, eye at topZ + eyeHeight
        var player = NewPlayer(new Vector3(256, 256, topZ + 14.0f));

        // Test FindFloor: in Sauerbraten (physics.cpp:1299), findfloor probes down by 0.1 to detect standing surface
        bool slide = false;
        Vector3 floor = Vector3.Zero;
        bool foundFloor = phys.FindFloor(player, false, Vector3.Zero, ref slide, ref floor);
        TestAssert.True(foundFloor, "findfloor detects top of lantern as floor");
        TestAssert.Equal(new Vector3(0, 0, 1), floor, "collidewall normal is upward floor (0, 0, 1)");
        TestAssert.True(floor.Z >= Phys.FloorZ, "top of lamp-post acts as valid standing floor");

        // When moving downward into the lantern top, Collide blocks downward motion
        var fallingPlayer = NewPlayer(new Vector3(256, 256, topZ + 14.0f - 0.2f));
        bool hit = phys.Collide(fallingPlayer, new Vector3(0, 0, -1), Phys.SlopeZ);
        TestAssert.True(hit, "downward penetration collision detects top of lantern");
        TestAssert.Equal(new Vector3(0, 0, 1), phys.CollideWall, "collidewall is (0, 0, 1)");
    }

    [Test]
    public void CylinderModelUsesEllipticalCollision()
    {
        var octa = new World.Octa();
        octa.EmptyMap(10);
        var phys = new OctaPhysics(octa);

        // vegetation/tree05: EllipseCollide = true, radius = 4.0
        var treeInfo = new ModelInfo
        {
            Name = "vegetation/tree05",
            Collide = true,
            EllipseCollide = true,
            CollideCenter = new Vector3(0.0f, 0.0f, 25.957f),
            CollideRadius = new Vector3(4.0f, 4.0f, 28.5059f),
            RejectRadius = 54.7559f,
        };

        var modelPos = new Vector3(256, 256, 512);
        phys.AddMapModel(modelPos, 0f, treeInfo);

        // Note: EmptyMap has floor at Z=512. With EyeHeight=14, player Z must be > 526 to be above the octree floor.
        // At Z=535, player feet are at 521 (above map floor) and within tree height (512 to 566.46).
        // For an AABB box of radius 4.0, contact along 45 deg would occur at dist = (4*sqrt(2) + 4.1) = 9.75 u (so 8.5 would hit).
        // For a cylinder of radius 4.0, contact occurs at dist = (4.0 + 4.1) = 8.1 u (so 8.5 does NOT hit, but 7.5 DOES hit).
        float diagDistMiss = 8.5f;
        var playerMiss = NewPlayer(new Vector3(256 + diagDistMiss * 0.7071f, 256 + diagDistMiss * 0.7071f, 535));
        TestAssert.False(phys.Collide(playerMiss, Vector3.Zero), "player outside cylinder radius does not hit");

        float diagDistHit = 7.5f;
        var playerHit = NewPlayer(new Vector3(256 + diagDistHit * 0.7071f, 256 + diagDistHit * 0.7071f, 535));
        TestAssert.True(phys.Collide(playerHit, Vector3.Zero), "player inside cylinder radius hits");
    }
}
