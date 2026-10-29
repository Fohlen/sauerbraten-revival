using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlatSharp;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Editor;

namespace SauerbratenRevival.World.Network;

/// <summary>
/// Serialization utilities bridging FlatSharp FlatBuffers schema types with
/// network packets and in-memory byte buffers.
/// </summary>
public static class NetMessage
{
    public static byte[] Encode(Command cmd)
    {
        byte[] buffer = new byte[Command.Serializer.GetMaxSize(cmd)];
        int bytesWritten = Command.Serializer.Write(buffer, cmd);
        return buffer.AsSpan(0, bytesWritten).ToArray();
    }

    public static Command Decode(byte[] bytes) => Command.Serializer.Parse(bytes);

    public static byte[] PackCommands(IEnumerable<Command> commands, uint packetId = 0)
    {
        var packet = new NetworkPacket
        {
            PacketId = packetId,
            Commands = commands.ToList()
        };
        byte[] buffer = new byte[NetworkPacket.Serializer.GetMaxSize(packet)];
        int bytesWritten = NetworkPacket.Serializer.Write(buffer, packet);
        return buffer.AsSpan(0, bytesWritten).ToArray();
    }

    public static List<Command> UnpackCommands(byte[] packetBytes)
    {
        var packet = NetworkPacket.Serializer.Parse(packetBytes);
        return packet.Commands != null ? packet.Commands.ToList() : new List<Command>();
    }

    public static byte[] SerializeBlock(OctaEdit.CapturedBlock block)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(block.Descriptor.O.X);
        w.Write(block.Descriptor.O.Y);
        w.Write(block.Descriptor.O.Z);
        w.Write(block.Descriptor.S.X);
        w.Write(block.Descriptor.S.Y);
        w.Write(block.Descriptor.S.Z);
        w.Write(block.Descriptor.Grid);
        w.Write(block.Descriptor.Orient);
        w.Write(block.Cubes.Length);
        foreach (var c in block.Cubes)
            WriteCube(w, c);
        return ms.ToArray();
    }

    public static OctaEdit.CapturedBlock DeserializeBlock(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        var b = new OctaEdit.CapturedBlock
        {
            Descriptor = new Block3(
                new Vector3I(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
                new Vector3I(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
                r.ReadInt32(),
                r.ReadInt32())
        };
        int count = r.ReadInt32();
        b.Cubes = new Cube[count];
        for (int i = 0; i < count; i++)
            b.Cubes[i] = ReadCube(r);
        return b;
    }

    private static void WriteCube(BinaryWriter w, Cube c)
    {
        byte flags = 0;
        if (c.Children != null) flags |= 0x01;
        if (c.Material != Octa.MAT_AIR) flags |= 0x02;
        if (c.Merged != 0) flags |= 0x04;
        if (c.Visible != 0) flags |= 0x08;
        w.Write(flags);
        w.Write(c.Faces[0]);
        w.Write(c.Faces[1]);
        w.Write(c.Faces[2]);
        for (int i = 0; i < 6; i++) w.Write(c.Texture[i]);
        if ((flags & 0x02) != 0) w.Write(c.Material);
        if ((flags & 0x04) != 0) w.Write(c.Merged);
        if ((flags & 0x08) != 0) w.Write(c.Visible);
        if ((flags & 0x01) != 0)
        {
            foreach (var child in c.Children!)
                WriteCube(w, child);
        }
    }

    private static Cube ReadCube(BinaryReader r)
    {
        var c = new Cube();
        byte flags = r.ReadByte();
        c.Faces[0] = r.ReadUInt32();
        c.Faces[1] = r.ReadUInt32();
        c.Faces[2] = r.ReadUInt32();
        for (int i = 0; i < 6; i++) c.Texture[i] = r.ReadUInt16();
        if ((flags & 0x02) != 0) c.Material = r.ReadUInt16();
        if ((flags & 0x04) != 0) c.Merged = r.ReadByte();
        if ((flags & 0x08) != 0) c.Visible = r.ReadByte();
        if ((flags & 0x01) != 0)
        {
            c.Children = new Cube[8];
            for (int i = 0; i < 8; i++)
                c.Children[i] = ReadCube(r);
        }
        return c;
    }
}
