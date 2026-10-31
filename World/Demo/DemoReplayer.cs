using System;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Game.Players;

namespace SauerbratenRevival.World.Demo;

/// <summary>
/// Replays a unified stream of <see cref="Command"/>s from a <see cref="DemoPackage"/> onto a player and world.
/// Implements <see cref="IPlayerInputSource"/> to feed inputs directly into <see cref="MapPlayer.PrePhysicsStep"/>.
/// </summary>
public class DemoReplayer : IPlayerInputSource
{
    private MapPlayer? _attachedPlayer;
    private CommandSink? _commandSink;

    public DemoPackage Demo { get; }
    public int CurrentIndex { get; private set; }
    public bool IsFinished => Demo.Commands == null || CurrentIndex >= Demo.Commands.Count;
    public int TotalCommands => Demo.Commands?.Count ?? 0;

    /// <summary>Fired when playback reaches the end of the recorded command stream.</summary>
    public event Action? PlaybackFinished;

    public DemoReplayer(DemoPackage demo, CommandSink? commandSink = null)
    {
        Demo = demo;
        _commandSink = commandSink;
        CurrentIndex = 0;
    }

    /// <summary>Loads and initializes a replayer from a demo file on disk (*.fbs or *.bin).</summary>
    public static DemoReplayer FromFile(string path, CommandSink? commandSink = null) =>
        new(DemoPackage.Load(path), commandSink);

    /// <summary>
    /// Attaches this replayer as the input source for the given player.
    /// </summary>
    public void Attach(MapPlayer player)
    {
        if (ReferenceEquals(_attachedPlayer, player)) return;
        Detach();
        _attachedPlayer = player;
        _attachedPlayer.InputSource = this;
    }

    /// <summary>
    /// Detaches this replayer from the player.
    /// </summary>
    public void Detach()
    {
        if (_attachedPlayer != null)
        {
            if (ReferenceEquals(_attachedPlayer.InputSource, this))
            {
                _attachedPlayer.InputSource = null;
            }
            _attachedPlayer = null;
        }
    }

    /// <summary>Resets the playback position to the start.</summary>
    public void Reset()
    {
        CurrentIndex = 0;
    }

    /// <summary>
    /// Fetches the next player update. Called per 5 ms physics step by <see cref="MapPlayer.PrePhysicsStep"/>.
    /// Dispatches any interleaved lifecycle or world commands encountered before the next update.
    /// </summary>
    public PlayerUpdateCmd? GetUpdate(int tick, MapPlayer player)
    {
        if (Demo.Commands == null || Demo.Commands.Count == 0) return null;

        while (CurrentIndex < Demo.Commands.Count)
        {
            var cmd = Demo.Commands[CurrentIndex++];
            if (cmd.Op == WorldOp.PlayerUpdate && cmd.PlayerUpdate != null)
            {
                if (CurrentIndex >= Demo.Commands.Count)
                {
                    PlaybackFinished?.Invoke();
                }
                return cmd.PlayerUpdate;
            }

            // Dispatch non-update commands (spawn, death, teleport, jump pad, or world edits)
            DispatchCommand(cmd, player);

            if (CurrentIndex >= Demo.Commands.Count)
            {
                PlaybackFinished?.Invoke();
            }
        }

        return null;
    }

    /// <summary>
    /// Dispatches an individual command to player or world state.
    /// </summary>
    public void DispatchCommand(Command cmd, MapPlayer? player = null)
    {
        var targetPlayer = player ?? _attachedPlayer;

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
                    // Pass octree / entity / environment commands to the live CommandSink if present
                    _commandSink?.Apply(cmd);
                    break;
                }
        }
    }

    /// <summary>
    /// Explicitly steps the player with the next command from the demo.
    /// </summary>
    public bool Step(MapPlayer player, int? tick = null)
    {
        int t = tick ?? (player.LastMillis / Physics.Phys.PhysFrameTime);
        var update = GetUpdate(t, player);
        if (update != null)
        {
            player.ApplyUpdate(update);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Spawns or places the player at the demo's recorded initial position and orientation.
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
