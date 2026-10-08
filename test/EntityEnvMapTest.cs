using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies ET_ENVMAP representation. earthstation places 38 envmap entities with
/// attr2 = 6 (cubemap size) and attr3 = 0 (no blur); attr1 (radius) is a mix of 0
/// (engine default) and custom values (10..120).
/// </summary>
public class EntityEnvMapTest : GoTestBase
{
    public EntityEnvMapTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsEnvMapProbes()
    {
        var map = TestData.ReadMap("earthstation");
        var root = EnvMapBuilder.Build(map);

        int count = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is not EnvMapProbe probe) continue;

            int index = int.Parse(probe.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];

            TestAssert.Equal((int)entity.Attr1, probe.Attr1, $"{probe.Name} attr1");
            TestAssert.Equal((int)entity.Attr2, probe.Attr2, $"{probe.Name} attr2");
            TestAssert.Equal((int)entity.Attr3, probe.Attr3, $"{probe.Name} attr3");

            TestAssert.Equal(EnvMapBuilder.ResolveRadius(entity), probe.Radius, $"{probe.Name} radius");
            TestAssert.Equal(EnvMapBuilder.ResolveCubeMapSize(entity), probe.CubeMapSize, $"{probe.Name} size");
            TestAssert.Equal(EnvMapBuilder.ResolveBlur(entity), probe.Blur, $"{probe.Name} blur");

            TestAssert.True(probe.Position.DistanceTo(entity.Position) < 0.01f, $"{probe.Name} position");
            count++;
        }

        TestAssert.Equal(38, count, "envmap entities");
        root.Free();
    }

    [Test]
    public void ResolvesEngineDefaults()
    {
        var map = TestData.ReadMap("earthstation");
        var root = EnvMapBuilder.Build(map);

        // Every entity here has attr2 = 6, so the resolved size is min(6, envmapsize=7) = 6.
        int defaults = 0, custom = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is not EnvMapProbe probe) continue;
            TestAssert.Equal(6, probe.CubeMapSize, $"{probe.Name} cubemap size");
            TestAssert.Equal(0, probe.Blur, $"{probe.Name} blur");
            if (probe.Attr1 == 0)
            {
                TestAssert.Equal(EnvMapBuilder.DefaultRadius, probe.Radius, $"{probe.Name} default radius");
                defaults++;
            }
            else
            {
                TestAssert.Equal(probe.Attr1, probe.Radius, $"{probe.Name} custom radius");
                custom++;
            }
        }

        TestAssert.Equal(38, defaults + custom, "all envmaps classified");
        TestAssert.True(custom > 0, "earthstation has custom radii");
        root.Free();
    }

    [Test]
    public void ProbeConfiguresItself()
    {
        var map = TestData.ReadMap("earthstation");
        var root = EnvMapBuilder.Build(map);

        var probe = (EnvMapProbe)root.GetChild(0);

        // _Ready runs on AddChild when the node is in the tree; force it here since
        // the fixture root is not added to the scene tree.
        probe._Ready();

        float extent = Mathf.Max(2.0f * probe.Radius, 1.0f);
        TestAssert.True(Mathf.Abs(probe.Size.X - extent) < 0.01f, "probe box extent");
        TestAssert.True(Mathf.Abs(probe.MaxDistance - Mathf.Max(probe.Radius, 1.0f)) < 0.01f, "max distance");
        TestAssert.Equal((int)ReflectionProbe.UpdateModeEnum.Once, (int)probe.UpdateMode, "update mode");
        TestAssert.True(probe.BoxProjection, "box projection");
        TestAssert.Equal(6, probe.CubeMapSize, "resolution exponent");
        TestAssert.Equal(64, probe.CubeMapResolution, "1 << 6");
        root.Free();
    }
}
