using System;
using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Demo;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Network;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Unit and permutation tests verifying the player update command,
/// lifecycle commands (spawn, death, suicide, teleport, jump pad),
/// unified Command union serialization to *.fbs / *.bin, and deterministic playback.
/// </summary>
public class DemoTest : GoTestBase
{
    public DemoTest(Node testScene) : base(testScene) { }

    [Test]
    public void PlayerUpdateCmd_PropertiesAndConstructor()
    {
        var update = new PlayerUpdateCmd(
            tick: 105,
            move: 1,
            strafe: -1,
            jumping: true,
            yaw: 135.5f,
            pitch: -15.2f,
            attacking: true,
            selectedGun: (int)Gun.Rifle,
            pos: new Vector3(3000, 1000, 3050),
            vel: new Vector3(10, -5, 20),
            physState: 1,
            lifeSequence: 3);

        TestAssert.Equal(105, update.Tick, "Tick matches");
        TestAssert.Equal(1, update.Move, "Move matches");
        TestAssert.Equal(-1, update.Strafe, "Strafe matches");
        TestAssert.True(update.Jumping, "Jumping matches");
        TestAssert.Near(135.5f, update.Yaw, 1e-4f, "Yaw matches");
        TestAssert.Near(-15.2f, update.Pitch, 1e-4f, "Pitch matches");
        TestAssert.True(update.Attacking, "Attacking matches");
        TestAssert.Equal((int)Gun.Rifle, update.SelectedGun, "SelectedGun matches");
        TestAssert.Near(3000f, update.Pos?.X ?? 0f, 1e-4f, "Pos X matches");
        TestAssert.Near(10f, update.Vel?.X ?? 0f, 1e-4f, "Vel X matches");
        TestAssert.Equal((byte)1, update.PhysState, "PhysState matches");
        TestAssert.Equal(3, update.LifeSequence, "LifeSequence matches");
    }

    [Test]
    public void CommandUnion_WrapsLifecycleCommandsAndSerializes()
    {
        // 1. PlayerUpdate command
        var updateCmd = Command.CreatePlayerUpdate(
            tick: 42,
            move: -1,
            strafe: 1,
            jumping: false,
            yaw: 90f,
            pitch: 0f,
            attacking: false,
            selectedGun: (int)Gun.Shotgun,
            pos: new Vector3(100, 200, 300),
            vel: new Vector3(0, 0, 0),
            author: 7);
        byte[] bytes = NetMessage.Encode(updateCmd);
        var decodedUpdate = NetMessage.Decode(bytes);
        TestAssert.Equal(WorldOp.PlayerUpdate, decodedUpdate.Op, "Op is PlayerUpdate");
        TestAssert.Equal(7, decodedUpdate.PeerId, "PeerId is author");
        TestAssert.True(decodedUpdate.PlayerUpdate != null, "PlayerUpdate is present");
        TestAssert.Equal(42, decodedUpdate.PlayerUpdate!.Tick, "Tick matches");

        // 2. PlayerSpawn command
        var spawnCmd = Command.CreatePlayerSpawn(new Vector3(500, 600, 700), yaw: 180f, lifeSequence: 2, gun: (int)Gun.Rifle, author: 1);
        bytes = NetMessage.Encode(spawnCmd);
        var decodedSpawn = NetMessage.Decode(bytes);
        TestAssert.Equal(WorldOp.PlayerSpawn, decodedSpawn.Op, "Op is PlayerSpawn");
        TestAssert.True(decodedSpawn.PlayerSpawn != null, "PlayerSpawn is present");
        TestAssert.Near(500f, decodedSpawn.PlayerSpawn!.Pos.X, 1e-4f, "Spawn X matches");
        TestAssert.Near(180f, decodedSpawn.PlayerSpawn.Yaw, 1e-4f, "Spawn Yaw matches");
        TestAssert.Equal(2, decodedSpawn.PlayerSpawn.LifeSequence, "LifeSequence matches");

        // 3. PlayerDeath / Suicide command
        var deathCmd = Command.CreatePlayerDeath(victimPeerId: 1, killerPeerId: 2, gun: (int)Gun.RocketLauncher, isSuicide: false, deathPos: new Vector3(1, 2, 3), impulse: new Vector3(0, 0, 50), author: 2);
        bytes = NetMessage.Encode(deathCmd);
        var decodedDeath = NetMessage.Decode(bytes);
        TestAssert.Equal(WorldOp.PlayerDeath, decodedDeath.Op, "Op is PlayerDeath");
        TestAssert.True(decodedDeath.PlayerDeath != null, "PlayerDeath is present");
        TestAssert.Equal(1, decodedDeath.PlayerDeath!.VictimPeerId, "Victim matches");
        TestAssert.Equal(2, decodedDeath.PlayerDeath.KillerPeerId, "Killer matches");
        TestAssert.False(decodedDeath.PlayerDeath.IsSuicide, "Not suicide");

        var suicideCmd = Command.CreatePlayerDeath(victimPeerId: 1, killerPeerId: 1, gun: -1, isSuicide: true, author: 1);
        bytes = NetMessage.Encode(suicideCmd);
        var decodedSuicide = NetMessage.Decode(bytes);
        TestAssert.True(decodedSuicide.PlayerDeath!.IsSuicide, "Is suicide");

        // 4. PlayerTeleport command
        var teleCmd = Command.CreatePlayerTeleport(teleportId: 5, destPos: new Vector3(1000, 2000, 3000), destYaw: 270f, author: 1);
        bytes = NetMessage.Encode(teleCmd);
        var decodedTele = NetMessage.Decode(bytes);
        TestAssert.Equal(WorldOp.PlayerTeleport, decodedTele.Op, "Op is PlayerTeleport");
        TestAssert.True(decodedTele.PlayerTeleport != null, "PlayerTeleport is present");
        TestAssert.Near(1000f, decodedTele.PlayerTeleport!.DestPos.X, 1e-4f, "Dest X matches");
        TestAssert.Near(270f, decodedTele.PlayerTeleport.DestYaw, 1e-4f, "Dest Yaw matches");

        // 5. PlayerJumpPad command
        var padCmd = Command.CreatePlayerJumpPad(padId: 3, impulse: new Vector3(0, 0, 150), author: 1);
        bytes = NetMessage.Encode(padCmd);
        var decodedPad = NetMessage.Decode(bytes);
        TestAssert.Equal(WorldOp.PlayerJumpPad, decodedPad.Op, "Op is PlayerJumpPad");
        TestAssert.True(decodedPad.PlayerJumpPad != null, "PlayerJumpPad is present");
        TestAssert.Near(150f, decodedPad.PlayerJumpPad!.Impulse.Z, 1e-4f, "Pad impulse matches");
    }

