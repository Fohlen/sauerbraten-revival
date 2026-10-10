using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Parser + pose evaluator for Cube 2 / Doom 3 <c>.md5anim</c> skeletal-animation
/// files (engine <c>md5.h</c> <c>loadanim</c>, r6889). This is a faithful port of
/// the engine's frame composition, verified against golden output dumped from the
/// original by <c>tools/animprobe</c> (see <c>test/Md5AnimTest.cs</c>).
///
/// <para>
/// The formula per bone, copied from <c>md5.h</c>: start from the animation's
/// <c>baseframe</c> joint, overwrite the components flagged in the frame's float
/// array (with the engine's sign conventions), build a dual quaternion, then
/// compose with the mesh's bind pose:
/// <c>dq = dq * bone.invbase</c>; <c>dst = (parent &lt; 0) ? dq : parent.base * dq</c>.
/// </para>
///
/// <para>
/// The engine's <c>quat::restorew()</c> stores <c>w = -sqrt(1 - x² - y² - z²)</c>
/// (negated, clamped to 0), so identity joints come out as <c>(0,0,0,-1)</c> — this
/// class reproduces that exactly; the sign is irrelevant to the rotation but the
/// golden comparison is sign-sensitive.
/// </para>
/// </summary>
public sealed class Md5Anim
{
    /// <summary>One animated joint's hierarchy entry (<c>md5hierarchy</c>).</summary>
    public readonly record struct Joint(int Parent, int Flags, int Start, int Bone);

    /// <summary>A baseframe joint: position plus the three stored quat components (w via <see cref="RestoreW"/>).</summary>
    private struct RawJoint
    {
        public float Px, Py, Pz, Qx, Qy, Qz;
    }

    private readonly List<Joint> _hierarchy = new();
    private readonly List<RawJoint> _baseframe = new();
    private readonly List<float[]> _frames = new();

    public int NumFrames { get; private set; }
    public int NumJoints { get; private set; }
    public int NumAnimatedComponents { get; private set; }
    public float FrameRate { get; private set; }

    /// <summary>True once a file was parsed successfully.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Number of joints in the parsed hierarchy.</summary>
    public int JointCount => _hierarchy.Count;

    /// <summary>The joint hierarchy (name lookup is done by <see cref="Md5MeshBind"/> index remap upstream).</summary>
    public IReadOnlyList<Joint> Hierarchy => _hierarchy;

    // ---- engine math primitives (verbatim semantics) ----

    /// <summary>geom.h <c>quat::restorew()</c>: <c>w = 1 - x²-y²-z²; w = (w &lt; 0) ? 0 : -sqrt(w)</c>.</summary>
    public static float RestoreW(float x, float y, float z)
    {
        float w = 1.0f - x * x - y * y - z * z;
        return w < 0f ? 0f : -Mathf.Sqrt(w);
    }

    /// <summary>
    /// Parse an <c>.md5anim</c> file. <paramref name="bindNames"/> is the mesh
    /// skeleton's bone names, used for the engine's name-based joint remap.
    /// </summary>
    public static Md5Anim? Parse(string path, IReadOnlyList<string> bindNames)
    {
        if (!System.IO.File.Exists(path)) return null;
        var anim = new Md5Anim();
        return anim.ParseLines(System.IO.File.ReadAllLines(path), bindNames, path) ? anim : null;
    }

