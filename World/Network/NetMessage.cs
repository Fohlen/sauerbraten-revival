using System;
using System.Collections.Generic;
using FlatSharp;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Commands;

namespace SauerbratenRevival.World.Network;

/// <summary>
/// Network message packaging and translation between in-engine <see cref="Command"/>
/// objects and FlatSharp FlatBuffers types (<see cref="WorldCommand"/>, <see cref="NetworkPacket"/>)
/// for zero-copy live ENet networking and streaming logs (ADR 0008).
/// </summary>
public static class NetMessage
{
    public static SelectionBox ToSelectionBox(in SelectionPayload sel) => new()
    {
        Origin = new Vec3i { X = sel.Origin.X, Y = sel.Origin.Y, Z = sel.Origin.Z },
        Size = new Vec3i { X = sel.Size.X, Y = sel.Size.Y, Z = sel.Size.Z },
        Grid = sel.Grid,
        Orient = sel.Orient,
    };

    public static SelectionPayload ToSelectionPayload(SelectionBox? box)
    {
        if (box == null) return default;
        return new SelectionPayload(
            new Vector3I(box.Origin.X, box.Origin.Y, box.Origin.Z),
            new Vector3I(box.Size.X, box.Size.Y, box.Size.Z),
            box.Grid,
            box.Orient);
    }

    public static EntityCmd ToEntityCmd(in EntityPayload ent) => new()
    {
        EntityId = ent.Id,
        Type = ent.Type,
        Pos = new Vec3f
        {
            X = ent.FixedX / (float)FixedPoint.DMF,
            Y = ent.FixedY / (float)FixedPoint.DMF,
            Z = ent.FixedZ / (float)FixedPoint.DMF,
        },
        Attr1 = ent.Attr1,
        Attr2 = ent.Attr2,
        Attr3 = ent.Attr3,
        Attr4 = ent.Attr4,
        Attr5 = ent.Attr5,
    };

    public static EntityPayload ToEntityPayload(EntityCmd cmd) => new(
        cmd.EntityId,
        (byte)cmd.Type,
        (int)System.MathF.Round((cmd.Pos?.X ?? 0f) * FixedPoint.DMF),
        (int)System.MathF.Round((cmd.Pos?.Y ?? 0f) * FixedPoint.DMF),
        (int)System.MathF.Round((cmd.Pos?.Z ?? 0f) * FixedPoint.DMF),
        (short)cmd.Attr1,
        (short)cmd.Attr2,
        (short)cmd.Attr3,
        (short)cmd.Attr4,
        (short)cmd.Attr5);

    public static SetEnvironmentCmd ToSetEnvironmentCmd(SetEnvironmentPayload env) => new()
    {
        SunColor = env.SunColor,
        SunScale = env.SunScale,
        SunYaw = env.SunYaw,
        SunPitch = env.SunPitch,
        SkyColor = env.SkyColor,
        AmbientColor = env.AmbientColor,
        Skybox = env.Skybox ?? "",
        FogDistance = env.FogDistance,
        FogColor = env.FogColor,
    };

    public static SetEnvironmentPayload ToSetEnvironmentPayload(SetEnvironmentCmd cmd) => new()
    {
        SunColor = cmd.SunColor,
        SunScale = cmd.SunScale,
        SunYaw = cmd.SunYaw,
        SunPitch = cmd.SunPitch,
        SkyColor = cmd.SkyColor,
        AmbientColor = cmd.AmbientColor,
        Skybox = cmd.Skybox ?? "",
        FogDistance = cmd.FogDistance,
        FogColor = cmd.FogColor,
    };

    /// <summary>Translate an in-engine <see cref="Command"/> into a FlatSharp <see cref="WorldCommand"/>.</summary>
    public static WorldCommand ToFlatCommand(this Command cmd, uint timestamp = 0)
    {
        var fbCmd = new WorldCommand
        {
            Sequence = cmd.Sequence,
            PeerId = cmd.AuthorPeerId,
            Timestamp = timestamp,
            Op = cmd.Op switch
            {
                Commands.WorldOp.DeleteCube => Format.WorldOp.DeleteCube,
                Commands.WorldOp.EditFace => Format.WorldOp.EditFace,
                Commands.WorldOp.SetFaceTexture => Format.WorldOp.SetFaceTexture,
                Commands.WorldOp.ReplaceTexture => Format.WorldOp.ReplaceTexture,
                Commands.WorldOp.SetFaceMaterial => Format.WorldOp.SetFaceMaterial,
                Commands.WorldOp.FlipCube => Format.WorldOp.FlipCube,
                Commands.WorldOp.RotateCube => Format.WorldOp.RotateCube,
                Commands.WorldOp.Remip => Format.WorldOp.Remip,
                Commands.WorldOp.NewMap => Format.WorldOp.NewMap,
                Commands.WorldOp.AddEntity => Format.WorldOp.AddEntity,
                Commands.WorldOp.RemoveEntity => Format.WorldOp.RemoveEntity,
                Commands.WorldOp.EditEntity => Format.WorldOp.EditEntity,
                Commands.WorldOp.SetEnvironment => Format.WorldOp.SetEnvironment,
                Commands.WorldOp.ReplaceBlock => Format.WorldOp.ReplaceBlock,
                _ => Format.WorldOp.None,
            },
        };

        switch (cmd.Payload)
        {
            case DeleteCubePayload del:
                fbCmd.Payload = new DeleteCubeCmd { Sel = ToSelectionBox(del.Selection) };
                break;
            case EditFacePayload ef:
                fbCmd.Payload = new EditFaceCmd
                {
                    Sel = ToSelectionBox(ef.Selection),
                    Dir = ef.Dir,
                    Mode = ef.Mode,
                    Stride = 0,
                };
                break;
            case SetFaceTexturePayload st:
                fbCmd.Payload = new SetTextureCmd
                {
                    Sel = ToSelectionBox(st.Selection),
                    TextureId = st.Texture,
                    Flags = st.Orient,
                };
                break;
            case RemipPayload rp:
                fbCmd.Payload = new RemipCmd { MipVis = rp.MipVis };
                break;
            case EntityPayload ep:
                fbCmd.Payload = ToEntityCmd(ep);
                break;
            case SetEnvironmentPayload env:
                fbCmd.Payload = ToSetEnvironmentCmd(env);
                break;
        }

        return fbCmd;
    }

