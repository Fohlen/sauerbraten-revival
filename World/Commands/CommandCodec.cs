using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;

namespace SauerbratenRevival.World.Commands;

/// <summary>
/// Canonical binary encoding for commands using MemoryMarshal over unmanaged structs.
///
/// Every fixed-size command is blitted directly with MemoryMarshal.Write/Read,
/// ensuring zero heap allocations and exact, deterministic memory layouts.
/// </summary>
public static class CommandCodec
{
    public const byte FormatVersion = 2;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct CommandHeader
    {
        public const int HeaderSize = 16;

        public readonly byte Version;
        public readonly ulong Sequence;
        public readonly int AuthorPeerId;
        public readonly byte Source;
        public readonly ushort Op;

        public CommandHeader(byte version, ulong sequence, int author, CommandSource source, WorldOp op)
        {
            Version = version;
            Sequence = sequence;
            AuthorPeerId = author;
            Source = (byte)source;
            Op = (ushort)op;
        }
    }

    public static byte[] Encode(Command cmd)
    {
        var header = new CommandHeader(FormatVersion, cmd.Sequence, cmd.AuthorPeerId, cmd.Source, cmd.Op);

        switch (cmd.Op)
        {
            case WorldOp.DeleteCube:
                return EncodeBlit(header, (DeleteCubePayload)cmd.Payload);
            case WorldOp.EditFace:
                return EncodeBlit(header, (EditFacePayload)cmd.Payload);
            case WorldOp.SetFaceTexture:
                return EncodeBlit(header, (SetFaceTexturePayload)cmd.Payload);
            case WorldOp.ReplaceTexture:
                return EncodeBlit(header, (ReplaceTexturePayload)cmd.Payload);
            case WorldOp.SetFaceMaterial:
                return EncodeBlit(header, (SetFaceMaterialPayload)cmd.Payload);
            case WorldOp.FlipCube:
            case WorldOp.RotateCube:
                return EncodeBlit(header, (TransformCubePayload)cmd.Payload);
            case WorldOp.Remip:
                return EncodeBlit(header, (RemipPayload)cmd.Payload);
            case WorldOp.NewMap:
                return EncodeBlit(header, (NewMapPayload)cmd.Payload);
            case WorldOp.AddEntity:
            case WorldOp.RemoveEntity:
            case WorldOp.EditEntity:
                return EncodeBlit(header, (EntityPayload)cmd.Payload);
            case WorldOp.Snapshot:
                return EncodeBlit(header, (EmptyPayload)cmd.Payload);
            case WorldOp.ReplaceBlock:
                return EncodeReplaceBlock(header, (ReplaceBlockPayload)cmd.Payload);
            default:
                throw new NotSupportedException($"Cannot encode payload for {cmd.Op}");
        }
    }

