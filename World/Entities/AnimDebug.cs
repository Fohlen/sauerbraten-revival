using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Temporary animation diagnostic. Prints Godot's bone names and whether the
/// expected bones resolve, plus the AnimationPlayer state. Remove once the
/// animation wiring is verified.
/// </summary>
public partial class AnimDebug : Node
{
    private int _frames;
    private Vector3 _first;
    private bool _have;

    public override void _Process(double delta)
    {
        var body = FindPlayerModel(GetTree().Root);
        if (body == null) return;
        var ap = body.GetNodeOrNull<AnimationPlayer>("AnimationPlayer");
        var skel = body.Skeleton;
        if (ap == null || skel == null) return;

        int b = skel.FindBone("LowerArm.R");
        var pose = b >= 0 ? skel.GetBonePoseRotation(b) : Quaternion.Identity;
        if (!_have && b >= 0) { _first = new Vector3(pose.X, pose.Y, pose.Z); _have = true; }

        if (_frames == 30)
        {
            GD.Print($"[animdbg] bones={skel.GetBoneCount()} LowerArm.R={b}");
            int hip = skel.FindBone("Hips");
            int hand = skel.FindBone("HandGoal.R");
            GD.Print($"[animdbg] hipGlobal={skel.GetBoneGlobalPose(hip).Origin} handGlobal={skel.GetBoneGlobalPose(hand).Origin}");
        }
        if (_frames >= 30 && _frames <= 200 && _frames % 40 == 0)
        {
            int hip = skel.FindBone("Hips");
            int hand = skel.FindBone("HandGoal.R");
            int foot = skel.FindBone("LowerFoot.R");
            float dh = (skel.GetBoneGlobalPose(hand).Origin - skel.GetBoneGlobalPose(hip).Origin).Length();
            float df = (skel.GetBoneGlobalPose(foot).Origin - skel.GetBoneGlobalPose(hip).Origin).Length();
            GD.Print($"[animdbg] f={_frames} handDist={dh:F2} footDist={df:F2}");
        }
        _frames++;
    }

    private static PlayerModel? FindPlayerModel(Node n)
    {
        if (n is PlayerModel p) return p;
        foreach (var c in n.GetChildren()) { var r = FindPlayerModel(c); if (r != null) return r; }
        return null;
    }
}