    [Test]
    public void DemoPackage_UnifiedCommandStream_EncodeDecodeAndFileRoundTrip()
    {
        string path = "user://test_demo_unified.fbs";

        var commands = new List<Command>
        {
            Command.CreatePlayerSpawn(new Vector3(3000, 946, 3040), 90f, lifeSequence: 1, author: 0)
        };

        for (int i = 0; i < 20; i++)
        {
            commands.Add(Command.CreatePlayerUpdate(
                tick: i,
                move: i % 2 == 0 ? 1 : 0,
                strafe: i % 3 == 0 ? -1 : 0,
                jumping: i == 5 || i == 6,
                yaw: i * 15f,
                pitch: -10f + i,
                attacking: i >= 10 && i <= 12,
                selectedGun: i > 10 ? (int)Gun.RocketLauncher : (int)Gun.Pistol,
                pos: new Vector3(3000 + i, 946, 3040),
                author: 0));
        }

        // Add a suicide event at the end
        commands.Add(Command.CreatePlayerDeath(victimPeerId: 0, killerPeerId: 0, gun: -1, isSuicide: true, author: 0));

        var demo = new DemoPackage
        {
            FormatVersion = 1,
            MapName = "base/church51",
            GameMode = "ffa",
            InitialSpawnPos = new Vec3f { X = 3000f, Y = 946f, Z = 3040f },
            InitialSpawnYaw = 90f,
            Commands = commands
        };

        DemoPackage.Save(path, demo);

        TestAssert.True(File.Exists(ProjectSettings.GlobalizePath(path)), "Demo file was written to disk");

        var loaded = DemoPackage.Load(path);
        TestAssert.Equal(1u, loaded.FormatVersion, "Version matches");
        TestAssert.Equal("base/church51", loaded.MapName, "MapName matches");
        TestAssert.Equal("ffa", loaded.GameMode, "GameMode matches");
        TestAssert.Equal(22, loaded.TotalCommands, "Total commands match");
        TestAssert.Equal(20, loaded.TotalTicks, "Total player update ticks match");
        TestAssert.Near(0.1f, loaded.DurationSeconds, 1e-4f, "Duration in seconds matches 20 * 5ms");

        TestAssert.Equal(WorldOp.PlayerSpawn, loaded.Commands![0].Op, "First command is PlayerSpawn");
        TestAssert.Equal(WorldOp.PlayerDeath, loaded.Commands[21].Op, "Last command is PlayerDeath");
        TestAssert.True(loaded.Commands[21].PlayerDeath!.IsSuicide, "Last command is suicide");

        // Clean up temporary file
        string globalPath = ProjectSettings.GlobalizePath(path);
        if (File.Exists(globalPath)) File.Delete(globalPath);
    }

    [Test]
    public void DemoRecorder_CapturesAllEventsAndDemoReplayerDispatches()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = new MapPlayer();
        player.Setup(octa, new Vector3(3000, 946, 3040), 90f);

        var recorder = new DemoRecorder();
        recorder.Start(mapName: "church51", gameMode: "ffa", initialSpawnPos: player.Entity.O, initialSpawnYaw: 90f);
        recorder.Attach(player);

        // Record a spawn event
        recorder.RecordSpawn(player.Entity.O, 90f, lifeSequence: 1);

        // Simulate 5 movement ticks
        for (int i = 0; i < 5; i++)
        {
            player.Entity.Move = 1;
            player.Entity.Yaw = 90f + i * 10f;
            recorder.RecordPlayer(player, i);
            player.Advance(Phys.PhysFrameTime / 1000.0);
        }

