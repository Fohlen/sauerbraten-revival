using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game.Monsters;
using Monster = SauerbratenRevival.World.Game.Monsters.Monster;
using MonsterViewWeapon = SauerbratenRevival.World.Game.Monsters.MonsterViewWeapon;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies the MONSTER spawn representation. crnsp1 places 220 monsters covering
/// all 9 monster types; lost places 235 (types 0–5).
/// </summary>
public class EntityMonsterTest : GoTestBase
{
    public EntityMonsterTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsAllMonsterTypesFromCrnsp1()
    {
        var map = TestData.ReadMap("crnsp1");
        var root = MonsterBuilder.Build(map);

        int count = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        foreach (var child in root.GetChildren())
        {
            if (child is not Monster m) continue;
            int index = int.Parse(m.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];

            TestAssert.Equal((int)entity.Attr2, m.MonsterType, $"{m.Name} type");
            TestAssert.Equal((int)entity.Attr3, m.Tag, $"{m.Name} tag");
            TestAssert.Equal((int)entity.Attr1, (int)m.Yaw, $"{m.Name} yaw");

            var info = MonsterTypes.Get(entity.Attr2)!;
            TestAssert.Equal(info.ModelName, m.ModelPath, $"{m.Name} model");
            TestAssert.Equal(info.Name, m.DisplayName, $"{m.Name} display name");
            TestAssert.Equal(info.Gun, m.Gun, $"{m.Name} gun");
            TestAssert.Equal(info.Health, m.Health, $"{m.Name} health");
            TestAssert.Equal(info.Speed, m.Speed, $"{m.Name} speed");
            TestAssert.Equal(info.Weight, m.Weight, $"{m.Name} weight");
            TestAssert.Equal(info.BScale, m.BScale, $"{m.Name} bscale");

            float scale = info.BScale / 10.0f;
            TestAssert.True(Mathf.Abs(m.Radius - MonsterBuilder.BaseRadius * scale) < 0.01f, $"{m.Name} radius");
            TestAssert.True(Mathf.Abs(m.EyeHeight - 8.0f * scale) < 0.01f, $"{m.Name} eyeheight");
            TestAssert.True(Mathf.Abs(m.AboveEye - 7.0f * scale) < 0.01f, $"{m.Name} aboveeye");

            seen.Add(m.MonsterType);
            count++;
        }

        TestAssert.Equal(220, count, "monsters on crnsp1");
        TestAssert.Equal(9, seen.Count, "all 9 monster types covered");
        root.Free();
    }

    [Test]
    public void AttachesViewWeapons()
    {
        var map = TestData.ReadMap("crnsp1");
        var root = MonsterBuilder.Build(map);

        var vwepTypes = new System.Collections.Generic.HashSet<int>();
        var noVwepTypes = new System.Collections.Generic.HashSet<int>();
        int withVwep = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is not Monster m) continue;

            MonsterViewWeapon? vwep = null;
            foreach (var c in m.GetChildren())
                if (c is MonsterViewWeapon v) { vwep = v; break; }

            if (m.ViewWeapon == null)
            {
                TestAssert.True(vwep == null, $"{m.Name} has no view weapon");
                noVwepTypes.Add(m.MonsterType);
            }
            else
            {
                TestAssert.True(vwep != null, $"{m.Name} has a view-weapon node");
                TestAssert.Equal(m.ViewWeapon, vwep!.ModelPath, $"{m.Name} view-weapon path");
                vwepTypes.Add(m.MonsterType);
                withVwep++;
            }
        }

        // 6 of the 9 monster types carry a view weapon (rhino/hellpig/spider do not).
        TestAssert.Equal(3, noVwepTypes.Count, "types without a view weapon");
        TestAssert.Equal(6, vwepTypes.Count, "types with a view weapon");
        TestAssert.True(withVwep > 0, "some monsters have view weapons");
        root.Free();
    }

    [Test]
    public void MonsterTypeTableMatchesEngine()
    {
        TestAssert.Equal(9, MonsterTypes.Count, "NUMMONSTERTYPES");

        // Spot-check a few rows against monster.cpp monstertypes.
        var ogro = MonsterTypes.Get(0)!;
        TestAssert.Equal("ogro", ogro.ModelName, "ogro model");
        TestAssert.Equal(100, ogro.Health, "ogro health");
        TestAssert.Equal(15, ogro.Speed, "ogro speed");
        TestAssert.Equal(90, ogro.Weight, "ogro weight");
        TestAssert.Equal(10, ogro.BScale, "ogro bscale");

        var bauul = MonsterTypes.Get(4)!;
        TestAssert.Equal("monster/bauul", bauul.ModelName, "bauul model");
        TestAssert.Equal(500, bauul.Health, "bauul health");
        TestAssert.Equal(24, bauul.BScale, "bauul bscale");

        var spider = MonsterTypes.Get(8)!;
        TestAssert.Equal("monster/spider", spider.ModelName, "spider model");

        // View-weapon names (monster.cpp vwepname column); three types have none.
        TestAssert.Equal("ogro/vwep", ogro.ViewWeapon, "ogro vwep");
        TestAssert.Equal("monster/bauul/vwep", bauul.ViewWeapon, "bauul vwep");
        TestAssert.True(MonsterTypes.Get(1)!.ViewWeapon == null, "rhino has no vwep");
        TestAssert.True(MonsterTypes.Get(5)!.ViewWeapon == null, "hellpig has no vwep");
        TestAssert.True(spider.ViewWeapon == null, "spider has no vwep");

        TestAssert.True(MonsterTypes.Get(-1) == null, "negative type is null");
        TestAssert.True(MonsterTypes.Get(9) == null, "out-of-range type is null");
    }

    [Test]
    public void UnknownTypeFallsBackToOgro()
    {
        var map = TestData.ReadMap("lost");
        map.Entities.Add(new World.Runtime.Formats.Ogz.OgzEntity { Type = EntityType.Monster, Attr1 = 90, Attr2 = 200, Attr3 = 7 });

        var warnings = new System.Collections.Generic.List<string>();
        var root = MonsterBuilder.Build(map, warn: warnings.Add);
        Monster? last = null;
        foreach (var child in root.GetChildren())
            if (child is Monster m) last = m;

        TestAssert.True(last != null, "built a monster");
        TestAssert.Equal(0, last!.MonsterType, "clamped to type 0");
        TestAssert.Equal("ogro", last.ModelPath, "ogro model");
        TestAssert.Equal(7, last.Tag, "tag preserved");
        // The fallback must still report the unknown type exactly once.
        TestAssert.Equal(1, warnings.Count, "one warning emitted");
        TestAssert.True(warnings[0].Contains("unknown monster type 200"), "warning names the bad type");
        root.Free();
    }
}
