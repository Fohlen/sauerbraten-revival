using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Lighting;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies that <see cref="MapEnvironment"/> materializes a streamed
/// <see cref="SetEnvironmentCmd"/> onto runtime scene nodes (ADR 0009 §6): the sun
/// is a <see cref="DirectionalLight3D"/> using the engine's
/// <c>sunlightyaw</c>/<c>sunlightpitch</c> direction, and sky/ambient/fog land on a
/// <see cref="WorldEnvironment"/>.
/// </summary>
public class MapEnvironmentTest : GoTestBase
{
    public MapEnvironmentTest(Node testScene) : base(testScene) { }

    private static Node3D NewMapNode(Node parent)
    {
        // Same Z-up -> Y-up transform MapLevel applies before materializing nodes.
        var host = new Node3D
        {
            Name = "MapNode",
            RotationDegrees = new Vector3(-90, 0, 0),
            Scale = new Vector3(-1, 1, 1),
        };
        parent.AddChild(host);
        return host;
    }

    [Test]
    public void ApplyMaterializesSunAndEnvironment()
    {
        Node3D host = NewMapNode(TestScene);
        var env = new SetEnvironmentCmd
        {
            SunColor = 0xFFAA55,
            SunScale = 1.5f,
            SunYaw = 0f,
            SunPitch = 90f,
            SkyColor = 0x102030,
            AmbientColor = 0x202020,
            FogDistance = 2048f,
            FogColor = 0x334455,
        };

        MapEnvironment.Apply(host, env, textures: null);

        var sun = host.GetNodeOrNull<DirectionalLight3D>(MapEnvironment.SunNodeName);
        TestAssert.True(sun != null, "sun node materialized");
        TestAssert.True(sun!.Visible, "sun enabled");
        TestAssert.Equal(1.5f, sun.LightEnergy, "energy is sunlightscale");
        TestAssert.True(sun.LightColor.IsEqualApprox(RadianceVolume.DecodeColor(0xFFAA55)), "colour is sunlight");

        var worldEnvironment = host.GetNodeOrNull<WorldEnvironment>(MapEnvironment.EnvironmentNodeName);
        TestAssert.True(worldEnvironment != null, "world environment materialized");
        Godot.Environment e = worldEnvironment!.Environment;
        TestAssert.Equal(Godot.Environment.BGMode.Sky, e.BackgroundMode, "sky background");
        TestAssert.True(e.Sky != null, "sky material present");
        TestAssert.True(e.FogEnabled, "fog enabled when fog_distance > 0");
        TestAssert.Equal(2048f, e.FogDepthEnd, "fog distance is the depth end");
        TestAssert.True(e.AmbientLightSource == Godot.Environment.AmbientSource.Sky, "ambient comes from the sky");

        host.QueueFree();
    }

    [Test]
    public void SunFollowsEngineSunlightDirection()
    {
        Node3D host = NewMapNode(TestScene);
        var env = new SetEnvironmentCmd
        {
            SunColor = 0xFFFFFF,
            SunScale = 1f,
            SunYaw = 0f,
            SunPitch = 90f, // straight overhead in Sauerbraten Z-up
        };

        MapEnvironment.Apply(host, env, textures: null);

        var sun = host.GetNodeOrNull<DirectionalLight3D>(MapEnvironment.SunNodeName)!;
        // A DirectionalLight3D shines along its global -Z. pitch=90 puts the sun
        // overhead, so the light must travel downward in Godot Y-up space.
        Vector3 lightDirection = -sun.GlobalTransform.Basis.Z.Normalized();
        TestAssert.True(lightDirection.Dot(Vector3.Down) > 0.99f,
            $"pitch=90 shines straight down (was {lightDirection})");

        env.SunYaw = 180f;
        env.SunPitch = -90f;
        MapEnvironment.Apply(host, env, textures: null);
        Vector3 flipped = -sun.GlobalTransform.Basis.Z.Normalized();
        TestAssert.True(flipped.Dot(Vector3.Up) > 0.99f,
            $"pitch=-90 shines straight up (was {flipped})");

        host.QueueFree();
    }

    [Test]
    public void SunIsDisabledWhenTheCommandHasNoSun()
    {
        Node3D host = NewMapNode(TestScene);
        var env = new SetEnvironmentCmd { SunColor = 0, SunScale = 1f, AmbientColor = 0x101010 };

        MapEnvironment.Apply(host, env, textures: null);

        var sun = host.GetNodeOrNull<DirectionalLight3D>(MapEnvironment.SunNodeName)!;
        TestAssert.False(sun.Visible, "sunlight=0 disables the sun");
        var worldEnvironment = host.GetNodeOrNull<WorldEnvironment>(MapEnvironment.EnvironmentNodeName)!;
        TestAssert.False(worldEnvironment.Environment.FogEnabled, "fog disabled when fog_distance == 0");

        host.QueueFree();
    }

    [Test]
    public void ReapplyUpdatesTheSameNodes()
    {
        Node3D host = NewMapNode(TestScene);
        MapEnvironment.Apply(host, new SetEnvironmentCmd { SunColor = 0x000000 }, textures: null);
        int sunCount = 0;
        int environmentCount = 0;
        foreach (Node child in host.GetChildren())
        {
            if (child is DirectionalLight3D) sunCount++;
            if (child is WorldEnvironment) environmentCount++;
        }
        TestAssert.Equal(1, sunCount, "one sun node");
        TestAssert.Equal(1, environmentCount, "one environment node");

        var updated = new SetEnvironmentCmd { SunColor = 0x808080, SunScale = 3f, AmbientColor = 0x303030 };
        MapEnvironment.Apply(host, updated, textures: null);

        TestAssert.Equal(2, host.GetChildCount(), "no duplicate nodes on re-apply");
        var sun = host.GetNodeOrNull<DirectionalLight3D>(MapEnvironment.SunNodeName)!;
        TestAssert.True(sun.Visible, "re-applied sun enabled");
        TestAssert.Equal(3f, sun.LightEnergy, "re-applied energy");

        host.QueueFree();
    }
}
