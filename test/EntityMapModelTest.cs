using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.Tests;

/// <summary>Verifies ET_MAPMODEL placement (path resolution + position/yaw).</summary>
public class EntityMapModelTest : TestClass
{
    public EntityMapModelTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsMapModels()
    {
        var map = TestData.ReadMap();
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePath);
        var root = MapModelBuilder.Build(map, catalogue);

        int expected = 0, built = 0;
        for (int i = 0; i < map.Entities.Count; i++)
        {
            var entity = map.Entities[i];
            if (entity.Type != EntityType.MapModel) continue;
            expected++;

            var node = root.GetNodeOrNull<MapModel>($"MapModel_{i}");
            TestAssert.True(node != null, $"MapModel_{i} exists");
            TestAssert.True(node!.Position.DistanceTo(entity.Position) < 0.01f, $"{node.Name} position");
            TestAssert.True(Mathf.Abs(node.Yaw - entity.Attr1) < 0.01f, $"{node.Name} yaw");
            TestAssert.Equal(catalogue.MapModelPath(entity.Attr2) ?? "", node.ModelPath, $"{node.Name} path");
            built++;
        }

        TestAssert.Equal(11, expected, "mapmodel entities");
        TestAssert.Equal(expected, built, "mapmodels built");
    }
}
