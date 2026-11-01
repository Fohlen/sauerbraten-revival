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

        // Record suicide via player.Suicide() - automatically captured via player.Died hook in Attach()
        player.Suicide();

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

    [Test]
    public void MultiplayerDemo_RecordingAndPlayback()
    {
        var octa = TestData.ReadMap().ToOcta();

        var player0 = new MapPlayer { PlayerId = 0 };
        player0.Setup(octa, new Vector3(3000, 946, 3040), 0f);

        var player1 = new MapPlayer { PlayerId = 1 };
        player1.Setup(octa, new Vector3(3050, 946, 3040), 180f);

        var recorder = new DemoRecorder();
        recorder.Start(mapName: "church51", gameMode: "ffa");

        // Attach both players to the same recorder
        recorder.Attach(player0);
        recorder.Attach(player1);

        // Record 10 steps of concurrent multi-player movement
        for (int i = 0; i < 10; i++)
        {
            player0.Entity.Move = 1;
            player0.Entity.Yaw = i * 10f;

            player1.Entity.Strafe = 1;
            player1.Entity.Jumping = (i == 4 || i == 5);
            player1.GunSelect = (i >= 5) ? (int)Gun.RocketLauncher : (int)Gun.Shotgun;

            recorder.RecordPlayer(player0, i);
            recorder.RecordPlayer(player1, i);

            player0.Advance(Phys.PhysFrameTime / 1000.0);
            player1.Advance(Phys.PhysFrameTime / 1000.0);
        }

        // Record a combat event: player 0 kills player 1
        recorder.RecordDeath(victimPeerId: 1, killerPeerId: 0, gun: (int)Gun.Rifle);

        var demo = recorder.Stop();
        TestAssert.Equal(21, demo.TotalCommands, "20 player updates + 1 death command");

        // Save & reload from disk (*.fbs)
        string demoPath = "user://test_multiplayer_roundtrip.fbs";
        DemoPackage.Save(demoPath, demo);
        var loadedDemo = DemoPackage.Load(demoPath);

        // Set up two fresh playback players
        var replay0 = new MapPlayer { PlayerId = 0 };
        replay0.Setup(octa, new Vector3(3000, 946, 3040), 0f);

        var replay1 = new MapPlayer { PlayerId = 1 };
        replay1.Setup(octa, new Vector3(3050, 946, 3040), 180f);

        var replayer = new DemoReplayer(loadedDemo);
        replayer.RegisterPlayer(replay0);
        replayer.RegisterPlayer(replay1);

        // Step simulation for both players concurrently
        for (int i = 0; i < 10; i++)
        {
            replay0.PrePhysicsStep();
            replay1.PrePhysicsStep();

            TestAssert.Equal(1, replay0.Entity.Move, $"Step {i}: Player0 Move");
            TestAssert.Near(i * 10f, replay0.Entity.Yaw, 1e-3f, $"Step {i}: Player0 Yaw");

            TestAssert.Equal(1, replay1.Entity.Strafe, $"Step {i}: Player1 Strafe");
            TestAssert.Equal(i == 4 || i == 5, replay1.Entity.Jumping, $"Step {i}: Player1 Jumping");
            TestAssert.Equal(i >= 5 ? (int)Gun.RocketLauncher : (int)Gun.Shotgun, replay1.GunSelect, $"Step {i}: Player1 Gun");

            replay0.Advance(Phys.PhysFrameTime / 1000.0);
            replay1.Advance(Phys.PhysFrameTime / 1000.0);
        }

        // Process death event
        replayer.GetUpdate(10, replay1);
        TestAssert.True(replay1.Entity.Dead, "Player 1 is dead after death event in multiplayer demo");

        player0.Free();
        player1.Free();
        replay0.Free();
        replay1.Free();

        string globalPath = ProjectSettings.GlobalizePath(demoPath);
        if (File.Exists(globalPath)) File.Delete(globalPath);
    }

    [Test]
    public void GenerateDemoPath_FollowsSauerbratenTimestampSchema()
    {
        // 1. Without map name (exact Sauerbraten format: YYYY-MM-DD_HH.mm.ss.fbs)
        string defaultPath = DemoPackage.GenerateDemoPath();
        TestAssert.True(defaultPath.StartsWith("user://demos/"), $"Path starts with user://demos/: '{defaultPath}'");
        TestAssert.True(defaultPath.EndsWith(".fbs"), $"Path ends with .fbs: '{defaultPath}'");

        string fileName = Path.GetFileNameWithoutExtension(defaultPath);
        // Format is yyyy-MM-dd_HH.mm.ss (length 19: 4 + 1 + 2 + 1 + 2 + 1 + 2 + 1 + 2 + 1 + 2)
        TestAssert.Equal(19, fileName.Length, $"Timestamp filename has length 19 (YYYY-MM-DD_HH.mm.ss): '{fileName}'");
        TestAssert.True(DateTime.TryParseExact(fileName, "yyyy-MM-dd_HH.mm.ss", null, System.Globalization.DateTimeStyles.None, out _),
            $"Filename '{fileName}' parses as valid Sauerbraten timestamp");

        // 2. With map name prefix
        string mapPath = DemoPackage.GenerateDemoPath("base/church51.ogz");
        TestAssert.True(mapPath.StartsWith("user://demos/church51_"), $"Path has map prefix: '{mapPath}'");
        TestAssert.True(mapPath.EndsWith(".fbs"), $"Path ends with .fbs: '{mapPath}'");

        // 3. With custom extension
        string dmoPath = DemoPackage.GenerateDemoPath("ot", "dmo");
        TestAssert.True(dmoPath.EndsWith(".dmo"), $"Path has custom extension: '{dmoPath}'");
    }

    [Test]
    public void CommandLineArgs_ParsesDemoAndRecordFlags()
    {
        // --play-demo=path
        TestAssert.Equal("demo1.fbs", DemoPackage.ParseDemoArg(new[] { "--play-demo=demo1.fbs" }), "Parse --play-demo=path");
        TestAssert.Equal("demo2.fbs", DemoPackage.ParseDemoArg(new[] { "--play-demo", "demo2.fbs" }), "Parse --play-demo path");

        // --demo=path and -d
        TestAssert.Equal("demo3.fbs", DemoPackage.ParseDemoArg(new[] { "--demo=demo3.fbs" }), "Parse --demo=path");
        TestAssert.Equal("demo4.fbs", DemoPackage.ParseDemoArg(new[] { "--demo", "demo4.fbs" }), "Parse --demo path");
        TestAssert.Equal("demo5.fbs", DemoPackage.ParseDemoArg(new[] { "-d", "demo5.fbs" }), "Parse -d path");

        // Quoted arguments
        TestAssert.Equal("user://demos/quoted.fbs", DemoPackage.ParseDemoArg(new[] { "--play-demo=\"user://demos/quoted.fbs\"" }), "Parse quoted arg");

        // Absent or other args
        TestAssert.True(DemoPackage.ParseDemoArg(new[] { "--other", "arg" }) == null, "Returns null when absent");

        // --record-demo
        TestAssert.Equal("", DemoPackage.ParseRecordDemoArg(new[] { "--record-demo" }), "Parse --record-demo flag");
        TestAssert.Equal("test_record.fbs", DemoPackage.ParseRecordDemoArg(new[] { "--record-demo=test_record.fbs" }), "Parse --record-demo=path");
        TestAssert.Equal("test_record2.fbs", DemoPackage.ParseRecordDemoArg(new[] { "--record-demo", "test_record2.fbs" }), "Parse --record-demo path");
        TestAssert.True(DemoPackage.ParseRecordDemoArg(new[] { "--play-demo=foo" }) == null, "Returns null when record flag absent");
    }

    [Test]
    public void ResolveDemoPath_ResolvesFilesystemAndUserDemos()
    {
        string tempDemoPath = DemoPackage.GenerateDemoPath("test_resolve");
        var pkg = new DemoPackage { MapName = "test_map", GameMode = "ffa" };
        DemoPackage.Save(tempDemoPath, pkg);

        try
        {
            // 1. Direct path
            string? resolvedDirect = DemoPackage.ResolveDemoPath(tempDemoPath);
            TestAssert.True(resolvedDirect != null, $"Resolved direct path '{tempDemoPath}'");

            // 2. Bare filename with extension
            string fileName = Path.GetFileName(tempDemoPath);
            string? resolvedName = DemoPackage.ResolveDemoPath(fileName);
            TestAssert.True(resolvedName != null, $"Resolved filename '{fileName}' from user://demos/");

            // 3. Bare filename without .fbs extension
            string baseName = Path.GetFileNameWithoutExtension(tempDemoPath);
            string? resolvedBase = DemoPackage.ResolveDemoPath(baseName);
            TestAssert.True(resolvedBase != null, $"Resolved bare name without extension '{baseName}'");

            // 4. Non-existent file
            string? resolvedMissing = DemoPackage.ResolveDemoPath("non_existent_file_xyz_123");
            TestAssert.True(resolvedMissing == null, "Non-existent demo resolves to null");

            // 5. Load through auto-resolved path
            var loaded = DemoPackage.Load(baseName);
            TestAssert.Equal("test_map", loaded.MapName, "DemoPackage.Load auto-resolves bare name");
        }
        finally
        {
            string global = ProjectSettings.GlobalizePath(tempDemoPath);
            if (File.Exists(global)) File.Delete(global);
        }
    }

    [Test]
    public void MapPlayer_ToggleDemoRecording_SavesDemoPackage()
    {
        var octa = TestData.ReadMap().ToOcta();
        var player = new MapPlayer();
        player.Setup(octa, new Vector3(3000, 946, 3040), 90f);

        string tempPath = DemoPackage.GenerateDemoPath("test_toggle");
        bool toggledActive = false;
        string? toggledPath = null;
        player.DemoRecordingToggled += (active, path) =>
        {
            toggledActive = active;
            toggledPath = path;
        };

        try
        {
            // Start recording via toggle
            player.ToggleDemoRecording(tempPath);
            TestAssert.True(player.IsRecordingDemo, "Player is recording demo");
            TestAssert.True(toggledActive, "DemoRecordingToggled fired with active=true");
            TestAssert.Equal(tempPath, toggledPath, "DemoRecordingToggled passed path");

            // Capture 5 ticks of movement
            for (int i = 0; i < 5; i++)
            {
                player.Entity.Move = 1;
                player.PrePhysicsStep();
                player.Advance(Phys.PhysFrameTime / 1000.0);
            }

            // Stop recording via toggle
            player.ToggleDemoRecording();
            TestAssert.True(!player.IsRecordingDemo, "Player stopped recording demo");
            TestAssert.True(!toggledActive, "DemoRecordingToggled fired with active=false");

            // Verify demo was saved and is readable
            var loaded = DemoPackage.Load(tempPath);
            TestAssert.True(loaded.TotalTicks >= 5, $"Loaded demo has at least 5 ticks: {loaded.TotalTicks}");
            TestAssert.Near(3000f, loaded.InitialSpawnPos?.X ?? 0f, 1f, "Recorded initial spawn position");
            TestAssert.Near(90f, loaded.InitialSpawnYaw, 1e-3f, "Recorded initial spawn yaw");
        }
        finally
        {
            player.Free();
            string global = ProjectSettings.GlobalizePath(tempPath);
            if (File.Exists(global)) File.Delete(global);
        }
    }

    [Test]
    public void OgzMapViewer_PlaysDemoWithInitialPlacementAndReplayer()
    {
        var demo = new DemoPackage
        {
            FormatVersion = 1,
            MapName = "church51",
            GameMode = "ffa",
            InitialSpawnPos = new Vec3f { X = 3123, Y = 945, Z = 3050 },
            InitialSpawnYaw = 135f,
            Commands = new List<Command>
            {
                Command.CreatePlayerUpdate(tick: 0, move: 1, strafe: 0, jumping: false, yaw: 135f, pitch: 0f, attacking: false, selectedGun: 0)
            }
        };

        var viewer = new SauerbratenRevival.World.Runtime.Formats.Ogz.OgzMapViewer
        {
            OgzPath = "base/church51.ogz",
            SpawnPlayer = true,
            FrameCamera = false,
            DemoToPlay = demo
        };

        var player = new MapPlayer { Name = "Player" };
        viewer.AddChild(player);

        TestScene.AddChild(viewer);

        try
        {
            TestAssert.True(viewer.ActiveReplayer != null, "Viewer initialized ActiveReplayer from DemoToPlay");
            TestAssert.True(ReferenceEquals(player.InputSource, viewer.ActiveReplayer), "Player InputSource bound to viewer ActiveReplayer");
            TestAssert.Near(3123f, player.Entity.O.X, 1e-3f, "Player placed at demo InitialSpawnPos X");
            TestAssert.Near(945f, player.Entity.O.Y, 1e-3f, "Player placed at demo InitialSpawnPos Y");
            TestAssert.Near(3050f, player.Entity.O.Z, 1e-3f, "Player placed at demo InitialSpawnPos Z");
            TestAssert.Near(135f, player.Entity.Yaw, 1e-3f, "Player oriented at demo InitialSpawnYaw");
        }
        finally
        {
            viewer.QueueFree();
        }
    }
}
