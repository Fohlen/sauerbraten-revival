using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Runtime;

namespace SauerbratenRevival.World.Demo;

/// <summary>
/// Replays a unified stream of <see cref="Command"/>s from a <see cref="DemoPackage"/>
/// across one or multiple players concurrently.
/// Implements <see cref="IPlayerInputSource"/> to feed inputs directly into <see cref="MapPlayer.PrePhysicsStep"/>.
/// </summary>
public class DemoReplayer : IPlayerInputSource
{
    private readonly Dictionary<int, MapPlayer> _players = new();
    private readonly Dictionary<int, Queue<Command>> _peerQueues = new();
    private readonly Queue<Command> _globalQueue = new();
    private CommandSink? _commandSink;

    public DemoPackage Demo { get; }
    public int TotalCommands => Demo.Commands?.Count ?? 0;

    /// <summary>Fired when playback reaches the end of all recorded commands across all players.</summary>
    public event Action? PlaybackFinished;

    public bool IsFinished
    {
        get
        {
            if (_globalQueue.Count > 0) return false;
            foreach (var (_, q) in _peerQueues)
            {
                if (q.Count > 0) return false;
            }
            return true;
        }
    }

    public DemoReplayer(DemoPackage demo, CommandSink? commandSink = null)
    {
        Demo = demo;
        _commandSink = commandSink;
        Reset();
    }

    /// <summary>Loads and initializes a replayer from a demo file on disk (*.fbs or *.bin).</summary>
    public static DemoReplayer FromFile(string path, CommandSink? commandSink = null) =>
        new(DemoPackage.Load(path), commandSink);

    /// <summary>
    /// Registers a player to receive its recorded updates by <see cref="MapPlayer.PlayerId"/>.
    /// </summary>
    public void RegisterPlayer(MapPlayer player)
    {
        _players[player.PlayerId] = player;
        player.InputSource = this;
    }

    /// <summary>
    /// Attaches as the input source for all active players in a world.
    /// </summary>
    public void AttachWorld(IGameWorld world)
    {
        for (int i = 0; i < world.Players.Count; i++)
        {
            RegisterPlayer(world.Players[i]);
        }
    }

    /// <summary>
    /// Attaches a single player (shortcut for RegisterPlayer).
    /// </summary>
    public void Attach(MapPlayer player) => RegisterPlayer(player);

    /// <summary>
    /// Detaches a specific player.
    /// </summary>
    public void Detach(MapPlayer player)
    {
        if (_players.Remove(player.PlayerId, out var p))
        {
            if (ReferenceEquals(p.InputSource, this))
            {
                p.InputSource = null;
            }
        }
    }

    /// <summary>
    /// Detaches all registered players.
    /// </summary>
    public void DetachAll()
    {
        foreach (var (_, p) in _players)
        {
            if (ReferenceEquals(p.InputSource, this))
            {
                p.InputSource = null;
            }
        }
        _players.Clear();
    }

    /// <summary>Resets playback position to the beginning for all players.</summary>
    public void Reset()
    {
        _peerQueues.Clear();
        _globalQueue.Clear();

        if (Demo.Commands == null) return;

        for (int i = 0; i < Demo.Commands.Count; i++)
        {
            var cmd = Demo.Commands[i];

            // Direct player commands to their target peer queue
            int targetPeer = cmd.PeerId;
            if (cmd.Op == WorldOp.PlayerDeath && cmd.PlayerDeath != null)
            {
                targetPeer = cmd.PlayerDeath.VictimPeerId;
            }

            if (IsPlayerOp(cmd.Op))
            {
                if (!_peerQueues.TryGetValue(targetPeer, out var q))
                {
                    q = new Queue<Command>();
                    _peerQueues[targetPeer] = q;
                }
                q.Enqueue(cmd);
            }
            else
            {
                _globalQueue.Enqueue(cmd);
            }
        }
    }

    private static bool IsPlayerOp(WorldOp op) => op switch
    {
        WorldOp.PlayerUpdate => true,
        WorldOp.PlayerSpawn => true,
        WorldOp.PlayerDeath => true,
        WorldOp.PlayerTeleport => true,
        WorldOp.PlayerJumpPad => true,
        _ => false
    };