    /// <summary>Translate a FlatSharp <see cref="WorldCommand"/> back into an in-engine <see cref="Command"/>.</summary>
    public static Command ToCommand(this WorldCommand fbCmd)
    {
        var op = fbCmd.Op switch
        {
            Format.WorldOp.DeleteCube => Commands.WorldOp.DeleteCube,
            Format.WorldOp.EditFace => Commands.WorldOp.EditFace,
            Format.WorldOp.SetFaceTexture => Commands.WorldOp.SetFaceTexture,
            Format.WorldOp.ReplaceTexture => Commands.WorldOp.ReplaceTexture,
            Format.WorldOp.SetFaceMaterial => Commands.WorldOp.SetFaceMaterial,
            Format.WorldOp.FlipCube => Commands.WorldOp.FlipCube,
            Format.WorldOp.RotateCube => Commands.WorldOp.RotateCube,
            Format.WorldOp.Remip => Commands.WorldOp.Remip,
            Format.WorldOp.NewMap => Commands.WorldOp.NewMap,
            Format.WorldOp.AddEntity => Commands.WorldOp.AddEntity,
            Format.WorldOp.RemoveEntity => Commands.WorldOp.RemoveEntity,
            Format.WorldOp.EditEntity => Commands.WorldOp.EditEntity,
            Format.WorldOp.SetEnvironment => Commands.WorldOp.SetEnvironment,
            Format.WorldOp.ReplaceBlock => Commands.WorldOp.ReplaceBlock,
            _ => Commands.WorldOp.Snapshot,
        };

        ICommandPayload payload = EmptyPayload.Instance;
        if (fbCmd.Payload.HasValue)
        {
            var p = fbCmd.Payload.Value;
            payload = p.Kind switch
            {
                CommandPayload.ItemKind.DeleteCubeCmd => new DeleteCubePayload(ToSelectionPayload(p.Item3.Sel)),
                CommandPayload.ItemKind.EditFaceCmd => new EditFacePayload(ToSelectionPayload(p.Item1.Sel), p.Item1.Dir, p.Item1.Mode),
                CommandPayload.ItemKind.SetTextureCmd => new SetFaceTexturePayload(ToSelectionPayload(p.Item2.Sel), p.Item2.TextureId, p.Item2.Flags),
                CommandPayload.ItemKind.RemipCmd => new RemipPayload(p.Item4.MipVis),
                CommandPayload.ItemKind.EntityCmd => ToEntityPayload(p.Item5),
                CommandPayload.ItemKind.SetEnvironmentCmd => ToSetEnvironmentPayload(p.Item6),
                _ => EmptyPayload.Instance,
            };
        }

        return new Command(op, payload, fbCmd.PeerId)
        {
            Sequence = fbCmd.Sequence,
        };
    }

    /// <summary>Pack commands into a serialized byte buffer representing a <see cref="NetworkPacket"/>.</summary>
    public static byte[] Pack(uint packetId, IEnumerable<Command> commands, uint timestamp = 0)
    {
        var packet = new NetworkPacket
        {
            PacketId = packetId,
            Commands = new List<WorldCommand>(),
        };

        foreach (var c in commands)
            packet.Commands.Add(c.ToFlatCommand(timestamp));

        byte[] buffer = new byte[NetworkPacket.Serializer.GetMaxSize(packet)];
        int bytesWritten = NetworkPacket.Serializer.Write(buffer, packet);
        if (bytesWritten < buffer.Length)
            Array.Resize(ref buffer, bytesWritten);
        return buffer;
    }

    /// <summary>Unpack a serialized byte buffer into a <see cref="NetworkPacket"/>.</summary>
    public static NetworkPacket Unpack(byte[] data) => NetworkPacket.Serializer.Parse(data);

    /// <summary>Unpack a serialized byte buffer and materialize all contained in-engine <see cref="Command"/>s.</summary>
    public static List<Command> UnpackCommands(byte[] data)
    {
        var packet = Unpack(data);
        var result = new List<Command>(packet.Commands?.Count ?? 0);
        if (packet.Commands != null)
        {
            foreach (var fbCmd in packet.Commands)
                result.Add(fbCmd.ToCommand());
        }
        return result;
    }
}
