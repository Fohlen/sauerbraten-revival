using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Asserts the C# MD5 animation port (<see cref="Md5Anim"/> + <see cref="Md5MeshBind"/>)
/// reproduces the ORIGINAL engine's frame composition, via golden dumps from
/// <c>tools/animprobe</c> (<c>test/golden/anim/*.csv</c>). The probe copies the
/// frame-compose math verbatim from the engine's <c>md5.h</c>, so this diffs our
/// port against the engine's exact dual-quaternion output.
///
/// Skips if the read-only reference dump isn't present (the golden CSV is committed,
/// but re-evaluating it needs the source <c>.md5mesh</c>/<c>.md5anim</c>, same as the
/// other tool-dependent suites).
/// </summary>
public class Md5AnimTest : TestClass
{
    public Md5AnimTest(Node testScene) : base(testScene) { }

    private const float Eps = 1e-4f;

    [Test] public void MrfixitIdleFrame0MatchesEngine() => Compare("mrfixit", "idle", 0);
    [Test] public void MrfixitIdleFrame50MatchesEngine() => Compare("mrfixit", "idle", 50);
    [Test] public void MrfixitIdleFrame100MatchesEngine() => Compare("mrfixit", "idle", 100);
    [Test] public void BuildsAnimationWithBoneTracks() => BuildAnimation();

    private static void BuildAnimation()
    {
        string refRoot = System.Environment.GetEnvironmentVariable("SAUER_REF")
            ?? "/Users/fohlen/Downloads/sauerbraten-code-r6889";
        string meshPath = Path.Combine(refRoot, "packages", "models", "mrfixit", "mrfixit.md5mesh");
        string animPath = Path.Combine(refRoot, "packages", "models", "mrfixit", "idle.md5anim");
        if (!File.Exists(meshPath) || !File.Exists(animPath))
        {
            GD.Print("[md5anim] reference dump not present; skipping");
            return;
        }

        var bind = Md5MeshBind.Parse(meshPath)!;
        var a = Md5Anim.Parse(animPath, bind.Names)!;
        // Name-based resolvers (Godot bone tracks use "<skeleton>:<bone name>").
        var present = new HashSet<string>(bind.Names);
        System.Func<string, string?> boneName = n => present.Contains(n) ? n : null;
        System.Func<string, string?> boneParentName = n =>
        {
            int i = bind.Names.IndexOf(n);
            if (i < 0 || bind.Bones[i].Parent < 0) return null;
            return bind.Names[bind.Bones[i].Parent];
        };

        var anim = a.BuildAnimation(bind, "Skeleton3D", boneName, boneParentName, _ => Transform3D.Identity);
        TestAssert.Equal(bind.BoneCount * 2, anim.GetTrackCount(), "one position+rotation track per bone");
        TestAssert.True(anim.Length > 0f, "non-zero length");
        var path = anim.TrackGetPath(0).ToString();
        GD.Print($"[md5anim] animation length={anim.Length:F3}s track0={path} tracks={anim.GetTrackCount()}");
        TestAssert.True(path.StartsWith("Skeleton3D:"), "track targets the skeleton node");
        // Canonical bone path: "<skeleton>:<bone_name>" (no /position or /rotation suffix).
        TestAssert.True(!path.EndsWith("/position") && !path.EndsWith("/rotation"), "bone track uses the bone-name form");
        TestAssert.True(path.Contains(':'), "track has a bone name after the colon");
    }

    private static void Compare(string model, string anim, int frame)
    {
        string refRoot = System.Environment.GetEnvironmentVariable("SAUER_REF")
            ?? "/Users/fohlen/Downloads/sauerbraten-code-r6889";
        string meshPath = Path.Combine(refRoot, "packages", "models", model, model + ".md5mesh");
        string animPath = Path.Combine(refRoot, "packages", "models", model, anim + ".md5anim");
        string goldenPath = Path.Combine(ProjectSettings.GlobalizePath("res://"),
            "test/golden/anim", $"{model}_{anim}_f{frame}.csv");

        if (!File.Exists(meshPath) || !File.Exists(animPath))
        {
            GD.Print($"[md5anim] reference dump not present ({meshPath}); skipping");
            return;
        }
        TestAssert.True(File.Exists(goldenPath), $"golden fixture exists at {goldenPath}");

        var bind = Md5MeshBind.Parse(meshPath);
        TestAssert.True(bind != null, "mesh bind pose parsed");
        var a = Md5Anim.Parse(animPath, bind!.Names);
        TestAssert.True(a != null, "anim parsed");

        var expected = ReadGolden(goldenPath);
        var poses = a!.Evaluate(frame, bind);
        TestAssert.Equal(expected.Count, poses.Length, "bone count matches golden");

        float maxPos = 0f, maxRot = 0f;
        foreach (var (bone, idx, name, pos, rot) in expected)
        {
            var p = poses[idx];
            float dp = (p.Position - pos).Length();
            // Rotation closeness up to sign (q and -q are the same rotation):
            // |dot| = 1 means identical, so the "delta" is 1 - |dot|.
            float dr = 1f - qdot(p.Rotation, rot);
            maxPos = Mathf.Max(maxPos, dp);
            maxRot = Mathf.Max(maxRot, dr);
            TestAssert.True(dp < Eps, $"{name} pos delta {dp} (got {p.Position}, want {pos})");
            TestAssert.True(dr < Eps, $"{name} rot delta {dr}");
        }
        GD.Print($"[md5anim] {model}/{anim} f{frame}: {poses.Length} bones, maxPos={maxPos:G3} maxRot={maxRot:G3}");
    }

    private static float qdot(Quaternion a, Quaternion b) =>
        Mathf.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);

    private static List<(int Bone, int Idx, string Name, Vector3 Pos, Quaternion Rot)> ReadGolden(string path)
    {
        var rows = new List<(int, int, string, Vector3, Quaternion)>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (!line.StartsWith("BONE ")) continue;
            var t = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            int bone = int.Parse(t[1], CultureInfo.InvariantCulture);
            int idx = int.Parse(t[2], CultureInfo.InvariantCulture);
            string name = t[3];
            float f(int i) => float.Parse(t[i], CultureInfo.InvariantCulture);
            var pos = new Vector3(f(4), f(5), f(6));
            var rot = new Quaternion(f(7), f(8), f(9), f(10));
            rows.Add((bone, idx, name, pos, rot));
        }
        return rows;
    }
}
