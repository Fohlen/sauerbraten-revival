using System.IO;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Models;
using PlayerModel = SauerbratenRevival.World.Models.PlayerModel;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies <see cref="PlayerModel"/> uses the <b>baked glb's own</b>
/// <see cref="AnimationPlayer"/> (clips exported by <c>tools/model-import</c>), not a
/// hand-mapped engine pose. Skips if the converted models aren't present (git-ignored).
/// See (docs/adr/0004-character-animation.md) for why the manual mapping was abandoned.
/// </summary>
public class PlayerModelAnimTest : GoTestBase
{
    public PlayerModelAnimTest(Node testScene) : base(testScene) { }

    [Test]
    public void UsesBakedAnimationPlayer()
    {
        string dir = ProjectSettings.GlobalizePath("res://assets/models");
        var catalog = ModelCatalog.Load(Path.Combine(dir, "models.json"), dir, "res://assets/models");
        if (catalog == null) { GD.Print("[player-anim] no converted models; skipping"); return; }
        var info = catalog.Get("mrfixit/blue");
        if (info == null) { GD.Print("[player-anim] mrfixit/blue missing; skipping"); return; }

        var body = new PlayerModel { Name = "PlayerModel" };
        var holder = new Node3D();
        holder.AddChild(body);
        TestScene.AddChild(holder);
        body.Configure(catalog, null, 0, 1);

        if (!body.Animated)
        {
            GD.Print("[player-anim] glb has no baked clips (re-run tools/model-import); skipping");
            return;
        }

        var player = Find<AnimationPlayer>(body)!;
        var clips = player.GetAnimationList();
        GD.Print($"[player-anim] clips={clips.Length} [{string.Join(",", clips.Take(6))}…] current={body.CurrentAnim}");
        TestAssert.True(clips.Contains("idle"), "idle clip present");
        TestAssert.True(clips.Contains("forward"), "forward clip present");
        TestAssert.Equal("idle", body.CurrentAnim, "idle started by default");

        // A rotation track must actually vary (idle moves the arms).
        var idle = player.GetAnimation("idle");
        float maxRot = 0f;
        for (int i = 0; i < idle.GetTrackCount(); i++)
        {
            if (idle.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            int keys = idle.TrackGetKeyCount(i);
            var a = (Quaternion)idle.TrackGetKeyValue(i, 0);
            for (int k = 0; k < keys; k += 10)
                maxRot = Mathf.Max(maxRot, 1f - Mathf.Abs(a.Dot((Quaternion)idle.TrackGetKeyValue(i, k))));
        }
        TestAssert.True(maxRot > 1e-3f, "idle rotation tracks vary over time");
        holder.QueueFree();
    }

    /// <summary>
    /// The baked clips span the <c>.md5anim</c>'s authored 24 fps times, so the
    /// engine's anims.cfg speed (animmodel.h <c>info.speed = 1000/fps</c>) must be
    /// applied as <c>engineFps / 24</c>: forward/left/right 125, backward 160,
    /// swim 20, idle 10.
    /// </summary>
    [Test]
    public void MovementClipsUseEngineFps()
    {
        string dir = ProjectSettings.GlobalizePath("res://assets/models");
        var catalog = ModelCatalog.Load(Path.Combine(dir, "models.json"), dir, "res://assets/models");
        var info = catalog?.Get("mrfixit/blue");
        if (info == null) { GD.Print("[player-anim] mrfixit/blue missing; skipping"); return; }

        var body = new PlayerModel { Name = "PlayerModel" };
        var holder = new Node3D();
        holder.AddChild(body);
        TestScene.AddChild(holder);
        body.Configure(catalog, null, 0, 1);
        if (!body.Animated) { GD.Print("[player-anim] glb has no baked clips; skipping"); return; }

        var player = Find<AnimationPlayer>(body)!;
        const float authoredFps = 24f;
        var cases = new[]
        {
            ("forward", 125f), ("left", 125f), ("right", 125f),
            ("backward", 160f), ("swim", 20f), ("idle", 10f),
        };
        foreach (var (clip, engineFps) in cases)
        {
            if (!player.HasAnimation(clip)) continue;
            body.Play(clip);
            float expected = engineFps / authoredFps;
            TestAssert.True(Mathf.Abs(player.SpeedScale - expected) < 1e-3f,
                $"{clip}: SpeedScale {player.SpeedScale} != engine {engineFps}/{authoredFps} = {expected}");
        }
        holder.QueueFree();
    }

    private static T? Find<T>(Node n) where T : Node
    {
        if (n is T t) return t;
        foreach (var c in n.GetChildren()) { var r = Find<T>(c); if (r != null) return r; }
        return null;
    }
}
