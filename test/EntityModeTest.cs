using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Runtime.Formats.Ogz;
using ModeEntity = SauerbratenRevival.World.Game.ModeEntity;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies the game-mode entity representation. bt_falls places 5 bases and 2
/// flags (the colour models resolve through the model import); lost places 57
/// boxes, 114 barrels, 11 platforms and 13 elevators plus a flag and respawn
/// points (the movable attributes).
/// </summary>
public class EntityModeTest : GoTestBase
{
    public EntityModeTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsFlagsFromBtFalls()
    {
        var map = TestData.ReadMap("bt_falls");
        var root = ModeEntityBuilder.Build(map);

        int flags = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is not ModeEntity { ModeType: EntityType.Flag } flag) continue;
            int index = int.Parse(flag.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];

            TestAssert.Equal((int)entity.Attr1, flag.Attr1, $"{flag.Name} attr1");
            TestAssert.Equal((int)entity.Attr2, flag.Attr2, $"{flag.Name} attr2");
            TestAssert.Equal((int)entity.Attr2, flag.Team, $"{flag.Name} team");
            TestAssert.Equal((int)entity.Attr1, (int)flag.Yaw, $"{flag.Name} yaw");
            TestAssert.Equal(ModeEntityBuilder.FlagModel(entity.Attr2), flag.ModelPath, $"{flag.Name} model");
            flags++;
        }

        TestAssert.Equal(2, flags, "flags on bt_falls");
        root.Free();
    }

    [Test]
    public void BuildsBasesFromBtFalls()
    {
        var map = TestData.ReadMap("bt_falls");
        var root = ModeEntityBuilder.Build(map);

        int bases = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is not ModeEntity { ModeType: EntityType.Base } b) continue;
            int index = int.Parse(b.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];

            TestAssert.Equal((int)entity.Attr1, b.Tag, $"{b.Name} ammo type");
            TestAssert.Equal((int)entity.Attr2, b.BaseIndex, $"{b.Name} base index");
            TestAssert.Equal("base/neutral", b.ModelPath, $"{b.Name} model");
            bases++;
        }

        TestAssert.Equal(5, bases, "bases on bt_falls");
        root.Free();
    }

    // FLAG/BASE are mode-owned: entities.cpp entmodel() returns NULL for both and
    // client.cpp setclientmode() picks capture (bases) or ctf (flags), so other
    // modes must not draw them. A null mode keeps the raw representation (above).

    [Test]
    public void FfaDrawsNeitherFlagsNorBases()
    {
        var map = TestData.ReadMap("bt_falls");
        var root = ModeEntityBuilder.Build(map, mode: GameModeCatalog.Get("ffa"));

        TestAssert.Equal(0, CountType(root, EntityType.Flag), "flags in ffa");
        TestAssert.Equal(0, CountType(root, EntityType.Base), "bases in ffa");
        root.Free();
    }

    [Test]
    public void CtfDrawsNoBasesAndLeavesFlagsToFlagNode()
    {
        var map = TestData.ReadMap("bt_falls");
        var root = ModeEntityBuilder.Build(map, mode: GameModeCatalog.Get("effic_ctf"));

        TestAssert.Equal(0, CountType(root, EntityType.Base), "bases in effic ctf");
        TestAssert.Equal(0, CountType(root, EntityType.Flag), "static flags in effic ctf (FlagNode owns them)");
        root.Free();
    }

    [Test]
    public void CaptureDrawsBasesOnly()
    {
        var map = TestData.ReadMap("bt_falls");
        var root = ModeEntityBuilder.Build(map, mode: GameModeCatalog.Get("capture"));

        TestAssert.Equal(5, CountType(root, EntityType.Base), "bases in capture");
        TestAssert.Equal(0, CountType(root, EntityType.Flag), "flags in capture");
        root.Free();
    }

    [Test]
    public void HoldDrawsNeitherBasesNorFlags()
    {
        // Hold turns BASE entities into (invisible) hold spawns and creates its
        // single neutral flag dynamically (ctf.h:729).
        var map = TestData.ReadMap("bt_falls");
        var root = ModeEntityBuilder.Build(map, mode: GameModeCatalog.Get("hold"));

        TestAssert.Equal(0, CountType(root, EntityType.Base), "bases in hold");
        TestAssert.Equal(0, CountType(root, EntityType.Flag), "flags in hold");
        root.Free();
    }

    [Test]
    public void ActiveModeKeepsGenericModeEntities()
    {
        var map = TestData.ReadMap("lost");
        var root = ModeEntityBuilder.Build(map, mode: GameModeCatalog.Get("ffa"));

        TestAssert.Equal(57, CountType(root, EntityType.Box), "boxes in ffa");
        TestAssert.Equal(114, CountType(root, EntityType.Barrel), "barrels in ffa");
        root.Free();
    }

    [Test]
    public void BuildsMovablesFromLost()
    {
        var map = TestData.ReadMap("lost");
        var root = ModeEntityBuilder.Build(map);

        int box = 0, barrel = 0, platform = 0, elevator = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is not ModeEntity m) continue;
            int index = int.Parse(m.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];

            switch (m.ModeType)
            {
                case EntityType.Box:
                    TestAssert.Equal(entity.Attr3 != 0 ? entity.Attr3 : ModeEntityBuilder.BoxWeight, m.Weight, $"{m.Name} weight");
                    box++;
                    break;
                case EntityType.Barrel:
                    TestAssert.Equal(entity.Attr3 != 0 ? entity.Attr3 : ModeEntityBuilder.BarrelWeight, m.Weight, $"{m.Name} weight");
                    TestAssert.Equal(entity.Attr4 != 0 ? entity.Attr4 : ModeEntityBuilder.BarrelHealth, m.Health, $"{m.Name} health");
                    barrel++;
                    break;
                case EntityType.Platform:
                case EntityType.Elevator:
                    TestAssert.Equal((int)entity.Attr3, m.Tag, $"{m.Name} tag");
                    float expected = entity.Attr4 != 0 ? Mathf.Abs(entity.Attr4) : ModeEntityBuilder.PlatformSpeed;
                    TestAssert.True(Mathf.Abs(m.Speed - expected) < 0.01f, $"{m.Name} speed");
                    TestAssert.Equal(entity.Attr4 < 0 ? -1 : 1, m.Direction, $"{m.Name} direction");
                    TestAssert.True(m.Weight == 1000, $"{m.Name} platform weight");
                    if (m.ModeType == EntityType.Platform) platform++;
                    else elevator++;
                    break;
            }
        }

        TestAssert.Equal(57, box, "boxes");
        TestAssert.Equal(114, barrel, "barrels");
        TestAssert.Equal(11, platform, "platforms");
        TestAssert.Equal(13, elevator, "elevators");
        root.Free();
    }

    [Test]
    public void DirectionFollowsSpeedSign()
    {
        // A negative attr4 means the platform runs in reverse (movable.cpp).
        var map = TestData.ReadMap("lost");
        var negative = new MapEntity { Type = EntityType.Elevator, Attr1 = 0, Attr2 = 1, Attr3 = 3, Attr4 = -12 };
        var positive = new MapEntity { Type = EntityType.Elevator, Attr1 = 0, Attr2 = 1, Attr3 = 3, Attr4 = 12 };
        map.Entities.Add(negative);
        map.Entities.Add(positive);

        var root = ModeEntityBuilder.Build(map);
        var nodes = new System.Collections.Generic.List<ModeEntity>();
        foreach (var child in root.GetChildren())
            if (child is ModeEntity m) nodes.Add(m);

        var neg = nodes[^2];
        var pos = nodes[^1];
        TestAssert.Equal(-1, neg.Direction, "negative attr4 -> reverse");
        TestAssert.Equal(1, pos.Direction, "positive attr4 -> forward");
        TestAssert.True(Mathf.Abs(neg.Speed - 12.0f) < 0.01f, "abs speed");
        root.Free();
    }

    [Test]
    public void ResolvesModelsThroughCatalogue()
    {
        // Movables use mapmodelname(attr2), which needs the metadata catalogue.
        var map = TestData.ReadMap("lost");
        var catalogue = MetadataCatalogue.Load(TestData.CataloguePathFor("bt_falls"));

        var box = map.Entities.FindIndex(e => e.Type == EntityType.Box);
        TestAssert.True(box >= 0, "lost has a box");

        var root = ModeEntityBuilder.Build(map, catalogue);
        foreach (var child in root.GetChildren())
        {
            if (child is not ModeEntity { ModeType: EntityType.Box } m) continue;
            int index = int.Parse(m.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];
            string? expected = entity.Attr2 > 0 ? catalogue.MapModelPath(entity.Attr2) : null;
            TestAssert.Equal(expected, m.ModelPath, $"{m.Name} model path");
            break;
        }
        root.Free();
    }

    [Test]
    public void ArrowsMatchEntradius()
    {
        // entities.cpp entradius draws a facing arrow for these types.
        byte[] arrowed = { EntityType.Flag, EntityType.RespawnPoint, EntityType.Box, EntityType.Barrel, EntityType.Platform, EntityType.Elevator };
        foreach (var t in arrowed)
            TestAssert.True(ModeEntityBuilder.DrawsFacingArrow(t), $"{EntityType.Name(t)} arrow");

        TestAssert.True(!ModeEntityBuilder.DrawsFacingArrow(EntityType.Base), "base has no arrow");
        TestAssert.True(!ModeEntityBuilder.DrawsFacingArrow(EntityType.Carrot), "carrot has no arrow");
    }

    private static int CountType(Node root, byte type)
    {
        int count = 0;
        foreach (var child in root.GetChildren())
            if (child is ModeEntity m && m.ModeType == type) count++;
        return count;
    }
}