    private bool ParseLines(string[] lines, IReadOnlyList<string> bindNames, string path)
    {
        var names = new List<string>();
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i].Trim();
            string[] tok = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);

            if (line.StartsWith("numFrames")) NumFrames = ParseInt(tok);
            else if (line.StartsWith("numJoints")) NumJoints = ParseInt(tok);
            else if (line.StartsWith("numAnimatedComponents")) NumAnimatedComponents = ParseInt(tok);
            else if (line.StartsWith("frameRate")) FrameRate = ParseFloat(tok);
            else if (line.StartsWith("hierarchy"))
            {
                i++;
                while (i < lines.Length && lines[i].Trim() != "}")
                {
                    var (name, parent, flags, start) = ParseHierarchyLine(lines[i]);
                    if (name != null)
                    {
                        names.Add(name);
                        _hierarchy.Add(new Joint(parent, flags, start, _hierarchy.Count));
                    }
                    i++;
                }
                RemapBones(bindNames, names);
            }
            else if (line.StartsWith("baseframe"))
            {
                i++;
                while (i < lines.Length && lines[i].Trim() != "}")
                {
                    if (TryParseJoint(lines[i], out var j))
                    {
                        // verbatim sign handling from md5.h baseframe block
                        j.Py = -j.Py; j.Qx = -j.Qx; j.Qz = -j.Qz;
                        _baseframe.Add(j);
                    }
                    i++;
                }
            }
            else if (line.StartsWith("frame "))
            {
                int frame = (int)ParseFloat(tok);
                i++;
                var data = new List<float>();
                while (i < lines.Length && lines[i].Trim() != "}")
                {
                    foreach (var s in lines[i].Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                            data.Add(v);
                    i++;
                }
                while (_frames.Count <= frame) _frames.Add(System.Array.Empty<float>());
                _frames[frame] = data.ToArray();
            }
            i++;
        }

        if (_hierarchy.Count == 0 || _baseframe.Count != NumJoints || _frames.Count < NumFrames)
            return false;
        Loaded = true;
        return true;
    }

    private void RemapBones(IReadOnlyList<string> bindNames, List<string> names)
    {
        for (int k = 0; k < _hierarchy.Count; k++)
        {
            var h = _hierarchy[k];
            int bone = k;
            if (k < bindNames.Count && bindNames[k] != names[k])
                for (int b = 0; b < bindNames.Count; b++)
                    if (bindNames[b] == names[k]) { bone = b; break; }
            _hierarchy[k] = h with { Bone = bone };
        }
    }

    // ---- frame evaluation ----

    /// <summary>A bone's evaluated local pose: translation + rotation quaternion (x,y,z,w).</summary>
    public readonly record struct BonePose(Vector3 Position, Quaternion Rotation);

    /// <summary>
    /// Evaluate <paramref name="frame"/> into per-bone <b>parent-relative</b> dual
    /// quaternions (indexed by the mesh bind index), reproducing the engine's
    /// <c>frame[]</c> compose. "Parent" here is the engine skeleton's parent; use
    /// <see cref="ToGodotLocal"/> to re-express against a different (e.g. Godot) bone
    /// hierarchy.
    /// </summary>
    private Dq[] EvaluateDq(int frame, Md5MeshBind bind)
    {
        var data = _frames[frame];
        var frameBones = new Dq[bind.BoneCount];
        for (int i = 0; i < _hierarchy.Count; i++)
        {
            Joint h = _hierarchy[i];
            RawJoint j = _baseframe[i];
            if (h.Start < NumAnimatedComponents && h.Flags != 0)
            {
                int d = h.Start;
                if ((h.Flags & 1) != 0) j.Px = data[d++];
                if ((h.Flags & 2) != 0) j.Py = -data[d++];
                if ((h.Flags & 4) != 0) j.Pz = data[d++];
                if ((h.Flags & 8) != 0) j.Qx = -data[d++];
                if ((h.Flags & 16) != 0) j.Qy = data[d++];
                if ((h.Flags & 32) != 0) j.Qz = -data[d++];
            }
            float qw = RestoreW(j.Qx, j.Qy, j.Qz);
            var dq = new Dq(new Quat(j.Qx, j.Qy, j.Qz, qw), new Vector3(j.Px, j.Py, j.Pz));

            int k = h.Bone;
            Md5MeshBind.Bone b = bind.Bones[k];
            dq = dq.Mul(b.InvBase);
            Dq dst = b.Parent < 0 ? dq : bind.Bones[b.Parent].Base.Mul(dq);
            dst = dst.FixAntipodal(bind.Bones[k].Base);
            frameBones[k] = dst;
        }
        return frameBones;
    }

    /// <summary>
    /// Evaluate <paramref name="frame"/> into per-bone parent-relative poses (engine
    /// hierarchy), for diagnostics/golden comparison.
    /// </summary>
    public BonePose[] Evaluate(int frame, Md5MeshBind bind)
    {
        var frameBones = EvaluateDq(frame, bind);
        var poses = new BonePose[bind.BoneCount];
        for (int k = 0; k < bind.BoneCount; k++)
            poses[k] = ToPose(frameBones[k]);
        return poses;
    }

    private static BonePose ToPose(Dq d) =>
        new(d.Translation(), new Quaternion(d.Real.X, d.Real.Y, d.Real.Z, d.Real.W));

    /// <summary>
    /// Convert the engine's parent-relative poses to Godot bone poses. The engine's
    /// <c>frame[k]</c> is a <b>delta from the bind pose</b> (identity = bind), so each
    /// target bone's pose is <c>rest[k] * delta[k]</c>. Godot's glTF importer collapses
    /// bones with no skinning influence (75 engine bones → 50), so the delta is
    /// accumulated to skeleton space and re-expressed against the target hierarchy
    /// first.
    /// </summary>
    /// <param name="enginePose">Per-bone parent-relative deltas (indexed by bind index).</param>
    /// <param name="bind">The mesh bind pose (engine parent links).</param>
    /// <param name="targetParent">For each bind bone, the bind index of its target
    /// hierarchy parent, or -1 for a target root (or unknown).</param>
    /// <param name="rest">Per-bind-bone Godot rest transform (relative to the target
    /// parent); a bound transform for bones the target skeleton lacks.</param>
    public static BonePose[] ToGodotLocal(BonePose[] enginePose, Md5MeshBind bind, int[] targetParent, Transform3D[] rest)
    {
        // Accumulate engine deltas to skeleton space.
        var abs = new Transform3D[bind.BoneCount];
        for (int k = 0; k < bind.BoneCount; k++)
        {
            var local = new Transform3D(new Basis(enginePose[k].Rotation), enginePose[k].Position);
            int p = bind.Bones[k].Parent;
            abs[k] = p < 0 ? local : abs[p] * local;
        }
        // Convert each target bone to its target-parent space, then apply its rest.
        var outp = new BonePose[bind.BoneCount];
        for (int k = 0; k < bind.BoneCount; k++)
        {
            Transform3D delta = abs[k];
            int tp = targetParent[k];
            // acc[targetParent] must also be in skeleton space; abs[tp] is.
            if (tp >= 0) delta = abs[tp].AffineInverse() * delta;
            // Godot pose is relative to the target parent; compose the bone's rest so
            // an identity delta reproduces the bind pose exactly.
            Transform3D t = rest[k] * delta;
            outp[k] = new BonePose(t.Origin, t.Basis.GetRotationQuaternion());
        }
        return outp;
    }

    /// <summary>
    /// Build a Godot <see cref="Animation"/> that drives a <see cref="Skeleton3D"/>'s
    /// bone poses. Engine poses are converted to the target skeleton's own bone
    /// hierarchy via <see cref="ToGodotLocal"/> (handles Godot's collapsed bones).
    ///
    /// Bone tracks use Godot's canonical form <c>"&lt;skeleton path&gt;:&lt;bone name&gt;"</c>
    /// (see <see cref="Animation.TrackSetPath"/>), NOT <c>bones/&lt;idx&gt;/rotation</c>.
    /// </summary>
    /// <param name="bind">The mesh bind pose (bone names + parents).</param>
    /// <param name="skeletonPath">Node path to the target <see cref="Skeleton3D"/>.</param>
    /// <param name="boneName">Resolves a bind bone name to the target skeleton's bone name, or null to skip.</param>
    /// <param name="boneParentName">Resolves a target bone name to its parent bone name, or null for a root.</param>
    /// <param name="boneRest">Resolves a target bone name to its Godot rest transform (parent-relative).</param>
    /// <param name="fps">Playback rate for track length; defaults to the file's frameRate.</param>
    public Animation BuildAnimation(
        Md5MeshBind bind,
        NodePath skeletonPath,
        System.Func<string, string?> boneName,
        System.Func<string, string?> boneParentName,
        System.Func<string, Transform3D?> boneRest,
        float fps = 0f)
    {
        float rate = fps > 0f ? fps : (FrameRate > 0f ? FrameRate : 24f);
        var anim = new Animation { Length = NumFrames > 1 ? (NumFrames - 1) / rate : 0f };

        // Resolve each bind bone to the target skeleton's bone name once.
        var target = new string?[bind.BoneCount];
        for (int k = 0; k < bind.BoneCount; k++)
        {
            var n = boneName(bind.Names[k]);
            target[k] = n;
        }

        // Target-hierarchy parent *expressed as a bind index* so ToGodotLocal can use it.
        var byTargetName = new System.Collections.Generic.Dictionary<string, int>();
        for (int k = 0; k < bind.BoneCount; k++)
            if (target[k] != null) byTargetName[target[k]!] = k;
        var targetParent = new int[bind.BoneCount];
        for (int k = 0; k < bind.BoneCount; k++)
        {
            if (target[k] == null) { targetParent[k] = -1; continue; }
            var tp = boneParentName(target[k]!);
            targetParent[k] = tp != null && byTargetName.TryGetValue(tp, out var bi) ? bi : -1;
        }

        var posTracks = new int[bind.BoneCount];
        var rotTracks = new int[bind.BoneCount];
        var rest = new Transform3D[bind.BoneCount];
        for (int k = 0; k < bind.BoneCount; k++)
        {
            rest[k] = target[k] != null ? (boneRest(target[k]!) ?? Transform3D.Identity) : Transform3D.Identity;
            if (target[k] == null) { posTracks[k] = rotTracks[k] = -1; continue; }
            // Canonical Godot bone track: "<skeleton>:<bone_name>".
            var bonePath = new NodePath($"{skeletonPath}:{target[k]}");
            posTracks[k] = anim.AddTrack(Animation.TrackType.Position3D);
            anim.TrackSetPath(posTracks[k], bonePath);
            rotTracks[k] = anim.AddTrack(Animation.TrackType.Rotation3D);
            anim.TrackSetPath(rotTracks[k], bonePath);
        }

        for (int f = 0; f < NumFrames; f++)
        {
            float t = f / rate;
            var poses = ToGodotLocal(Evaluate(f, bind), bind, targetParent, rest);
            for (int k = 0; k < bind.BoneCount; k++)
            {
                if (target[k] == null) continue;
                anim.TrackInsertKey(posTracks[k], t, poses[k].Position);
                anim.TrackInsertKey(rotTracks[k], t, poses[k].Rotation);
            }
        }
        return anim;
    }

    // ---- little float parsing helpers ----
    private static int ParseInt(string[] tok) => tok.Length > 1 ? (int)ParseFloat(tok) : 0;
    private static float ParseFloat(string[] tok) =>
        tok.Length > 1 && float.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    private static (string? Name, int Parent, int Flags, int Start) ParseHierarchyLine(string line)
    {
        line = StripComment(line);
        int q1 = line.IndexOf('"');
        if (q1 < 0) return (null, 0, 0, 0);
        int q2 = line.IndexOf('"', q1 + 1);
        if (q2 < 0) return (null, 0, 0, 0);
        string name = line.Substring(q1 + 1, q2 - q1 - 1);
        var rest = line.Substring(q2 + 1).Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 3) return (null, 0, 0, 0);
        return (name,
            int.Parse(rest[0], CultureInfo.InvariantCulture),
            int.Parse(rest[1], CultureInfo.InvariantCulture),
            int.Parse(rest[2], CultureInfo.InvariantCulture));
    }

    /// <summary>Strip a trailing Cube2 <c>//</c> comment.</summary>
    private static string StripComment(string line)
    {
        int c = line.IndexOf("//", System.StringComparison.Ordinal);
        return c < 0 ? line : line.Substring(0, c);
    }

    private static bool TryParseJoint(string line, out RawJoint j)
    {
        j = default;
        int a = line.IndexOf('(');
        if (a < 0) return false;
        int a2 = line.IndexOf(')', a);
        int b = line.IndexOf('(', a2);
        int b2 = line.IndexOf(')', b);
        if (a2 < 0 || b < 0 || b2 < 0) return false;
        var p = line.Substring(a + 1, a2 - a - 1).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var q = line.Substring(b + 1, b2 - b - 1).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3 || q.Length < 3) return false;
        j.Px = float.Parse(p[0], CultureInfo.InvariantCulture);
        j.Py = float.Parse(p[1], CultureInfo.InvariantCulture);
        j.Pz = float.Parse(p[2], CultureInfo.InvariantCulture);
        j.Qx = float.Parse(q[0], CultureInfo.InvariantCulture);
        j.Qy = float.Parse(q[1], CultureInfo.InvariantCulture);
        j.Qz = float.Parse(q[2], CultureInfo.InvariantCulture);
        return true;
    }
}
