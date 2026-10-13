using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies item entity representation (ET_SHELLS…ET_QUAD / entmdlname).
/// church51 has 24 items: 8 health, 3+3 shells/bullets, 2+2 rockets/riflerounds,
/// 2 grenades, and one each healthboost/greenarmour/yellowarmour/quaddamage.
/// </summary>
public class EntityItemTest : GoTestBase
{
    public EntityItemTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsItems()
    {
        var map = TestData.ReadMap();
        var root = ItemBuilder.Build(map);

        int expected = 0, built = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is not Item item) continue;
            int index = int.Parse(item.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];

            TestAssert.Equal(ItemBuilder.ModelName(entity.Type), ItemBuilder.ModelName((byte)item.ItemType), $"{item.Name} model");
            TestAssert.Equal((int)entity.Type, item.ItemType, $"{item.Name} type");
            TestAssert.Equal(EntityType.Name(entity.Type), item.ItemName, $"{item.Name} name");
            TestAssert.True(item.Position.DistanceTo(entity.Position) < 0.01f, $"{item.Name} position");
            built++;
        }

        for (int i = 0; i < map.Entities.Count; i++)
            if (ItemBuilder.ModelName(map.Entities[i].Type) != null) expected++;

        TestAssert.Equal(24, expected, "item entities");
        TestAssert.Equal(expected, built, "items built");
        root.Free();
    }

    [Test]
    public void ItemModelNames()
    {
        TestAssert.Equal("health", ItemBuilder.ModelName(EntityType.Health), "health");
        TestAssert.Equal("boost", ItemBuilder.ModelName(EntityType.HealthBoost), "boost");
        TestAssert.Equal("ammo/shells", ItemBuilder.ModelName(EntityType.Shells), "shells");
        TestAssert.Equal("ammo/cartridges", ItemBuilder.ModelName(EntityType.Cartridges), "cartridges");
        TestAssert.Equal("armor/green", ItemBuilder.ModelName(EntityType.GreenArmour), "green armour");
        TestAssert.Equal("armor/yellow", ItemBuilder.ModelName(EntityType.YellowArmour), "yellow armour");
        TestAssert.Equal("quad", ItemBuilder.ModelName(EntityType.QuadDamage), "quad");
        TestAssert.True(ItemBuilder.ModelName(EntityType.Light) == null, "lights are not items");
    }

    [Test]
    public void ItemsOrientUprightInZUpWorld()
    {
        var map = TestData.ReadMap();
        var root = ItemBuilder.Build(map);

        Basis expectedBasis = Basis.LookingAt(new Vector3(0, 1, 0), new Vector3(0, 0, 1));
        foreach (var child in root.GetChildren())
        {
            if (child is not Item item) continue;
            TestAssert.True(item.Basis.IsEqualApprox(expectedBasis),
                $"{item.Name} basis ({item.Basis}) stands upright in Z-up world (yaw 0)");
        }
        root.Free();
    }
}
