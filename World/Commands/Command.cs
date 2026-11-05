global using Command = SauerbratenRevival.Format.Command;
global using WorldOp = SauerbratenRevival.Format.WorldOp;
global using PlayerUpdateCmd = SauerbratenRevival.Format.PlayerUpdateCmd;
global using PlayerSpawnCmd = SauerbratenRevival.Format.PlayerSpawnCmd;
global using PlayerDeathCmd = SauerbratenRevival.Format.PlayerDeathCmd;
global using PlayerTeleportCmd = SauerbratenRevival.Format.PlayerTeleportCmd;
global using PlayerJumpPadCmd = SauerbratenRevival.Format.PlayerJumpPadCmd;
global using DemoPackage = SauerbratenRevival.Format.DemoPackage;

using System;
using Godot;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Editor;
using SauerbratenRevival.World.Entities;

namespace SauerbratenRevival.Format;

/// <summary>
/// Domain methods and constructors extending the FlatSharp-generated <see cref="Command"/>.
/// Serves as the single unified command representation for in-memory execution,
/// undo/redo, network transmission, and disk storage.
/// </summary>
public partial class Command
{
    public int AuthorPeerId
    {
        get => PeerId;
        set => PeerId = value;
    }

    public Command(WorldOp op, CommandPayload payload, int author = 0)
    {
        Op = op;
        Payload = payload;
        PeerId = author;
    }

    public SelectionBox? Selection => Payload?.Kind switch
    {
        CommandPayload.ItemKind.EditFaceCmd => Payload.Value.Item1.Sel,
        CommandPayload.ItemKind.SetTextureCmd => Payload.Value.Item2.Sel,
        CommandPayload.ItemKind.ReplaceTextureCmd => Payload.Value.Item3.Sel,
        CommandPayload.ItemKind.DeleteCubeCmd => Payload.Value.Item4.Sel,
        CommandPayload.ItemKind.FlipCubeCmd => Payload.Value.Item6.Sel,
        CommandPayload.ItemKind.RotateCubeCmd => Payload.Value.Item7.Sel,
        CommandPayload.ItemKind.SetMaterialCmd => Payload.Value.Item10.Sel,
        CommandPayload.ItemKind.ReplaceBlockCmd => Payload.Value.Item12.Sel,
        _ => null
    };

    public PlayerUpdateCmd? PlayerUpdate => Payload?.Kind == CommandPayload.ItemKind.PlayerUpdateCmd ? Payload.Value.Item13 : null;
    public PlayerSpawnCmd? PlayerSpawn => Payload?.Kind == CommandPayload.ItemKind.PlayerSpawnCmd ? Payload.Value.Item14 : null;
    public PlayerDeathCmd? PlayerDeath => Payload?.Kind == CommandPayload.ItemKind.PlayerDeathCmd ? Payload.Value.Item15 : null;
    public PlayerTeleportCmd? PlayerTeleport => Payload?.Kind == CommandPayload.ItemKind.PlayerTeleportCmd ? Payload.Value.Item16 : null;
    public PlayerJumpPadCmd? PlayerJumpPad => Payload?.Kind == CommandPayload.ItemKind.PlayerJumpPadCmd ? Payload.Value.Item17 : null;

    /// <summary>
    /// Try to get the world-space bounding box and margin (grid size) of the geometry
    /// region affected by this command, using <see cref="Selection"/>.
    /// </summary>
    public bool TryGetAffectedRegion(out Aabb aabb, out float margin)
    {
        margin = 0f;
        aabb = default;
        var sel = Selection;
        if (sel == null || sel.Size == null || (sel.Size.X == 0 && sel.Size.Y == 0 && sel.Size.Z == 0))
            return false;

        margin = sel.Grid;
        aabb = sel.ToAabb();
        return true;
    }

    public override string ToString() => $"{Op}#{Sequence}(peer {PeerId})";

    public static Command CreateDeleteCube(Selection sel, int author = 0) => new()
    {
        Op = WorldOp.DeleteCube,
        PeerId = author,
        Payload = new(new DeleteCubeCmd { Sel = sel.ToSelectionBox() })
    };

    public static Command CreateEditFace(Selection sel, int dir, int mode, int stride = 1, int author = 0) => new()
    {
        Op = WorldOp.EditFace,
        PeerId = author,
        Payload = new(new EditFaceCmd { Sel = sel.ToSelectionBox(), Dir = dir, Mode = mode, Stride = stride })
    };

    public static Command CreateSetTexture(Selection sel, int textureId, int flags = 0, int author = 0) => new()
    {
        Op = WorldOp.SetFaceTexture,
        PeerId = author,
        Payload = new(new SetTextureCmd { Sel = sel.ToSelectionBox(), TextureId = textureId, Flags = flags })
    };

