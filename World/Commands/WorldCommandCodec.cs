using System;
using Godot;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// Canonical binary encoding for commands — the bytes shared by save, network and
/// demo (docs/editor-and-commands.md §5.1).
///
/// Uses Godot's <c>var_to_bytes</c> over **positional arrays** (never dictionaries,
/// which are not canonically ordered). Two logically-identical commands encode to
/// identical bytes, which is what parity hashing and byte-stable demos need.
/// A leading format-version byte lets us migrate if the layout ever changes.
///
/// Untrusted input (network) must use <see cref="Decode"/> which goes through the
/// object-free <c>bytes_to_var</c>, so a peer cannot smuggle an <c>Object</c>.
///
/// Design note: FlatBuffers was considered for zero-parse reads and cross-language
/// tooling, but it is not canonical by default (breaks parity hashing) and adds a
/// <c>flatc</c> toolchain; at our command sizes parse cost is not a bottleneck (the
/// real cost is meshing). If a shared C#/C++/Python format is ever needed, it swaps
/// in behind this one class.
/// </summary>
public static class WorldCommandCodec
{
    /// <summary>Bump when the payload layout changes incompatibly.</summary>
    public const byte FormatVersion = 1;

    // ---- command framing ----

    /// <summary>Encode a command as [u8 version][u64 sequence][i32 author][u8 source][Variant array].</summary>
    public static byte[] Encode(WorldCommand cmd)
    {
        var payload = new Godot.Collections.Array { (int)cmd.Op };
        WritePayload(payload, cmd);

        var body = GD.VarToBytes(payload);
        var outBytes = new byte[1 + 8 + 4 + 1 + body.Length];
        outBytes[0] = FormatVersion;
        BitConverter.TryWriteBytes(outBytes.AsSpan(1, 8), cmd.Sequence);
        BitConverter.TryWriteBytes(outBytes.AsSpan(9, 4), cmd.AuthorPeerId);
        outBytes[13] = (byte)cmd.Source;
        body.CopyTo(outBytes, 14);
        return outBytes;
    }

    /// <summary>Decode bytes produced by <see cref="Encode"/>. Object-free and safe for untrusted input.</summary>
    public static WorldCommand Decode(byte[] data)
    {
        byte version = data[0];
        if (version != FormatVersion)
            throw new InvalidOperationException($"Unsupported command format version {version} (expected {FormatVersion})");
        ulong sequence = BitConverter.ToUInt64(data, 1);
        int author = BitConverter.ToInt32(data, 9);
        var source = (CommandSource)data[13];
        var body = new byte[data.Length - 14];
        Array.Copy(data, 14, body, 0, body.Length);

        var decoded = GD.BytesToVar(body);
        if (decoded.VariantType != Variant.Type.Array)
            throw new InvalidOperationException("Malformed command payload");
        var arr = decoded.As<Godot.Collections.Array>();
        if (arr.Count < 1)
            throw new InvalidOperationException("Malformed command payload");

        var op = (WorldOp)(int)arr[0];
        return new WorldCommand(op, ReadPayload(op, arr), author, source) { Sequence = sequence };
    }

    // ---- payloads (positional arrays) ----

    private static void WriteSelection(Godot.Collections.Array a, SelectionPayload s)
    {
        a.Add(s.Origin.X); a.Add(s.Origin.Y); a.Add(s.Origin.Z);
        a.Add(s.Size.X); a.Add(s.Size.Y); a.Add(s.Size.Z);
        a.Add(s.Grid);
        a.Add(s.Orient);
    }

    private static SelectionPayload ReadSelection(Godot.Collections.Array a, int i) => new(
        new Vector3I((int)a[i], (int)a[i + 1], (int)a[i + 2]),
        new Vector3I((int)a[i + 3], (int)a[i + 4], (int)a[i + 5]),
        (int)a[i + 6], (int)a[i + 7]);

    private static void WritePayload(Godot.Collections.Array a, WorldCommand cmd)
    {
        switch (cmd.Op)
        {
            case WorldOp.DeleteCube:
                WriteSelection(a, ((DeleteCubePayload)cmd.Payload).Selection);
                break;
            case WorldOp.EditFace:
            {
                var p = (EditFacePayload)cmd.Payload;
                WriteSelection(a, p.Selection);
                a.Add(p.Dir); a.Add(p.Mode);
                break;
            }
            case WorldOp.ReplaceBlock:
            {
                var p = (ReplaceBlockPayload)cmd.Payload;
                WriteSelection(a, p.Target);
                if (p.Block == null) a.Add(false);
                else { a.Add(true); WriteBlock(a, p.Block); }
                break;
            }
            case WorldOp.SetFaceTexture:
            {
                var p = (SetFaceTexturePayload)cmd.Payload;
                WriteSelection(a, p.Selection);
                a.Add(p.Texture); a.Add(p.Orient);
                break;
            }
            case WorldOp.ReplaceTexture:
            {
                var p = (ReplaceTexturePayload)cmd.Payload;
                a.Add(p.InSelection);
                if (p.Selection.HasValue) { a.Add(true); WriteSelection(a, p.Selection.Value); } else a.Add(false);
                a.Add(p.OldTexture); a.Add(p.NewTexture);
                break;
            }
            case WorldOp.SetFaceMaterial:
            {
                var p = (SetFaceMaterialPayload)cmd.Payload;
                WriteSelection(a, p.Selection);
                a.Add(p.MaterialId); a.Add(p.Filter);
                break;
            }
            case WorldOp.FlipCube:
            case WorldOp.RotateCube:
            {
                var p = (TransformCubePayload)cmd.Payload;
                WriteSelection(a, p.Selection);
                a.Add(p.Direction);
                break;
            }
            case WorldOp.Remip:
                a.Add(((RemipPayload)cmd.Payload).MipVis);
                break;
            case WorldOp.Snapshot:
                break;
            case WorldOp.NewMap:
                a.Add(((NewMapPayload)cmd.Payload).Scale);
                break;
            default:
                throw new NotSupportedException($"Cannot encode payload for {cmd.Op}");
        }
    }

