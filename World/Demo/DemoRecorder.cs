using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Game.Players;

namespace SauerbratenRevival.World.Demo;

/// <summary>
/// Records player updates, lifecycle events, and world mutations during gameplay or simulation into a <see cref="DemoPackage"/>.
/// Every event is captured as a unified <see cref="Command"/> in the package.
/// </summary>
public class DemoRecorder
{
    private readonly List<Command> _commands = new();
    private MapPlayer? _attachedPlayer;

    public bool IsRecording { get; private set; }
    public string MapName { get; set; } = string.Empty;
    public string GameMode { get; set; } = string.Empty;
    public Vector3 InitialSpawnPos { get; set; }
    public float InitialSpawnYaw { get; set; }

    public IReadOnlyList<Command> Commands => _commands;

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
    /// </summary>
    public void Attach(MapPlayer player)
    {
        if (ReferenceEquals(_attachedPlayer, player)) return;
        Detach();
        _attachedPlayer = player;
        _attachedPlayer.OnUpdateCaptured += RecordPlayerUpdate;
    }

    /// <summary>
    /// Detaches from the currently attached player.
    /// </summary>
    public void Detach()
    {
        if (_attachedPlayer != null)
        {
            _attachedPlayer.OnUpdateCaptured -= RecordPlayerUpdate;
            _attachedPlayer = null;
        }
    }

    private void RecordPlayerUpdate(PlayerUpdateCmd update)
    {
        if (!IsRecording) return;
        _commands.Add(Command.CreatePlayerUpdate(update, _attachedPlayer?.PlayerId ?? 0));
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
        Detach();
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