    public static Command CreateReplaceTexture(Selection? sel, short oldTex, short newTex, bool inSel, int author = 0) => new()
    {
        Op = WorldOp.ReplaceTexture,
        PeerId = author,
        Payload = new(new ReplaceTextureCmd
        {
            Sel = sel?.ToSelectionBox() ?? new SelectionBox(),
            OldTexture = oldTex,
            NewTexture = newTex,
            InSelection = inSel
        })
    };

    public static Command CreateSetMaterial(Selection sel, int matId, int filter = 0, int author = 0) => new()
    {
        Op = WorldOp.SetFaceMaterial,
        PeerId = author,
        Payload = new(new SetMaterialCmd { Sel = sel.ToSelectionBox(), MaterialId = matId, Filter = filter })
    };

    public static Command CreateFlip(Selection sel, int author = 0) => new()
    {
        Op = WorldOp.FlipCube,
        PeerId = author,
        Payload = new(new FlipCubeCmd { Sel = sel.ToSelectionBox() })
    };

    public static Command CreateRotate(Selection sel, int direction, int author = 0) => new()
    {
        Op = WorldOp.RotateCube,
        PeerId = author,
        Payload = new(new RotateCubeCmd { Sel = sel.ToSelectionBox(), Direction = direction })
    };

    public static Command CreateRemip(bool mipVis, int author = 0) => new()
    {
        Op = WorldOp.Remip,
        PeerId = author,
        Payload = new(new RemipCmd { MipVis = mipVis })
    };

    public static Command CreateNewMap(int scale, int author = 0) => new()
    {
        Op = WorldOp.NewMap,
        PeerId = author,
        Payload = new(new NewMapCmd { Scale = scale })
    };

    public static Command CreateReplaceBlock(Selection sel, byte[] blockData, int author = 0) => new()
    {
        Op = WorldOp.ReplaceBlock,
        PeerId = author,
        Payload = new(new ReplaceBlockCmd { Sel = sel.ToSelectionBox(), Data = blockData })
    };

    public static Command CreateAddEntity(MapEntity entity, int author = 0) => new()
    {
        Op = WorldOp.AddEntity,
        PeerId = author,
        Payload = new(entity.ToEntityCmd())
    };

    public static Command CreateRemoveEntity(ulong entityId, int author = 0) => new()
    {
        Op = WorldOp.RemoveEntity,
        PeerId = author,
        Payload = new(new EntityCmd { EntityId = entityId })
    };

    public static Command CreateEditEntity(MapEntity entity, int author = 0) => new()
    {
        Op = WorldOp.EditEntity,
        PeerId = author,
        Payload = new(entity.ToEntityCmd())
    };

    public static Command CreateSetEnvironment(SetEnvironmentCmd env, int author = 0) => new()
    {
        Op = WorldOp.SetEnvironment,
        PeerId = author,
        Payload = new(env)
    };

    public static Command CreatePlayerUpdate(
        int tick,
        int move,
        int strafe,
        bool jumping,
        float yaw,
        float pitch,
        bool attacking,
        int selectedGun,
        Vector3? pos = null,
        Vector3? vel = null,
        byte physState = 0,
        int lifeSequence = 0,
        int author = 0) => new()
        {
            Op = WorldOp.PlayerUpdate,
            PeerId = author,
            Payload = new(new PlayerUpdateCmd
            {
                Tick = tick,
                Move = move,
                Strafe = strafe,
                Jumping = jumping,
                Yaw = yaw,
                Pitch = pitch,
                Attacking = attacking,
                SelectedGun = selectedGun,
                Pos = pos.HasValue ? new Vec3f { X = pos.Value.X, Y = pos.Value.Y, Z = pos.Value.Z } : null,
                Vel = vel.HasValue ? new Vec3f { X = vel.Value.X, Y = vel.Value.Y, Z = vel.Value.Z } : null,
                PhysState = physState,
                LifeSequence = lifeSequence
            })
        };

    public static Command CreatePlayerUpdate(PlayerUpdateCmd update, int author = 0) => new()
    {
        Op = WorldOp.PlayerUpdate,
        PeerId = author,
        Payload = new(update)
    };

    public static Command CreatePlayerSpawn(Vector3 pos, float yaw, int lifeSequence = 0, int gun = 0, int health = 100, int armour = 0, System.Collections.Generic.IList<int>? ammo = null, int author = 0) => new()
    {
        Op = WorldOp.PlayerSpawn,
        PeerId = author,
        Payload = new(new PlayerSpawnCmd
        {
            Pos = new Vec3f { X = pos.X, Y = pos.Y, Z = pos.Z },
            Yaw = yaw,
            LifeSequence = lifeSequence,
            SelectedGun = gun,
            Health = health,
            Armour = armour,
            Ammo = ammo
        })
    };

