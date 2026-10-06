using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies entity light -> Godot light conversion (ET_LIGHT / ET_SPOTLIGHT).
/// church51 has 119 lights and 8 spotlights (all 8 attach).
/// </summary>
public class EntityLightsTest : GoTestBase
{
    public EntityLightsTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsLightsFromEntities()
    {
        var root = LightBuilder.Build(TestData.ReadMap());

        int omni = 0, spot = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is SpotLight3D) spot++;
            else if (child is OmniLight3D) omni++;
        }

        TestAssert.Equal(111, omni, "omni lights");
        TestAssert.Equal(8, spot, "spot lights");
        TestAssert.Equal(119, omni + spot, "total entity lights");
        root.Free();
    }

    [Test]
    public void LightAttributesMatchEntities()
    {
        var map = TestData.ReadMap();
        var root = LightBuilder.Build(map);

        // The cone angle comes from the attached ET_SPOTLIGHT entity's attr1.
        var spotAngles = new System.Collections.Generic.HashSet<float>();
        foreach (var e in map.Entities)
            if (e.Type == EntityType.SpotLight) spotAngles.Add(Mathf.Clamp(e.Attr1, 1, 89));

        int checkedLights = 0, spotAngleChecked = 0;
        foreach (var child in root.GetChildren())
        {
            if (child is not Light3D light) continue;
            int index = int.Parse(light.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];
            float expectedRange = entity.Attr1 > 0 ? entity.Attr1 : LightBuilder.InfiniteRange;

            switch (light)
            {
                case OmniLight3D omni:
                    TestAssert.True(Mathf.Abs(omni.OmniRange - expectedRange) < 0.01f, $"{light.Name} range");
                    break;
                case SpotLight3D spot:
                    TestAssert.True(Mathf.Abs(spot.SpotRange - expectedRange) < 0.01f, $"{light.Name} range");
                    TestAssert.True(spotAngles.Contains(spot.SpotAngle), $"{light.Name} angle is a map spotlight angle");
                    spotAngleChecked++;
                    break;
            }

            var expected = new Color(entity.Attr2 / 255.0f, entity.Attr3 / 255.0f, entity.Attr4 / 255.0f);
            TestAssert.True(Mathf.Abs(light.LightColor.R - expected.R) < 0.01f, $"{light.Name} r");
            TestAssert.True(Mathf.Abs(light.LightColor.G - expected.G) < 0.01f, $"{light.Name} g");
            TestAssert.True(Mathf.Abs(light.LightColor.B - expected.B) < 0.01f, $"{light.Name} b");
            checkedLights++;
        }

        TestAssert.Equal(119, checkedLights, "checked every light");
        TestAssert.Equal(8, spotAngleChecked, "checked every spotlight angle");
        root.Free();
    }
}
