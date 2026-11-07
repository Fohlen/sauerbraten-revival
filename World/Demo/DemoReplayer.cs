using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Runtime;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Demo;

/// <summary>
/// Replays a unified stream of <see cref="Command"/>s from a <see cref="DemoPackage"/>
/// across one or multiple players concurrently.
/// Implements <see cref="IPlayerInputSource"/> to feed inputs directly into <see cref="MapPlayer.PrePhysicsStep"/>.
/// </summary>
[GlobalClass]
public partial class DemoReplayer : RefCounted, IPlayerInputSource
{
    [Signal]
    public delegate void PlaybackFinishedEventHandler();

    private readonly Dictionary<int, MapPlayer> _players = new();
    private readonly Dictionary<int, Queue<Command>> _peerQueues = new();
    private readonly Queue<Command> _globalQueue = new();
    private CommandSink? _commandSink;
    private bool _playbackFinishedFired;

    public DemoPackage Demo { get; }
    public int TotalCommands => Demo.Commands?.Count ?? 0;

    /// <summary>
    /// Smoothing window in milliseconds to apply to registered players (default 0 for raw playback, 75 for smooth demo playback).
    /// </summary>
    public int SmoothMove { get; set; } = 0;

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
        if (SmoothMove > 0)
        {
            player.SmoothMove = SmoothMove;
        }
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
        _playbackFinishedFired = false;
        _peerQueues.Clear();
        _globalQueue.Clear();

        foreach (var (_, p) in _players)
        {
            p.Frags = 0;
            p.Deaths = 0;
        }

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
    /// Fetches the next player update for the specific player at the specified simulation tick.
    /// Dispatches any interleaved lifecycle commands (spawns, deaths, teleports) whose timestamp has arrived.
    /// Returns null if the next update belongs to a future tick (e.g. death gap or lag pause).
    /// </summary>
    public PlayerUpdateCmd? GetUpdate(int tick, MapPlayer player)
    {
        uint currentMillis = (uint)(tick * SauerbratenRevival.World.Physics.Phys.PhysFrameTime);

        // Drain any pending global/world commands up to current time
        DrainGlobalQueue(currentMillis);

        int peerId = player.PlayerId;
        if (!_peerQueues.TryGetValue(peerId, out var q))
        {
            CheckFinished();
            return null;
        }

        while (q.Count > 0)
        {
            var cmd = q.Peek();

            if (cmd.Op == WorldOp.PlayerUpdate && cmd.PlayerUpdate != null)
            {
                // If update belongs to a future tick or timestamp, hold playback until simulation catches up
                if (cmd.PlayerUpdate.Tick > tick || (cmd.Timestamp > 0 && cmd.Timestamp > currentMillis))
                {
                    return null;
                }

                q.Dequeue();
                CheckFinished();
                return cmd.PlayerUpdate;
            }

            // For lifecycle commands (spawn, death, teleport, jump pad), wait until their timestamp has arrived
            if (cmd.Timestamp > 0 && cmd.Timestamp > currentMillis)
            {
                return null;
            }

            q.Dequeue();
            DispatchCommand(cmd, player);
        }

        CheckFinished();
        return null;
    }

    private void DrainGlobalQueue(uint currentMillis)
    {
        while (_globalQueue.Count > 0)
        {
            var cmd = _globalQueue.Peek();
            if (cmd.Timestamp > 0 && cmd.Timestamp > currentMillis)
            {
                break;
            }
            _globalQueue.Dequeue();
            _commandSink?.Apply(cmd);
        }
    }

    private void CheckFinished()
    {
        if (!_playbackFinishedFired && IsFinished)
        {
            _playbackFinishedFired = true;
            EmitSignal(SignalName.PlaybackFinished);
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
        if (targetPlayer == null && defaultPlayer != null && (defaultPlayer.PlayerId == peerId || _players.Count == 0))
        {
            targetPlayer = defaultPlayer;
        }

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
                        if (targetPlayer.Mode != null)
                        {
                            targetPlayer.Combat.Spawn(targetPlayer.Mode);
                        }
                        else
                        {
                            targetPlayer.Combat.Respawn();
                        }
                        targetPlayer.Combat.LifeSequence = spawn.LifeSequence;
                        if (spawn.Health > 0) targetPlayer.Health = spawn.Health;
                        if (spawn.Armour > 0) targetPlayer.Combat.Armour = spawn.Armour;
                        if (spawn.Ammo != null && spawn.Ammo.Count > 0)
                        {
                            for (int i = 0; i < spawn.Ammo.Count && i < targetPlayer.Combat.Ammo.Length; i++)
                            {
                                targetPlayer.Combat.Ammo[i] = spawn.Ammo[i];
                            }
                        }
                        if (spawn.SelectedGun >= 0)
                        {
                            targetPlayer.GunSelect = spawn.SelectedGun;
                            targetPlayer.SelectWeapon((Gun)spawn.SelectedGun);
                        }
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

                        // Update frags matching engine client.cpp:1560-1576 (N_DIED) and server.cpp:2278
                        if (death.IsSuicide || death.KillerPeerId < 0 || death.KillerPeerId == targetPlayer.PlayerId)
                        {
                            targetPlayer.Frags--;
                        }
                        else
                        {
                            _players.TryGetValue(death.KillerPeerId, out var killerPlayer);
                            killerPlayer ??= (defaultPlayer != null && defaultPlayer.PlayerId == death.KillerPeerId) ? defaultPlayer : null;
                            if (killerPlayer != null)
                            {
                                bool isTeam = targetPlayer.Mode != null &&
                                              targetPlayer.Mode.Has(Game.ModeFlags.Team) &&
                                              killerPlayer.Team == targetPlayer.Team;
                                if (isTeam)
                                {
                                    killerPlayer.Frags--;
                                }
                                else
                                {
                                    killerPlayer.Frags++;
                                }
                            }
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
        if (player.Mode != null)
        {
            player.Combat.Spawn(player.Mode);
        }
    }
}
