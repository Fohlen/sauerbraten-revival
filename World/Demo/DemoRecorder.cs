using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Runtime;

namespace SauerbratenRevival.World.Demo;

/// <summary>
/// Records player updates, lifecycle events, and world mutations for one or multiple players
/// into a <see cref="DemoPackage"/>. Every event is captured as a unified <see cref="Command"/>.
/// </summary>
public class DemoRecorder
{
    private readonly List<Command> _commands = new();
    private readonly Dictionary<int, MapPlayer> _attachedPlayers = new();
    private readonly Dictionary<int, (Action<PlayerUpdateCmd> UpdateCb, MapPlayer.DiedEventHandler DiedCb, MapPlayer.RespawnedEventHandler RespawnedCb)> _callbacks = new();
    private Commands.CommandSink? _attachedSink;

    public bool IsRecording { get; private set; }
    public string MapName { get; set; } = string.Empty;
    public string GameMode { get; set; } = string.Empty;
    public Vector3 InitialSpawnPos { get; set; }
    public float InitialSpawnYaw { get; set; }

    public IReadOnlyList<Command> Commands => _commands;
    public IReadOnlyCollection<MapPlayer> AttachedPlayers => _attachedPlayers.Values;

    public void Start(string mapName = "", string gameMode = "", Vector3 initialSpawnPos = default, float initialSpawnYaw = 0f)
    {
        MapName = mapName;
        GameMode = gameMode;
        InitialSpawnPos = initialSpawnPos;
        InitialSpawnYaw = initialSpawnYaw;
        _commands.Clear();
        IsRecording = true;
    }

    /// <summary>
    /// Attaches to a player, capturing its updates automatically every physics tick
    /// and hooking its spawn/death lifecycle events.
    /// Tagged with the player's <see cref="MapPlayer.PlayerId"/> (PeerId).
    /// </summary>
    public void Attach(MapPlayer player)
    {
        if (_attachedPlayers.ContainsKey(player.PlayerId)) return;

        Action<PlayerUpdateCmd> updateCb = update => RecordUpdate(update, player.PlayerId);
        MapPlayer.DiedEventHandler diedCb = () =>
        {
            RecordDeath(player.PlayerId, killerPeerId: -1, gun: -1, isSuicide: true, deathPos: player.Entity.O, impulse: Vector3.Zero, author: player.PlayerId);
        };
        MapPlayer.RespawnedEventHandler respawnedCb = () =>
        {
            RecordSpawn(player.Entity.O, player.Entity.Yaw, lifeSequence: player.Combat.LifeSequence, gun: player.Combat.GunSelect, health: player.Health, armour: player.Combat.Armour, author: player.PlayerId);
        };

        _attachedPlayers[player.PlayerId] = player;
        _callbacks[player.PlayerId] = (updateCb, diedCb, respawnedCb);

        player.OnUpdateCaptured += updateCb;
        player.Died += diedCb;
        player.Respawned += respawnedCb;
    }

    /// <summary>
    /// Attaches multiple players to record their actions concurrently.
    /// </summary>
    public void Attach(IEnumerable<MapPlayer> players)
    {
        foreach (var p in players)
        {
            Attach(p);
        }
    }

    /// <summary>
    /// Attaches all players currently active in the world.
    /// </summary>
    public void AttachWorld(IGameWorld world)
    {
        for (int i = 0; i < world.Players.Count; i++)
        {
            Attach(world.Players[i]);
        }
    }

    /// <summary>
    /// Hooks a <see cref="Commands.CommandSink"/> to record live world geometry edits and entity mutations.
    /// </summary>
    public void AttachSink(Commands.CommandSink sink)
    {
        if (ReferenceEquals(_attachedSink, sink)) return;
        DetachSink();
        _attachedSink = sink;
        sink.CommandApplied += RecordCommand;
    }

    /// <summary>
    /// Detaches the currently hooked command sink.
    /// </summary>
    public void DetachSink()
    {
        if (_attachedSink != null)
        {
            _attachedSink.CommandApplied -= RecordCommand;
            _attachedSink = null;
        }
    }

