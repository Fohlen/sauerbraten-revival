using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies trigger entity representation (ET_TELEPORT / ET_TELEDEST / ET_JUMPPAD).
/// church51 has 8 teleports, 8 destinations, 5 jumppads; all teleports pair.
/// </summary>
public class EntityTriggerTest : GoTestBase
{
    public EntityTriggerTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsTriggerEntities()
    {
        var root = TriggerBuilder.Build(TestData.ReadMap(), MetadataCatalogue.Load(TestData.CataloguePath));

        int teleports = 0, destinations = 0, jumpPads = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is Teleport) teleports++;
            else if (child is TeleDest) destinations++;
            else if (child is JumpPad) jumpPads++;
        }

        TestAssert.Equal(8, teleports, "teleports");
        TestAssert.Equal(8, destinations, "teledests");
        TestAssert.Equal(5, jumpPads, "jumppads");
        root.Free();
    }

    [Test]
    public void AttributesMatchEntities()
    {
        var map = TestData.ReadMap();
        var root = TriggerBuilder.Build(map, null);

        int checkedNodes = 0;
        foreach (var child in root.GetChildren())
        {
            int index = int.Parse(child.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];

            switch (child)
            {
                case Teleport teleport:
                    TestAssert.Equal((int)entity.Attr1, teleport.Tag, $"{child.Name} tag");
                    TestAssert.Equal((int)entity.Attr2, teleport.ModelIndex, $"{child.Name} model");
                    TestAssert.Equal((int)entity.Attr3, teleport.Hook, $"{child.Name} hook");
                    TestAssert.Equal((int)entity.Attr4, teleport.Sound, $"{child.Name} sound");
                    TestAssert.True(teleport.ModelPath == null, $"{child.Name} is invisible (attr2 < 0)");
                    break;

                case TeleDest dest:
                    TestAssert.Equal((int)entity.Attr2, dest.Tag, $"{child.Name} tag");
                    TestAssert.True(Mathf.Abs(dest.Yaw - entity.Attr1) < 0.01f, $"{child.Name} yaw");
                    TestAssert.Equal(entity.Attr3 > 0, dest.Pushed, $"{child.Name} pushed");
                    break;

                case JumpPad pad:
                    TestAssert.True(pad.Push.DistanceTo(TriggerBuilder.Push(entity)) < 0.01f, $"{child.Name} push");
                    TestAssert.Equal((int)entity.Attr4, pad.Sound, $"{child.Name} sound");
                    break;
            }
            checkedNodes++;
        }

        TestAssert.Equal(21, checkedNodes, "checked every trigger entity");
        root.Free();
    }

    [Test]
    public void TeleportsPairWithDestinations()
    {
        var root = TriggerBuilder.Build(TestData.ReadMap(), null);

        int paired = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is not Teleport teleport) continue;
            TestAssert.True(teleport.Destination != null, $"{teleport.Name} has a destination");
            TestAssert.Equal(teleport.Tag, teleport.Destination!.Tag, $"{teleport.Name} destination tag");
            paired++;
        }
        TestAssert.Equal(8, paired, "all teleports paired");
        root.Free();
    }
}
