using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Regression tests for <see cref="ModelInstance"/> model placement. A Cube 2
/// <c>mdltrans</c> offset is authored in Sauerbraten's Z-up frame, but the holder
/// lives inside the entity node's <see cref="EntityFacing.ApplyYaw"/> basis (which
/// maps the glTF model's Y-up onto the world's Z-up). The offset must therefore be
/// pre-rotated, or a vertical <c>mdltrans</c> becomes a horizontal one that orbits
/// with the entity's yaw (the teleporter bug).
/// </summary>
public class ModelInstanceTranslationTest : GoTestBase
{
    public ModelInstanceTranslationTest(Node testScene) : base(testScene) { }

    [Test]
    public void VerticalTranslationStaysVerticalUnderYaw()
    {
        // Teleporter: mdltrans 0 0 -7 (Sauerbraten Z-up).
        Vector3 holderOffset = ModelInstance.OffsetToModelFrame(new Vector3(0, 0, -7));
        TestAssert.True(holderOffset.IsEqualApprox(new Vector3(0, -7, 0)),
            $"vertical mdltrans maps into the model frame ({holderOffset})");

        foreach (float yaw in new[] { 0f, 37f, 90f, 180f, 270f })
        {
            Basis basis = Basis.LookingAt(EntityFacing.Forward(yaw), new Vector3(0, 0, 1));
            Vector3 worldOffset = basis * holderOffset;
            TestAssert.True(Mathf.Abs(worldOffset.X) < 1e-4f && Mathf.Abs(worldOffset.Y) < 1e-4f,
                $"yaw {yaw} has no horizontal offset ({worldOffset})");
            TestAssert.True(Mathf.Abs(worldOffset.Z + 7f) < 1e-4f,
                $"yaw {yaw} keeps the offset vertical ({worldOffset})");
        }
    }

    [Test]
    public void TeleporterModelSpinsInPlace()
    {
        const string resDir = "res://assets/models";
        string dir = ProjectSettings.GlobalizePath(resDir);
        string manifestPath = Path.Combine(dir, "models.json");
        if (!File.Exists(manifestPath))
        {
            GD.Print("[modelinstance] no converted models; skipping");
            return;
        }

        var catalog = ModelCatalog.Load(manifestPath, dir, resDir);
        var info = catalog?.Get("teleporter");
        if (info?.GlbPath == null && info?.GlbOsPath == null)
        {
            GD.Print("[modelinstance] no teleporter glb; skipping");
            return;
        }

        var world = new Node3D { RotationDegrees = new Vector3(-90, 0, 0) }; // Z-up -> Y-up
        var root = new Node3D();
        world.AddChild(root);
        TestScene.AddChild(world);

        var teleport = new Teleport
        {
            Name = "Teleport_probe",
            Position = new Vector3(3000, 1000, 3000),
            BasePosition = new Vector3(3000, 1000, 3000),
            ModelIndex = 0,
            ModelPath = "teleporter",
            GlbPath = info!.GlbPath,
            GlbOsPath = info.GlbOsPath,
            ModelScale = info.Scale,
            ModelTranslation = info.Translation,
        };
        root.AddChild(teleport);
        if (!teleport.ModelLoaded)
        {
            GD.Print("[modelinstance] teleporter model failed to load; skipping");
            world.Free();
            return;
        }

        teleport.UpdateVisual(0);
        Vector3 c0 = WorldCenter(teleport);
        teleport.UpdateVisual(500);
        Vector3 c1 = WorldCenter(teleport);
        teleport.UpdateVisual(1234);
        Vector3 c2 = WorldCenter(teleport);

        // The world is rotated -90 about X, so Sauerbraten's horizontal plane is the
        // world XZ plane; only the bob changes Y. A roughly constant XZ across yaws
        // means the model spins in place rather than orbiting (a regression moved the
        // centre by the ~7-unit mdltrans radius).
        TestAssert.True(Mathf.Abs(c0.X - c1.X) < 0.05f && Mathf.Abs(c0.Z - c1.Z) < 0.05f,
            $"t=0 vs t=500 horizontal center ({c0} vs {c1})");
        TestAssert.True(Mathf.Abs(c0.X - c2.X) < 0.05f && Mathf.Abs(c0.Z - c2.Z) < 0.05f,
            $"t=0 vs t=1234 horizontal center ({c0} vs {c2})");

        world.Free();
    }

    private static Vector3 WorldCenter(Node node)
    {
        Aabb? box = WorldAabb(node);
        return box?.GetCenter() ?? Vector3.Zero;
    }

    private static Aabb? WorldAabb(Node node)
    {
        Aabb? box = null;
        if (node is MeshInstance3D { Mesh: not null } mesh)
            box = mesh.GlobalTransform * mesh.Mesh.GetAabb();
        foreach (var child in node.GetChildren())
        {
            Aabb? childBox = WorldAabb(child);
            if (childBox == null) continue;
            box = box == null ? childBox : box.Value.Merge(childBox.Value);
        }
        return box;
    }
}