    private static ICommandPayload ReadPayload(WorldOp op, Godot.Collections.Array a)
    {
        // Index 0 is the op; payload begins at 1.
        const int b = 1;
        switch (op)
        {
            case WorldOp.DeleteCube:
                return new DeleteCubePayload(ReadSelection(a, b));
            case WorldOp.EditFace:
            {
                var sel = ReadSelection(a, b);
                return new EditFacePayload(sel, (int)a[b + 8], (int)a[b + 9]);
            }
            case WorldOp.ReplaceBlock:
            {
                var target = ReadSelection(a, b);
                if (!(bool)a[b + 8]) return new ReplaceBlockPayload(target, null);
                return new ReplaceBlockPayload(target, ReadBlock(a, b + 9));
            }
            case WorldOp.SetFaceTexture:
                return new SetFaceTexturePayload(ReadSelection(a, b), (int)a[b + 8], (int)a[b + 9]);
            case WorldOp.ReplaceTexture:
            {
                bool inSel = (bool)a[b];
                SelectionPayload? sel = (bool)a[b + 1] ? ReadSelection(a, b + 2) : null;
                int idx = inSel && sel.HasValue ? b + 2 + 8 : b + 2;
                return new ReplaceTexturePayload(sel, inSel, (int)a[idx], (int)a[idx + 1]);
            }
            case WorldOp.SetFaceMaterial:
                return new SetFaceMaterialPayload(ReadSelection(a, b), (int)a[b + 8], (int)a[b + 9]);
            case WorldOp.FlipCube:
            case WorldOp.RotateCube:
                return new TransformCubePayload(ReadSelection(a, b), (int)a[b + 8]);
            case WorldOp.Remip:
                return new RemipPayload((bool)a[b]);
            case WorldOp.Snapshot:
                return EmptyPayload.Instance;
            case WorldOp.NewMap:
                return new NewMapPayload((int)a[b]);
            default:
                throw new NotSupportedException($"Cannot decode payload for {op}");
        }
    }

    // ---- block / cube blobs ----

    private static void WriteBlock(Godot.Collections.Array a, OctaEdit.CapturedBlock block)
    {
        var d = block.Descriptor;
        a.Add(d.O.X); a.Add(d.O.Y); a.Add(d.O.Z);
        a.Add(d.S.X); a.Add(d.S.Y); a.Add(d.S.Z);
        a.Add(d.Grid); a.Add(d.Orient);

        var cubes = new Godot.Collections.Array();
        foreach (var c in block.Cubes) cubes.Add(WriteCube(c));
        a.Add(cubes);
    }

    private static OctaEdit.CapturedBlock ReadBlock(Godot.Collections.Array a, int i)
    {
        var o = new Vector3I((int)a[i], (int)a[i + 1], (int)a[i + 2]);
        var s = new Vector3I((int)a[i + 3], (int)a[i + 4], (int)a[i + 5]);
        int grid = (int)a[i + 6], orient = (int)a[i + 7];
        var cubesArr = a[i + 8].As<Godot.Collections.Array>();
        var cubes = new Cube[cubesArr.Count];
        for (int k = 0; k < cubesArr.Count; k++) cubes[k] = ReadCube(cubesArr[k].As<Godot.Collections.Array>());
        return new OctaEdit.CapturedBlock
        {
            Descriptor = new Block3(o, s, grid, orient),
            Cubes = cubes,
        };
    }

    /// <summary>Encode one cube: [faces0, faces1, faces2, tex0..5, material, merged, visible, children?].</summary>
    public static Godot.Collections.Array WriteCube(Cube c)
    {
        var a = new Godot.Collections.Array
        {
            (long)c.Faces[0], (long)c.Faces[1], (long)c.Faces[2],
        };
        for (int i = 0; i < 6; i++) a.Add((int)c.Texture[i]);
        a.Add((int)c.Material);
        a.Add((int)c.Merged);
        a.Add((int)c.Visible);
        if (c.Children == null) a.Add(false);
        else
        {
            a.Add(true);
            var ch = new Godot.Collections.Array();
            for (int i = 0; i < 8; i++) ch.Add(WriteCube(c.Children[i]));
            a.Add(ch);
        }
        return a;
    }

    public static Cube ReadCube(Godot.Collections.Array a)
    {
        var c = new Cube();
        c.Faces[0] = (uint)(long)a[0];
        c.Faces[1] = (uint)(long)a[1];
        c.Faces[2] = (uint)(long)a[2];
        for (int i = 0; i < 6; i++) c.Texture[i] = (ushort)(int)a[3 + i];
        c.Material = (ushort)(int)a[9];
        c.Merged = (byte)(int)a[10];
        c.Visible = (byte)(int)a[11];
        if ((bool)a[12])
        {
            var ch = a[13].As<Godot.Collections.Array>();
            c.Children = new Cube[8];
            for (int i = 0; i < 8; i++) c.Children[i] = ReadCube(ch[i].As<Godot.Collections.Array>());
        }
        return c;
    }
}