    /// <summary>
    /// Detaches from a specific player.
    /// </summary>
    public void Detach(MapPlayer player)
    {
        if (_attachedPlayers.Remove(player.PlayerId, out var p))
        {
            if (_callbacks.Remove(player.PlayerId, out var cbs))
            {
                p.OnUpdateCaptured -= cbs.UpdateCb;
                p.Died -= cbs.DiedCb;
                p.Respawned -= cbs.RespawnedCb;
            }
        }
    }

    /// <summary>
    /// Detaches from all currently attached players and sinks.
    /// </summary>
    public void DetachAll()
    {
        foreach (var (id, p) in _attachedPlayers)
        {
            if (_callbacks.TryGetValue(id, out var cbs))
            {
                p.OnUpdateCaptured -= cbs.UpdateCb;
                p.Died -= cbs.DiedCb;
                p.Respawned -= cbs.RespawnedCb;
            }
        }
        _attachedPlayers.Clear();
        _callbacks.Clear();
        DetachSink();
    }

    /// <summary>
    /// Records a player update directly as a <see cref="Command"/>.
    /// </summary>
    public void RecordUpdate(PlayerUpdateCmd update, int author = 0)
    {
        if (!IsRecording) return;
        _commands.Add(Command.CreatePlayerUpdate(update, author));
    }

    /// <summary>
    /// Captures and records the current state of a player as a <see cref="Command"/>.
    /// </summary>
    public void RecordPlayer(MapPlayer player, int? tick = null)
    {
        if (!IsRecording) return;
        _commands.Add(Command.CreatePlayerUpdate(player.CaptureUpdate(tick), player.PlayerId));
    }

    /// <summary>
    /// Records a player spawn / respawn event.
    /// </summary>
    public void RecordSpawn(Vector3 pos, float yaw, int lifeSequence = 0, int gun = 0, int health = 100, int armour = 0, System.Collections.Generic.IList<int>? ammo = null, int author = 0)
    {
        if (!IsRecording) return;
        _commands.Add(Command.CreatePlayerSpawn(pos, yaw, lifeSequence, gun, health, armour, ammo, author));
    }

    /// <summary>
    /// Records a player death or suicide event.
    /// </summary>
    public void RecordDeath(
        int victimPeerId,
        int killerPeerId = -1,
        int gun = -1,
        bool isSuicide = false,
        Vector3 deathPos = default,
        Vector3 impulse = default,
        int author = 0)
    {
        if (!IsRecording) return;
        _commands.Add(Command.CreatePlayerDeath(victimPeerId, killerPeerId, gun, isSuicide, deathPos, impulse, author));
    }

    /// <summary>
    /// Records a teleport event.
    /// </summary>
    public void RecordTeleport(int teleportId, Vector3 destPos, float destYaw, int author = 0)
    {
        if (!IsRecording) return;
        _commands.Add(Command.CreatePlayerTeleport(teleportId, destPos, destYaw, author));
    }

    /// <summary>
    /// Records a jump pad launch event.
    /// </summary>
    public void RecordJumpPad(int padId, Vector3 impulse, int author = 0)
    {
        if (!IsRecording) return;
        _commands.Add(Command.CreatePlayerJumpPad(padId, impulse, author));
    }

    /// <summary>
    /// Records an arbitrary world or game command.
    /// </summary>
    public void RecordCommand(Command command)
    {
        if (!IsRecording) return;
        _commands.Add(command);
    }

    /// <summary>
    /// Stops recording and returns the compiled <see cref="DemoPackage"/>.
    /// </summary>
    public DemoPackage Stop()
    {
        IsRecording = false;
        DetachAll();
        return ToPackage();
    }

    /// <summary>
    /// Compiles current recorded data into a <see cref="DemoPackage"/>.
    /// </summary>
    public DemoPackage ToPackage()
    {
        return new DemoPackage
        {
            FormatVersion = 1,
            MapName = MapName,
            GameMode = GameMode,
            InitialSpawnPos = new Vec3f { X = InitialSpawnPos.X, Y = InitialSpawnPos.Y, Z = InitialSpawnPos.Z },
            InitialSpawnYaw = InitialSpawnYaw,
            Commands = new List<Command>(_commands),
        };
    }

    /// <summary>
    /// Saves the recorded package to disk (*.fbs or *.bin).
    /// </summary>
    public void Save(string path)
    {
        DemoPackage.Save(path, ToPackage());
    }
}