    public static Command Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < CommandHeader.HeaderSize)
            throw new InvalidOperationException($"Command payload too short ({data.Length} bytes, expected at least {CommandHeader.HeaderSize})");

        var header = MemoryMarshal.Read<CommandHeader>(data[..CommandHeader.HeaderSize]);
        if (header.Version != FormatVersion)
            throw new InvalidOperationException($"Unsupported command format version {header.Version} (expected {FormatVersion})");

        var op = (WorldOp)header.Op;
        var source = (CommandSource)header.Source;
        var payloadSpan = data[CommandHeader.HeaderSize..];

        ICommandPayload payload = op switch
        {
            WorldOp.DeleteCube => ReadBlit<DeleteCubePayload>(payloadSpan),
            WorldOp.EditFace => ReadBlit<EditFacePayload>(payloadSpan),
            WorldOp.SetFaceTexture => ReadBlit<SetFaceTexturePayload>(payloadSpan),
            WorldOp.ReplaceTexture => ReadBlit<ReplaceTexturePayload>(payloadSpan),
            WorldOp.SetFaceMaterial => ReadBlit<SetFaceMaterialPayload>(payloadSpan),
            WorldOp.FlipCube or WorldOp.RotateCube => ReadBlit<TransformCubePayload>(payloadSpan),
            WorldOp.Remip => ReadBlit<RemipPayload>(payloadSpan),
            WorldOp.NewMap => ReadBlit<NewMapPayload>(payloadSpan),
            WorldOp.AddEntity or WorldOp.RemoveEntity or WorldOp.EditEntity => ReadBlit<EntityPayload>(payloadSpan),
            WorldOp.Snapshot => EmptyPayload.Instance,
            WorldOp.ReplaceBlock => DecodeReplaceBlock(payloadSpan),
            _ => throw new NotSupportedException($"Cannot decode payload for {op}")
        };

        return new Command(op, payload, header.AuthorPeerId, source) { Sequence = header.Sequence };
    }

    public static Command Decode(byte[] data) => Decode(data.AsSpan());

    private static byte[] EncodeBlit<T>(in CommandHeader header, in T payload) where T : struct
    {
        int payloadSize = Unsafe.SizeOf<T>();
        byte[] bytes = new byte[CommandHeader.HeaderSize + payloadSize];
        MemoryMarshal.Write(bytes.AsSpan(0, CommandHeader.HeaderSize), in header);
        MemoryMarshal.Write(bytes.AsSpan(CommandHeader.HeaderSize), in payload);
        return bytes;
    }

    private static T ReadBlit<T>(ReadOnlySpan<byte> span) where T : struct
    {
        int expectedSize = Unsafe.SizeOf<T>();
        if (span.Length < expectedSize)
            throw new InvalidOperationException($"Payload span too short for {typeof(T).Name}: got {span.Length}, expected {expectedSize}");
        return MemoryMarshal.Read<T>(span);
    }

    // ---- ReplaceBlock (variable length cube payload) ----

    private static byte[] EncodeReplaceBlock(in CommandHeader header, ReplaceBlockPayload p)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        Span<byte> headerSpan = stackalloc byte[CommandHeader.HeaderSize];
        MemoryMarshal.Write(headerSpan, in header);
        w.Write(headerSpan);

        Span<byte> targetSpan = stackalloc byte[SelectionPayload.PayloadSize];
        MemoryMarshal.Write(targetSpan, in p.Target);
        w.Write(targetSpan);

        if (p.Block == null)
        {
            w.Write(false);
        }
        else
        {
            w.Write(true);
            WriteBlock(w, p.Block);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static ReplaceBlockPayload DecodeReplaceBlock(ReadOnlySpan<byte> span)
    {
        if (span.Length < SelectionPayload.PayloadSize + 1)
            throw new InvalidOperationException("ReplaceBlock payload too short");

        var target = MemoryMarshal.Read<SelectionPayload>(span[..SelectionPayload.PayloadSize]);
        bool hasBlock = span[SelectionPayload.PayloadSize] != 0;
        if (!hasBlock)
            return new ReplaceBlockPayload(target, null);

        var body = span[(SelectionPayload.PayloadSize + 1)..];
        using var ms = new MemoryStream(body.ToArray(), writable: false);
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        var block = ReadBlock(r);
        return new ReplaceBlockPayload(target, block);
    }

    private static void WriteBlock(BinaryWriter w, OctaEdit.CapturedBlock block)
    {
        var d = block.Descriptor;
        w.Write(d.O.X); w.Write(d.O.Y); w.Write(d.O.Z);
        w.Write(d.S.X); w.Write(d.S.Y); w.Write(d.S.Z);
        w.Write(d.Grid);
        w.Write(d.Orient);

        w.Write(block.Cubes.Length);
        foreach (var c in block.Cubes) WriteCube(w, c);
    }

    private static OctaEdit.CapturedBlock ReadBlock(BinaryReader r)
    {
        var o = new Vector3I(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        var s = new Vector3I(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        int grid = r.ReadInt32();
        int orient = r.ReadInt32();
        int count = r.ReadInt32();
        var cubes = new Cube[count];
        for (int k = 0; k < count; k++) cubes[k] = ReadCube(r);
        return new OctaEdit.CapturedBlock
        {
            Descriptor = new Block3(o, s, grid, orient),
            Cubes = cubes,
        };
    }

    private static void WriteCube(BinaryWriter w, Cube c)
    {
        w.Write(c.Faces[0]);
        w.Write(c.Faces[1]);
        w.Write(c.Faces[2]);
        for (int i = 0; i < 6; i++) w.Write(c.Texture[i]);
        w.Write(c.Material);
        w.Write(c.Merged);
        w.Write(c.Visible);
        if (c.Children == null)
        {
            w.Write(false);
        }
        else
        {
            w.Write(true);
            for (int i = 0; i < 8; i++) WriteCube(w, c.Children[i]);
        }
    }

    private static Cube ReadCube(BinaryReader r)
    {
        var c = new Cube();
        c.Faces[0] = r.ReadUInt32();
        c.Faces[1] = r.ReadUInt32();
        c.Faces[2] = r.ReadUInt32();
        for (int i = 0; i < 6; i++) c.Texture[i] = r.ReadUInt16();
        c.Material = r.ReadUInt16();
        c.Merged = r.ReadByte();
        c.Visible = r.ReadByte();
        if (r.ReadBoolean())
        {
            c.Children = new Cube[8];
            for (int i = 0; i < 8; i++) c.Children[i] = ReadCube(r);
        }
        return c;
    }
}
