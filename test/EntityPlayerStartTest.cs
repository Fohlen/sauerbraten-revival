using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

/// <summary>Verifies ET_PLAYERSTART → <see cref="SpawnPoint"/> conversion.</summary>
public class EntityPlayerStartTest : GoTestBase
{
    public EntityPlayerStartTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsSpawnPoints()
    {
        var map = TestData.ReadMap();
        var root = PlayerStartBuilder.Build(map);

        int expected = 0, built = 0;
        for (int i = 0; i < map.Entities.Count; i++)
        {
            var entity = map.Entities[i];
            if (entity.Type != EntityType.PlayerStart) continue;
            expected++;

            var spawn = root.GetNodeOrNull<SpawnPoint>($"PlayerStart_{i}");
            TestAssert.True(spawn != null, $"PlayerStart_{i} exists");
            TestAssert.True(spawn!.Position.DistanceTo(entity.Position) < 0.01f, $"{spawn.Name} position");
            TestAssert.Equal((int)entity.Attr2, spawn.Team, $"{spawn.Name} team");
            TestAssert.True(Mathf.Abs(spawn.Yaw - entity.Attr1) < 0.01f, $"{spawn.Name} yaw");

            var forward = PlayerStartBuilder.Forward(entity.Attr1).Normalized();
            var facing = -spawn.Basis.Z;
            TestAssert.True(facing.DistanceTo(forward) < 0.001f, $"{spawn.Name} facing");
            built++;
        }

        TestAssert.Equal(32, expected, "playerstart entities");
        TestAssert.Equal(expected, built, "spawn points built");
        root.Free();
    }
}