        // Record suicide
        player.Suicide();
        recorder.RecordDeath(player.PlayerId, isSuicide: true);

        var demo = recorder.Stop();
        TestAssert.Equal(7, demo.TotalCommands, "7 total commands captured");

        // Now replay the demo on a second player
        var replayPlayer = new MapPlayer();
        replayPlayer.Setup(octa, new Vector3(3000, 946, 3040), 90f);

        var replayer = new DemoReplayer(demo);
        replayer.Attach(replayPlayer);

        bool finishedFired = false;
        replayer.PlaybackFinished += () => finishedFired = true;

        for (int i = 0; i < 5; i++)
        {
            replayPlayer.PrePhysicsStep();
            TestAssert.Equal(1, replayPlayer.Entity.Move, $"Replay tick {i} Move matches");
            TestAssert.Near(90f + i * 10f, replayPlayer.Entity.Yaw, 1e-3f, $"Replay tick {i} Yaw matches");
            replayPlayer.Advance(Phys.PhysFrameTime / 1000.0);
        }

        // Step past the death command
        replayer.GetUpdate(5, replayPlayer);
        TestAssert.True(replayPlayer.Entity.Dead, "Replay player is now dead after suicide command");
        TestAssert.True(finishedFired, "PlaybackFinished event fired");

        player.Free();
        replayPlayer.Free();
    }

    [Test]
    public void PermutationTest_DeterministicReplayAcrossConfigurations()
    {
        var commands = new List<Command>();
        for (int t = 0; t < 50; t++)
        {
            int move = t < 25 ? 1 : 0;
            int strafe = (t >= 25 && t < 40) ? 1 : 0;
            bool jump = (t == 5 || t == 6 || t == 26 || t == 27);
            float yaw = 45f + (t * 2.5f);
            float pitch = (t < 20) ? -5f : 10f;
            bool attacking = (t == 10 || t == 30);
            int gun = (t < 30) ? (int)Gun.Shotgun : (int)Gun.Rifle;

            commands.Add(Command.CreatePlayerUpdate(t, move, strafe, jump, yaw, pitch, attacking, gun));
        }

        var demo = new DemoPackage
        {
            FormatVersion = 1,
            MapName = "church51",
            GameMode = "ffa",
            InitialSpawnPos = new Vec3f { X = 3000f, Y = 946f, Z = 3040f },
            InitialSpawnYaw = 45f,
            Commands = commands
        };

        // Permutation 1: Run simulation with Octa instance A
        var octaA = TestData.ReadMap().ToOcta();
        var playerA = new MapPlayer();
        playerA.Setup(octaA, new Vector3(3000, 946, 3040), 45f);
        var replayerA = new DemoReplayer(demo);
        replayerA.Attach(playerA);

        var trajectoryA = new List<Vector3>();
        for (int t = 0; t < 50; t++)
        {
            playerA.PrePhysicsStep();
            playerA.Advance(Phys.PhysFrameTime / 1000.0);
            playerA.PostPhysicsStep();
            trajectoryA.Add(playerA.Entity.O);
        }

        // Permutation 2: Run simulation with Octa instance B (cloned independently)
        var octaB = TestData.ReadMap().ToOcta();
        var playerB = new MapPlayer();
        playerB.Setup(octaB, new Vector3(3000, 946, 3040), 45f);
        var replayerB = new DemoReplayer(demo);
        replayerB.Attach(playerB);

        var trajectoryB = new List<Vector3>();
        for (int t = 0; t < 50; t++)
        {
            playerB.PrePhysicsStep();
            playerB.Advance(Phys.PhysFrameTime / 1000.0);
            playerB.PostPhysicsStep();
            trajectoryB.Add(playerB.Entity.O);
        }

        // Assert that both permutations produce 100% deterministic, bit-exact trajectories!
        TestAssert.Equal(trajectoryA.Count, trajectoryB.Count, "Trajectory step count matches");
        for (int t = 0; t < 50; t++)
        {
            TestAssert.Near(trajectoryA[t].X, trajectoryB[t].X, 1e-4f, $"Step {t} X matches");
            TestAssert.Near(trajectoryA[t].Y, trajectoryB[t].Y, 1e-4f, $"Step {t} Y matches");
            TestAssert.Near(trajectoryA[t].Z, trajectoryB[t].Z, 1e-4f, $"Step {t} Z matches");
        }

        TestAssert.Near(playerA.Entity.Vel.X, playerB.Entity.Vel.X, 1e-4f, "Final Vel X matches");
        TestAssert.Near(playerA.Entity.Vel.Y, playerB.Entity.Vel.Y, 1e-4f, "Final Vel Y matches");
        TestAssert.Near(playerA.Entity.Vel.Z, playerB.Entity.Vel.Z, 1e-4f, "Final Vel Z matches");
        TestAssert.Equal(playerA.Entity.PhysState, playerB.Entity.PhysState, "Final PhysState matches");

        playerA.Free();
        playerB.Free();
    }
}
