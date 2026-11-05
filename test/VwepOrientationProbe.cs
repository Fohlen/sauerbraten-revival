using System.IO;
using System.Linq;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Models;
using PlayerModel = SauerbratenRevival.World.Models.PlayerModel;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Probe: where does Godot actually put third-person vwep vertices, relative to the
/// animated <c>Weapon</c> joint frame the engine uses (skelmodel.h calctags: the vwep's
/// bind-pose MD5 vertices are drawn directly in the tag frame, no per-weapon fixup)?
/// Prints the discrepancy rotation D = WeaponFrame⁻¹ · VwepVertexFrame (ideally identity
/// up to scale) and the barrel direction in the body's MD5 frame per hold animation.
/// </summary>
public class VwepOrientationProbe : GoTestBase
{
    public VwepOrientationProbe(Node testScene) : base(testScene) { }

    private static void Log(string s) => GD.PrintErr(s);

    private static string V(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";

    private static Basis Ortho(Basis b) => b.Orthonormalized();

    [Test]
    public void ProbeVwepFrames()
    {
        string dir = ProjectSettings.GlobalizePath("res://assets/models");
        var catalog = ModelCatalog.Load(Path.Combine(dir, "models.json"), dir, "res://assets/models");
        if (catalog == null) { Log("[probe] no catalog"); return; }

        var holder = new Node3D();
        TestScene.AddChild(holder);
        var body = new PlayerModel { Name = "ProbeBody" };
        holder.AddChild(body);
        body.Configure(catalog, null, 0, 1, viewWeapon: "vwep/shotg");
        if (!body.ModelLoaded || body.Skeleton == null || body.AnimPlayer == null) { Log("[probe] body not loaded"); return; }

        var skel = body.Skeleton;
        int hand = skel.FindBone("HandGoal.R");
        var weaponNode = body.FindChild("Weapon", recursive: true, owned: false) as Node3D;
        Log($"[probe] HandGoal.R bone={hand} weaponNode={weaponNode?.GetPath()} parentType={weaponNode?.GetParent()?.GetType().Name}");

        // (vwep, hold anim, barrel axis in raw MD5 mesh space)
        var cases = new (string vwep, string anim, Vector3 barrel)[]
        {
            ("vwep/shotg", "hold 2", new Vector3(1, 0, 0)),
            ("vwep/rocket", "hold 4", new Vector3(0, -1, 0)),
            ("vwep/rifle", "hold 5", new Vector3(1, 0, 0)),
            ("vwep/chaing", "hold 3", new Vector3(1, 0, 0)),
        };

        foreach (var (vwep, anim, barrel) in cases)
        {
            body.SetViewWeapon(vwep);
            body.Play(anim, loop: true, restart: true);
            body.AnimPlayer.Seek(0.0, update: true);
            skel.ForceUpdateAllBoneTransforms();

            var skelG = skel.GlobalTransform;
            // Engine tag frame: animated HandGoal.R pose * Weapon's constant local offset.
            var weaponFrame = skelG * skel.GetBoneGlobalPose(hand) * weaponNode!.Transform;
            var anchorG = weaponNode.GlobalTransform; // what the BoneAttachment chain reports

            var w = body.ViewWeapon!;
            var mi = w.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().First();
            var vSkel = mi.GetNodeOrNull<Skeleton3D>(mi.Skeleton);
            Transform3D vertexFrame;
            string how;
            if (mi.Skin != null && vSkel != null && mi.Skin.GetBindCount() > 0)
            {
                int b = mi.Skin.GetBindBone(0);
                if (b < 0) b = vSkel.FindBone(mi.Skin.GetBindName(0));
                // vSkel is static (no clips) so its global transform is valid; re-root it on
                // the analytic weapon frame so a stale BoneAttachment can't hide anything.
                var anchorToVSkel = anchorG.AffineInverse() * vSkel.GlobalTransform;
                vertexFrame = weaponFrame * anchorToVSkel * vSkel.GetBoneGlobalPose(b) * mi.Skin.GetBindPose(0);
                how = $"skinned bind='{mi.Skin.GetBindName(0)}'";
            }
            else
            {
                vertexFrame = weaponFrame * (anchorG.AffineInverse() * mi.GlobalTransform);
                how = "unskinned";
            }

            // Express everything in the body skeleton (= MD5 model) frame.
            var wfL = Ortho((skelG.AffineInverse() * weaponFrame).Basis);
            var vfL = Ortho((skelG.AffineInverse() * vertexFrame).Basis);
            var D = wfL.Inverse() * vfL;

            Log($"[probe] {vwep} {anim} ({how}) rotHack={w.ModelRotationDegrees}");
            Log($"[probe]   anchor stale? |anchorG - weaponFrame| origin diff={(anchorG.Origin - weaponFrame.Origin).Length():F4}");
            Log($"[probe]   Weapon frame axes (md5): X={V(wfL.Column0)} Y={V(wfL.Column1)} Z={V(wfL.Column2)}");
            Log($"[probe]   expected barrel (engine) = {V(wfL * barrel)}");
            Log($"[probe]   godot    barrel          = {V(vfL * barrel)}");
            Log($"[probe]   D euler deg = {V(D.GetEuler() * (180f / Mathf.Pi))}  D cols X={V(D.Column0)} Y={V(D.Column1)} Z={V(D.Column2)}");

            TestAssert.True(D.IsEqualApprox(Basis.Identity), $"{vwep} vertex frame matches engine Weapon frame (D is identity)");
        }

        holder.QueueFree();
    }
}
