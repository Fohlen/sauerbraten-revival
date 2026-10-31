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
    private readonly Dictionary<int, Action<PlayerUpdateCmd>> _captureCallbacks = new();

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
    /// Attaches to a player, capturing its updates automatically every physics tick.
    /// Tagged with the player's <see cref="MapPlayer.PlayerId"/> (PeerId).
    /// </summary>
    public void Attach(MapPlayer player)
    {
        if (_attachedPlayers.ContainsKey(player.PlayerId)) return;

        Action<PlayerUpdateCmd> callback = update => RecordUpdate(update, player.PlayerId);
        _attachedPlayers[player.PlayerId] = player;
        _captureCallbacks[player.PlayerId] = callback;
        player.OnUpdateCaptured += callback;
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
    /// Detaches from a specific player.
    /// </summary>
    public void Detach(MapPlayer player)
    {
        if (_attachedPlayers.Remove(player.PlayerId, out var p))
        {
            if (_captureCallbacks.Remove(player.PlayerId, out var cb))
            {
                p.OnUpdateCaptured -= cb;
            }
        }
    }

    /// <summary>
    /// Detaches from all currently attached players.
    /// </summary>
    public void DetachAll()
    {
        foreach (var (id, p) in _attachedPlayers)
        {
            if (_captureCallbacks.TryGetValue(id, out var cb))
            {
                p.OnUpdateCaptured -= cb;
            }
        }
        _attachedPlayers.Clear();
        _captureCallbacks.Clear();
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
    public void RecordSpawn(Vector3 pos, float yaw, int lifeSequence = 0, int gun = 0, int health = 100, int armour = 0, int author = 0)
    {
        if (!IsRecording) return;
        _commands.Add(Command.CreatePlayerSpawn(pos, yaw, lifeSequence, gun, health, armour, author));
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