    /// <summary>
    /// Fetches the next player update for the specific player.
    /// Dispatches any interleaved lifecycle commands (spawns, deaths, teleports) before returning the update.
    /// </summary>
    public PlayerUpdateCmd? GetUpdate(int tick, MapPlayer player)
    {
        // Drain any pending global/world commands
        DrainGlobalQueue();

        int peerId = player.PlayerId;
        if (!_peerQueues.TryGetValue(peerId, out var q))
        {
            CheckFinished();
            return null;
        }

        while (q.Count > 0)
        {
            var cmd = q.Dequeue();

            if (cmd.Op == WorldOp.PlayerUpdate && cmd.PlayerUpdate != null)
            {
                CheckFinished();
                return cmd.PlayerUpdate;
            }

            // Dispatch lifecycle commands (spawn, death, teleport, jump pad)
            DispatchCommand(cmd, player);
        }

        CheckFinished();
        return null;
    }

    private void DrainGlobalQueue()
    {
        while (_globalQueue.Count > 0)
        {
            var cmd = _globalQueue.Dequeue();
            _commandSink?.Apply(cmd);
        }
    }

    private void CheckFinished()
    {
        if (IsFinished)
        {
            PlaybackFinished?.Invoke();
        }
    }

    /// <summary>
    /// Dispatches an individual command to player or world state.
    /// </summary>
    public void DispatchCommand(Command cmd, MapPlayer? defaultPlayer = null)
    {
        int peerId = cmd.PeerId;
        if (cmd.Op == WorldOp.PlayerDeath && cmd.PlayerDeath != null)
        {
            peerId = cmd.PlayerDeath.VictimPeerId;
        }

        _players.TryGetValue(peerId, out var targetPlayer);
        targetPlayer ??= defaultPlayer;

        switch (cmd.Op)
        {
            case WorldOp.PlayerSpawn:
                {
                    var spawn = cmd.PlayerSpawn;
                    if (spawn != null && targetPlayer != null)
                    {
                        if (spawn.Pos != null)
                        {
                            targetPlayer.Entity.O = new Vector3(spawn.Pos.X, spawn.Pos.Y, spawn.Pos.Z);
                            targetPlayer.Entity.Yaw = spawn.Yaw;
                        }
                        targetPlayer.Combat.Respawn();
                        targetPlayer.Combat.LifeSequence = spawn.LifeSequence;
                        if (spawn.Health > 0) targetPlayer.Health = spawn.Health;
                        if (spawn.Armour > 0) targetPlayer.Combat.Armour = spawn.Armour;
                        if (spawn.SelectedGun >= 0) targetPlayer.GunSelect = spawn.SelectedGun;
                        targetPlayer.Entity.State = Physics.PhysEnt.CsAlive;
                        targetPlayer.SyncTransform();
                    }
                    break;
                }
            case WorldOp.PlayerDeath:
                {
                    var death = cmd.PlayerDeath;
                    if (death != null && targetPlayer != null)
                    {
                        targetPlayer.Die();
                        if (death.Impulse != null)
                        {
                            targetPlayer.ApplyHitPush(new Vector3(death.Impulse.X, death.Impulse.Y, death.Impulse.Z));
                        }
                    }
                    break;
                }
            case WorldOp.PlayerTeleport:
                {
                    var tele = cmd.PlayerTeleport;
                    if (tele != null && targetPlayer != null && tele.DestPos != null)
                    {
                        targetPlayer.Entity.O = new Vector3(tele.DestPos.X, tele.DestPos.Y, tele.DestPos.Z);
                        targetPlayer.Entity.Yaw = tele.DestYaw;
                        targetPlayer.SyncTransform();
                    }
                    break;
                }
            case WorldOp.PlayerJumpPad:
                {
                    var pad = cmd.PlayerJumpPad;
                    if (pad != null && targetPlayer != null && pad.Impulse != null)
                    {
                        targetPlayer.Entity.Vel += new Vector3(pad.Impulse.X, pad.Impulse.Y, pad.Impulse.Z);
                    }
                    break;
                }
            default:
                {
                    _commandSink?.Apply(cmd);
                    break;
                }
        }
    }

    /// <summary>
    /// Spawns or places a player at the demo's recorded initial position and orientation.
    /// </summary>
    public void ApplyInitialPlacement(MapPlayer player)
    {
        if (Demo.InitialSpawnPos != null)
        {
            player.Entity.O = new Vector3(Demo.InitialSpawnPos.X, Demo.InitialSpawnPos.Y, Demo.InitialSpawnPos.Z);
            player.Entity.Yaw = Demo.InitialSpawnYaw;
            player.SyncTransform();
        }
    }
}
