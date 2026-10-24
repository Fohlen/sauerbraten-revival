using System.Collections.Generic;
using System.Globalization;
using Godot;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.World.Models;

/// <summary>
/// The mesh skeleton's bind pose (<c>md5mesh</c> <c>joints {}</c> block), ported
/// from <c>md5.h</c> <c>loadmesh</c>. Holds each bone's parent and its dual-quat
/// bind transform (<c>base</c>/<c>invbase</c>), which <see cref="Md5Anim"/> composes
/// against to produce local bone poses.
/// </summary>
public sealed class Md5MeshBind
{
    /// <summary>One bone: parent index (-1 = root) and its bind dual quaternion.</summary>
    public readonly record struct Bone(int Parent, Dq Base, Dq InvBase);

    public List<Bone> Bones { get; } = new();
    public List<string> Names { get; } = new();

    public int BoneCount => Bones.Count;

    /// <summary>Parse the <c>joints {}</c> block of a <c>.md5mesh</c> (bind pose).</summary>
    public static Md5MeshBind? Parse(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        var lines = System.IO.File.ReadAllLines(path);
        var bind = new Md5MeshBind();
        var rawJoints = new List<(int Parent, float Px, float Py, float Pz, float Qx, float Qy, float Qz)>();
        bool inJoints = false;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (!inJoints)
            {
                if (line.StartsWith("joints")) inJoints = true;
                continue;
            }
            if (line.StartsWith("}")) break;
            var parsed = ParseJointLine(line);
            if (parsed is { } p)
            {
                // verbatim sign handling from md5.h joints block
                rawJoints.Add((p.Parent, p.Px, -p.Py, p.Pz, -p.Qx, p.Qy, -p.Qz));
                bind.Names.Add(p.Name);
            }
        }

        foreach (var rj in rawJoints)
        {
            float w = Md5Anim.RestoreW(rj.Qx, rj.Qy, rj.Qz);
            var dq = new Dq(new Quat(rj.Qx, rj.Qy, rj.Qz, w), new Vector3(rj.Px, rj.Py, rj.Pz));
            bind.Bones.Add(new Bone(rj.Parent, dq, dq.Inverse()));
        }
        return bind.Bones.Count > 0 ? bind : null;
    }

    private static (string Name, int Parent, float Px, float Py, float Pz, float Qx, float Qy, float Qz)? ParseJointLine(string line)
    {
        int q1 = line.IndexOf('"');
        if (q1 < 0) return null;
        int q2 = line.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        string name = line.Substring(q1 + 1, q2 - q1 - 1);

        string rest = line.Substring(q2 + 1);
        int a = rest.IndexOf('('), a2 = rest.IndexOf(')', a < 0 ? 0 : a);
        if (a < 0 || a2 < 0) return null;
        var head = rest.Substring(0, a).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        int b = rest.IndexOf('(', a2), b2 = rest.IndexOf(')', b < 0 ? 0 : b);
        if (b < 0 || b2 < 0 || head.Length < 1) return null;
        var p = rest.Substring(a + 1, a2 - a - 1).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var q = rest.Substring(b + 1, b2 - b - 1).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3 || q.Length < 3) return null;
        return (name,
            int.Parse(head[0], CultureInfo.InvariantCulture),
            F(p[0]), F(p[1]), F(p[2]), F(q[0]), F(q[1]), F(q[2]));
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
}
