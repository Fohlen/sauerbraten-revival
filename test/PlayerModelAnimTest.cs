using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies <see cref="PlayerModel"/> wires the parsed MD5 clips into an
/// <see cref="AnimationPlayer"/> that drives the imported <see cref="Skeleton3D"/>.
/// Skips if the converted models or the read-only reference dump aren't present
/// (both are git-ignored), like the other tool-dependent suites.
/// </summary>
public class PlayerModelAnimTest : GoTestBase
{
    public PlayerModelAnimTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsClipsAndAnimatesSkeleton()
    {
        string refRoot = System.Environment.GetEnvironmentVariable("SAUER_REF")
            ?? "/Users/fohlen/Downloads/sauerbraten-code-r6889";
        string meshOs = Path.Combine(refRoot, "packages/models/mrfixit/mrfixit.md5mesh");

        string dir = ProjectSettings.GlobalizePath("res://assets/models");
        var catalog = ModelCatalog.Load(Path.Combine(dir, "models.json"), dir, "res://assets/models");
        if (!File.Exists(meshOs) || catalog == null)
        {
            GD.Print("[player-anim] reference dump or converted models missing; skipping");
            return;
        }
        var info = catalog.Get("mrfixit/blue");
        TestAssert.True(info != null, "mrfixit/blue in catalogue");

        var body = new PlayerModel { Name = "PlayerModel" };
        var holder = new Node3D();
        holder.AddChild(body);
        TestScene.AddChild(holder);
        body.SetAnimSources(meshOs, info!.Anims, refRoot);
        body.Configure(catalog, null, 0, 1);

        TestAssert.True(body.ModelLoaded, "model loaded");
        TestAssert.True(body.Skeleton != null, "skeleton kept");
        TestAssert.True(body.Animated, "animated (clips present)");
        TestAssert.Equal("idle", body.CurrentAnim, "idle started by default");

        var player = body.GetNodeOrNull<AnimationPlayer>("AnimationPlayer")!;
        var idle = player.GetAnimation("idle");
        TestAssert.True(idle.GetTrackCount() > 0, "idle has tracks");

        // Every track path must resolve against the live tree, or nothing would move.
        for (int i = 0; i < idle.GetTrackCount(); i++)
            TestAssert.True(player.HasAnimation("idle"), "clip registered");
        // Canonical Godot bone path "<skeleton>:<bone_name>".
        TestAssert.True(idle.TrackGetPath(0).ToString().Contains(":"), "bone track path has a bone name");
        TestAssert.True(!idle.TrackGetPath(0).ToString().EndsWith("/position")
                    && !idle.TrackGetPath(0).ToString().EndsWith("/rotation"), "bone track path is not the raw property form");

        // The clip must not be a single static pose: scan for real variation.
        float maxRot = 0f, maxPos = 0f;
        for (int i = 0; i < idle.GetTrackCount(); i++)
        {
            int keys = idle.TrackGetKeyCount(i);
            if (idle.TrackGetType(i) == Animation.TrackType.Rotation3D)
            {
                var a = (Quaternion)idle.TrackGetKeyValue(i, 0);
                for (int k = 0; k < keys; k += 10)
                    maxRot = Mathf.Max(maxRot, 1f - Mathf.Abs(a.Dot((Quaternion)idle.TrackGetKeyValue(i, k))));
            }
            else if (idle.TrackGetType(i) == Animation.TrackType.Position3D)
            {
                var a = (Vector3)idle.TrackGetKeyValue(i, 0);
                for (int k = 0; k < keys; k += 10)
                    maxPos = Mathf.Max(maxPos, ((Vector3)idle.TrackGetKeyValue(i, k) - a).Length());
            }
        }
        GD.Print($"[player-anim] clips={player.GetAnimationList().Length} tracks={idle.GetTrackCount()} " +
                 $"maxRot={maxRot:F4} maxPos={maxPos:F4}");
        TestAssert.True(maxRot > 1e-3f || maxPos > 1e-3f, "idle poses vary over time");

        holder.QueueFree();
    }
}
