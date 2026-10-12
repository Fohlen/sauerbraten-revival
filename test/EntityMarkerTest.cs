using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

public class EntityMarkerTest : GoTestBase
{
    public EntityMarkerTest(Node testScene) : base(testScene) { }

    [Test]
    public void SetVisibleTogglesNodeSubtree()
    {
        var root = new Node3D();
        var entity = new Node3D { Name = "Spawn" };
        root.AddChild(entity);

        EntityMarker.Add(entity, null, arrow: true);

        var marker = entity.GetNodeOrNull<Sprite3D>("EntityMarker");
        var arrow = entity.GetNodeOrNull<MeshInstance3D>("EntityArrow");

        TestAssert.True(marker != null, "marker exists");
        TestAssert.True(arrow != null, "arrow exists");
        TestAssert.True(marker!.Visible, "marker initially visible");
        TestAssert.True(arrow!.Visible, "arrow initially visible");

        EntityMarker.SetVisible(root, false);
        TestAssert.True(!marker.Visible, "marker hidden after SetVisible(false)");
        TestAssert.True(!arrow.Visible, "arrow hidden after SetVisible(false)");

        EntityMarker.SetVisible(root, true);
        TestAssert.True(marker.Visible, "marker visible after SetVisible(true)");
        TestAssert.True(arrow.Visible, "arrow visible after SetVisible(true)");

        root.Free();
    }

    [Test]
    public void MapWorldRespectsShowEntityMarkers()
    {
        var map = TestData.ReadMap();
        var spawnRoot = PlayerStartBuilder.Build(map);

        EntityMarker.SetVisible(spawnRoot, false);

        int checkedCount = 0;
        foreach (var child in spawnRoot.GetChildren())
        {
            if (child is SpawnPoint spawn)
            {
                var marker = spawn.GetNodeOrNull<Sprite3D>("EntityMarker");
                var arrow = spawn.GetNodeOrNull<MeshInstance3D>("EntityArrow");
                TestAssert.True(marker != null, "marker exists on spawn");
                TestAssert.True(!marker!.Visible, "marker is hidden");
                if (arrow != null)
                {
                    TestAssert.True(!arrow.Visible, "arrow is hidden");
                }
                checkedCount++;
            }
        }

        TestAssert.True(checkedCount > 0, "verified spawns");
        spawnRoot.Free();
    }
}