    public static Command CreatePlayerDeath(
        int victimPeerId,
        int killerPeerId = -1,
        int gun = -1,
        bool isSuicide = false,
        Vector3 deathPos = default,
        Vector3 impulse = default,
        int author = 0) => new()
        {
            Op = WorldOp.PlayerDeath,
            PeerId = author,
            Payload = new(new PlayerDeathCmd
            {
                VictimPeerId = victimPeerId,
                KillerPeerId = killerPeerId,
                Gun = gun,
                IsSuicide = isSuicide,
                DeathPos = new Vec3f { X = deathPos.X, Y = deathPos.Y, Z = deathPos.Z },
                Impulse = new Vec3f { X = impulse.X, Y = impulse.Y, Z = impulse.Z }
            })
        };

    public static Command CreatePlayerTeleport(int teleportId, Vector3 destPos, float destYaw, int author = 0) => new()
    {
        Op = WorldOp.PlayerTeleport,
        PeerId = author,
        Payload = new(new PlayerTeleportCmd
        {
            TeleportId = teleportId,
            DestPos = new Vec3f { X = destPos.X, Y = destPos.Y, Z = destPos.Z },
            DestYaw = destYaw
        })
    };

    public static Command CreatePlayerJumpPad(int padId, Vector3 impulse, int author = 0) => new()
    {
        Op = WorldOp.PlayerJumpPad,
        PeerId = author,
        Payload = new(new PlayerJumpPadCmd
        {
            PadId = padId,
            Impulse = new Vec3f { X = impulse.X, Y = impulse.Y, Z = impulse.Z }
        })
    };
}

public partial class SelectionBox
{
    public SelectionBox(Vector3I origin, Vector3I size, int grid, int orient)
    {
        Origin = new Vec3i { X = origin.X, Y = origin.Y, Z = origin.Z };
        Size = new Vec3i { X = size.X, Y = size.Y, Z = size.Z };
        Grid = grid;
        Orient = orient;
    }

    public SelectionBox(Selection sel) : this(sel.O, sel.S, sel.Grid, sel.Orient) { }
    public SelectionBox(Block3 b) : this(b.O, b.S, b.Grid, b.Orient) { }

    public Block3 ToBlock() => new(
        new Vector3I(Origin?.X ?? 0, Origin?.Y ?? 0, Origin?.Z ?? 0),
        new Vector3I(Size?.X ?? 0, Size?.Y ?? 0, Size?.Z ?? 0),
        Grid,
        Orient);

    public Selection ToSelection() => new(
        new Vector3I(Origin?.X ?? 0, Origin?.Y ?? 0, Origin?.Z ?? 0),
        new Vector3I(Size?.X ?? 0, Size?.Y ?? 0, Size?.Z ?? 0),
        Grid,
        Orient);

    public Aabb ToAabb() => new(
        new Vector3(Origin?.X ?? 0, Origin?.Y ?? 0, Origin?.Z ?? 0),
        new Vector3((Size?.X ?? 0) * Grid, (Size?.Y ?? 0) * Grid, (Size?.Z ?? 0) * Grid));
}

public static class CommandFormatExtensions
{
    public static Vector3I ToVector3I(this Vec3i v) => new(v.X, v.Y, v.Z);
    public static Selection ToSelection(this Block3 b) => new(b.O, b.S, b.Grid, b.Orient);
    public static Block3 ToBlock(this Selection s) => new(s.O, s.S, s.Grid, s.Orient);
    public static SelectionBox ToSelectionBox(this Selection sel) => new(sel);
    public static SelectionBox ToSelectionBox(this Block3 block) => new(block);

    public static EntityCmd ToEntityCmd(this MapEntity e)
    {
        var (x, y, z) = e.FixedPosition;
        return new EntityCmd
        {
            EntityId = e.Id,
            Type = e.Type,
            Pos = new Vec3i { X = x, Y = y, Z = z },
            Attr1 = e.Attr1,
            Attr2 = e.Attr2,
            Attr3 = e.Attr3,
            Attr4 = e.Attr4,
            Attr5 = e.Attr5
        };
    }

    public static MapEntity ToMapEntity(this EntityCmd c)
    {
        return new MapEntity
        {
            Id = c.EntityId,
            Type = (byte)c.Type,
            Position = new Vector3(
                (c.Pos?.X ?? 0) / (float)FixedPoint.DMF,
                (c.Pos?.Y ?? 0) / (float)FixedPoint.DMF,
                (c.Pos?.Z ?? 0) / (float)FixedPoint.DMF),
            Attr1 = (short)c.Attr1,
            Attr2 = (short)c.Attr2,
            Attr3 = (short)c.Attr3,
            Attr4 = (short)c.Attr4,
            Attr5 = (short)c.Attr5
        };
    }
}
