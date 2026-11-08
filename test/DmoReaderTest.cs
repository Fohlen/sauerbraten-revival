using System;
using System.IO;
using System.IO.Compression;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.Format;
using SauerbratenRevival.World.Demo.Formats.Dmo;
using SauerbratenRevival.World.Demo;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Players;

namespace SauerbratenRevival.Tests;

public class DmoReaderTest : GoTestBase
{
    public DmoReaderTest(Node testScene) : base(testScene) { }

    [Test]
    public void VecFromYawPitch_MatchesGroundTruth()
    {
        // 0 yaw, 0 pitch, forward (+Y in Sauerbraten)
        var forward = DmoReader.VecFromYawPitch(0f, 0f, 1, 0);
        TestAssert.Near(0f, forward.X, 1e-4f, "Forward X is 0");
        TestAssert.Near(1f, forward.Y, 1e-4f, "Forward Y is 1");
        TestAssert.Near(0f, forward.Z, 1e-4f, "Forward Z is 0");

        // 90 yaw, 0 pitch, forward (-X in Sauerbraten: -sin(90) = -1)
        var left = DmoReader.VecFromYawPitch(90f, 0f, 1, 0);
        TestAssert.Near(-1f, left.X, 1e-4f, "Left X is -1");
        TestAssert.Near(0f, left.Y, 1e-4f, "Left Y is 0");

        // 0 yaw, 0 pitch, strafe right (-X in Sauerbraten)
        var strafeRight = DmoReader.VecFromYawPitch(0f, 0f, 0, -1);
        TestAssert.Near(-1f, strafeRight.X, 1e-4f, "Strafe right X is -1");
    }

    [Test]
    public void SynthesizedDemo_ParsesAndConvertsToDemoPackage()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new BinaryWriter(gz))
        {
            // 1. Header: 16-byte magic, version, protocol
            byte[] magic = new byte[16];
            System.Text.Encoding.ASCII.GetBytes("SAUERBRATEN_DEMO", 0, 16, magic, 0);
            writer.Write(magic);
            writer.Write(1);   // version
            writer.Write(260); // protocol

            // Record 1: millis=0, chan=1, welcome + mapchange + initclient
            using var r1Ms = new MemoryStream();
            using (var r1w = new BinaryWriter(r1Ms))
            {
                // N_WELCOME (2)
                r1w.Write((sbyte)2);

                // N_MAPCHANGE (22): string "duel7", mode 3, hasitems 1
                r1w.Write((sbyte)22);
                WriteString(r1w, "duel7");
                r1w.Write((sbyte)3); // mode 3 (instagib)
                r1w.Write((sbyte)1);

                // N_INITCLIENT (3): cn=0, name="Alice", team="good", model=1
                r1w.Write((sbyte)3);
                r1w.Write((sbyte)0);
                WriteString(r1w, "Alice");
                WriteString(r1w, "good");
                r1w.Write((sbyte)1);

                // N_INITCLIENT (3): cn=1, name="Bob", team="evil", model=2
                r1w.Write((sbyte)3);
                r1w.Write((sbyte)1);
                WriteString(r1w, "Bob");
                WriteString(r1w, "evil");
                r1w.Write((sbyte)2);

                // N_SPAWNSTATE (17): scn=0, lifeseq=1, hp=100, maxhp=100, arm=0, armtype=0, gun=4, ammo (6)
                r1w.Write((sbyte)17);
                r1w.Write((sbyte)0); // scn
                r1w.Write((sbyte)1); // lifeseq
                r1w.Write((sbyte)100);
                r1w.Write((sbyte)100);
                r1w.Write((sbyte)0);
                r1w.Write((sbyte)0);
                r1w.Write((sbyte)4); // gun (rifle)
                for (int i = 0; i < 6; i++) r1w.Write((sbyte)10);
            }
            byte[] r1Bytes = r1Ms.ToArray();
            writer.Write(0);           // gamemillis
            writer.Write(1);           // chan 1
            writer.Write(r1Bytes.Length);
            writer.Write(r1Bytes);

            // Record 2: millis=100, chan=0, N_POS for cn=0
            using var r2Ms = new MemoryStream();
            using (var r2w = new BinaryWriter(r2Ms))
            {
                // N_POS (4)
                r2w.Write((sbyte)4);
                r2w.Write((byte)0); // cn = 0
                // physstate: phys=1 (floor), move=1 (bit 4: 1<<4 = 16), strafe=0
                r2w.Write((byte)(1 | (1 << 4)));
                r2w.Write((byte)0); // flags = 0 (low 16-bit positions)

                // Pos: x=512, y=256, z=128 (fixed point * 16)
                int px = 512 * 16, py = 256 * 16, pz = 128 * 16;
                r2w.Write((ushort)px);
                r2w.Write((ushort)py);
                r2w.Write((ushort)pz);

                // Yaw = 90, Pitch = 0 -> dir = yaw + (pitch+90)*360 = 90 + 90*360 = 32490
                int dir = 90 + 90 * 360;
                r2w.Write((ushort)dir);
                r2w.Write((byte)90); // roll = 0 (clamp(roll,0,180)-90)

                // Vel magnitude = 0, dir = 0
                r2w.Write((byte)0);
                r2w.Write((ushort)0);
            }
            byte[] r2Bytes = r2Ms.ToArray();
            writer.Write(100);
            writer.Write(0); // chan 0
            writer.Write(r2Bytes.Length);
            writer.Write(r2Bytes);

            // Record 3: millis=200, chan=1, N_DIED (victim=1, killer=0, frags=1, tfrags=1)
            using var r3Ms = new MemoryStream();
            using (var r3w = new BinaryWriter(r3Ms))
            {
                r3w.Write((sbyte)11); // N_DIED
                r3w.Write((sbyte)1);  // victim cn
                r3w.Write((sbyte)0);  // killer cn
                r3w.Write((sbyte)1);  // frags
                r3w.Write((sbyte)1);  // teamfrags
            }
            byte[] r3Bytes = r3Ms.ToArray();
            writer.Write(200);
            writer.Write(1);
            writer.Write(r3Bytes.Length);
            writer.Write(r3Bytes);
        }

        byte[] demoBytes = ms.ToArray();
        using var readStream = new MemoryStream(demoBytes);
        var parsed = DmoReader.Read(readStream);

        TestAssert.Equal("SAUERBRATEN_DEMO", parsed.Magic, "Magic matches");
        TestAssert.Equal(1, parsed.Version, "Version matches");
        TestAssert.Equal(260, parsed.Protocol, "Protocol matches");
        TestAssert.Equal("duel7", parsed.MapName, "Map matches");
        TestAssert.Equal(3, parsed.GameMode, "Mode matches");
        TestAssert.Equal("instagib", parsed.GameModeName, "GameModeName is instagib");
        TestAssert.Equal(200, parsed.DurationMillis, "Duration is 200ms");
        TestAssert.Equal(3, parsed.TotalRecords, "3 records");

        TestAssert.Equal(2, parsed.Players.Count, "2 players");
        TestAssert.Equal("Alice", parsed.Players[0].Name, "Player 0 is Alice");
        TestAssert.Equal("Bob", parsed.Players[1].Name, "Player 1 is Bob");
        TestAssert.Equal(1, parsed.Players[0].Frags, "Alice has 1 frag");
        TestAssert.Equal(1, parsed.Players[1].Deaths, "Bob has 1 death");

        TestAssert.Equal(1, parsed.Positions.Count, "1 position");
        var p0 = parsed.Positions[0];
        TestAssert.Equal(0, p0.ClientNum, "CN is 0");
        TestAssert.Near(512f, p0.FeetPos.X, 1e-4f, "X matches");
        TestAssert.Near(256f, p0.FeetPos.Y, 1e-4f, "Y matches");
        TestAssert.Near(128f, p0.FeetPos.Z, 1e-4f, "Feet Z matches");
        TestAssert.Near(142f, p0.Pos.Z, 1e-4f, "Eye Z matches (feet + 14)");
        TestAssert.Near(90f, p0.Yaw, 1e-4f, "Yaw matches");
        TestAssert.Equal(1, p0.Move, "Move is 1 (forward)");

        // Translate to DemoPackage
        var package = DmoReader.ToDemoPackage(parsed);
        TestAssert.Equal("duel7", package.MapName, "Package MapName matches");
        TestAssert.Equal("instagib", package.GameMode, "Package GameMode matches");
        TestAssert.True(package.Commands != null && package.Commands.Count > 0, "Package has commands");

        // Serialize and Deserialize DemoPackage via FlatSharp
        byte[] packageBytes = package.Encode();
        TestAssert.True(packageBytes.Length > 0, "Encoded package bytes");
        var decoded = DemoPackage.Decode(packageBytes);
        TestAssert.Equal("duel7", decoded.MapName, "Decoded map matches");
        TestAssert.Equal(package.Commands.Count, decoded.Commands?.Count ?? 0, "Decoded command count matches");

        // Replay through DemoReplayer
        var replayer = new DemoReplayer(decoded);
        TestAssert.False(replayer.IsFinished, "Replayer starts not finished");
        TestAssert.Equal(package.Commands.Count, replayer.TotalCommands, "Replayer TotalCommands matches");
    }

    [Test]
    public void RealWorldDemo_CannibalVsRexus_MatchesGroundTruth()
    {
        string demoPath = Path.Combine(
            ProjectSettings.GlobalizePath("res://"),
            "test", "golden", "demos", "cannibal_vs_rexus_finals_duel7_game1.dmo");

        if (!File.Exists(demoPath))
        {
            // Guard against a missing golden fixture
            return;
        }

        var dmo = DmoReader.ReadFile(demoPath);

        // Ground-truth assertions matching r6889 dmoprobe
        TestAssert.Equal("SAUERBRATEN_DEMO", dmo.Magic, "Magic matches");
        TestAssert.Equal(1, dmo.Version, "Version is 1");
        TestAssert.Equal(260, dmo.Protocol, "Protocol is 260");
        TestAssert.Equal("duel7", dmo.MapName, "Map is duel7");
        TestAssert.Equal(3, dmo.GameMode, "Mode is 3");
        TestAssert.Equal("instagib", dmo.GameModeName, "GameModeName is instagib");
        TestAssert.Equal(609968, dmo.DurationMillis, "Duration is 609968ms");
        TestAssert.Equal(34481, dmo.TotalRecords, "Records count matches 34481");
        TestAssert.Equal(34419, dmo.Positions.Count, "Positions count matches 34419");
        TestAssert.Equal(471, dmo.Shots.Count, "Shots count matches 471");
        TestAssert.Equal(171, dmo.Deaths.Count, "Deaths count matches 171");

        // Assert player stats
        TestAssert.True(dmo.Players.ContainsKey(4), "Player 4 exists");
        TestAssert.True(dmo.Players.ContainsKey(9), "Player 9 exists");

        var rexus = dmo.Players[4];
        TestAssert.Equal("Rexus|sp4nk|", rexus.Name, "Player 4 is Rexus");
        TestAssert.Equal(85, rexus.Frags, "Rexus frags = 85");
        TestAssert.Equal(86, rexus.Deaths, "Rexus deaths = 86");
        TestAssert.Equal(248, rexus.Shots, "Rexus shots = 248");
        TestAssert.Equal(17304, rexus.PositionCount, "Rexus position count = 17304");

        var cannibal = dmo.Players[9];
        TestAssert.Equal("Cannibal", cannibal.Name, "Player 9 is Cannibal");
        TestAssert.Equal(86, cannibal.Frags, "Cannibal frags = 86");
        TestAssert.Equal(85, cannibal.Deaths, "Cannibal deaths = 85");
        TestAssert.Equal(223, cannibal.Shots, "Cannibal shots = 223");
        TestAssert.Equal(17115, cannibal.PositionCount, "Cannibal position count = 17115");

        // Assert first position of Client 4 (matches dmoprobe sample: millis=165, pos=(508, 420, 506.875), yaw=353)
        var p4_0 = dmo.Positions[0];
        TestAssert.Equal(165, p4_0.Millis, "First position millis = 165");
        TestAssert.Equal(4, p4_0.ClientNum, "First position CN = 4");
        TestAssert.Near(508f, p4_0.Pos.X, 1e-3f, "P4 Pos X = 508");
        TestAssert.Near(420f, p4_0.Pos.Y, 1e-3f, "P4 Pos Y = 420");
        TestAssert.Near(506.875f, p4_0.FeetPos.Z, 1e-3f, "P4 Feet Z = 506.875");
        TestAssert.Near(506.875f + 14.0f, p4_0.Pos.Z, 1e-3f, "P4 Eye Z = 520.875");
        TestAssert.Near(353f, p4_0.Yaw, 1e-3f, "P4 Yaw = 353");
        TestAssert.Near(0f, p4_0.Pitch, 1e-3f, "P4 Pitch = 0");

        // Convert to DemoPackage
        var package = DmoReader.ToDemoPackage(dmo);
        TestAssert.Equal("duel7", package.MapName, "Package map is duel7");
        TestAssert.Equal("instagib", package.GameMode, "Package gamemode is instagib");
        TestAssert.True(package.Commands != null && package.Commands.Count > 10000, "Package contains full expanded command sequence");

        // Ensure all spawn commands have valid map geometry coordinates (NOT 0,0,0!) and ammo
        int spawnCount = 0;
        foreach (var cmd in package.Commands!)
        {
            if (cmd.Op == WorldOp.PlayerSpawn && cmd.PlayerSpawn?.Pos != null)
            {
                spawnCount++;
                TestAssert.True(cmd.PlayerSpawn.Pos.X > 100f, "Spawn X is in map bounds");
                TestAssert.True(cmd.PlayerSpawn.Pos.Y > 100f, "Spawn Y is in map bounds");
                TestAssert.True(cmd.PlayerSpawn.Pos.Z > 100f, "Spawn Z is in map bounds");
                TestAssert.True(cmd.PlayerSpawn.Ammo != null && cmd.PlayerSpawn.Ammo.Count >= 7, "Spawn has ammo list");
                TestAssert.Equal(100, cmd.PlayerSpawn.Ammo![4], "Instagib rifle ammo is 100");
            }
        }
        TestAssert.True(spawnCount > 0, "Spawns exist in commands");

        // Verify peers are populated for multi-player demo
        TestAssert.True(package.Peers != null && package.Peers.Count == 2, "2 peers populated in package");
        TestAssert.Equal(0, package.Peers![0].PeerId, "Peer 0 is mapped primary");
        TestAssert.Equal("Rexus|sp4nk|", package.Peers[0].Name, "Peer 0 name is Rexus");
        TestAssert.Equal(1, package.Peers[1].PeerId, "Peer 1 is opponent");
        TestAssert.Equal("Cannibal", package.Peers[1].Name, "Peer 1 name is Cannibal");

        // Encode to FlatBuffers binary
        byte[] packageBytes = package.Encode();
        TestAssert.True(packageBytes.Length > 0, "FlatBuffers encode produced bytes");

        // Decode back
        var decoded = DemoPackage.Decode(packageBytes);
        TestAssert.Equal(package.Commands.Count, decoded.Commands?.Count ?? 0, "Decoded command count matches");

        // Verify DemoReplayer can initialize with decoded package and dispatches spawn with correct ammo
        var replayer = new DemoReplayer(decoded);
        TestAssert.False(replayer.IsFinished, "Replayer not finished");
        TestAssert.Equal(package.Commands.Count, replayer.TotalCommands, "Replayer command count matches");

        var testPlayer = new MapPlayer { PlayerId = 0 };
        testPlayer.Mode = GameModeCatalog.Get("instagib");
        replayer.Attach(testPlayer);
        var firstUpdate = replayer.GetUpdate(0, testPlayer);
        TestAssert.True(firstUpdate != null, "First update received");
        TestAssert.Equal(100, testPlayer.Combat.Ammo[4], "Player combat ammo rifle is 100");
        TestAssert.Equal(100, testPlayer.Combat.SelectedAmmo, "Player selected ammo is 100");
        testPlayer.Free();
    }

    [Test]
    public void DmoConverter_CommandLineCommands_WorkViaSystemCommandLine()
    {
        string demoPath = Path.Combine(
            ProjectSettings.GlobalizePath("res://"),
            "test", "golden", "demos", "cannibal_vs_rexus_finals_duel7_game1.dmo");

        if (!File.Exists(demoPath))
        {
            return;
        }

        // Test info command via direct subcommand
        using var infoOut = new StringWriter();
        using var infoErr = new StringWriter();
        int infoExit = DmoConverter.Run(new[] { "info", demoPath }, infoOut, infoErr);
        TestAssert.Equal(0, infoExit, "DmoConverter info exit code is 0");
        string infoText = infoOut.ToString();
        TestAssert.True(infoText.Contains("duel7"), "Info output contains map name");
        TestAssert.True(infoText.Contains("Rexus|sp4nk|"), "Info output contains player name");

        // Test info command via nested subcommand: "demo info <file>"
        using var demoInfoOut = new StringWriter();
        using var demoInfoErr = new StringWriter();
        int demoInfoExit = DmoConverter.Run(new[] { "demo", "info", demoPath }, demoInfoOut, demoInfoErr);
        TestAssert.Equal(0, demoInfoExit, "DmoConverter 'demo info' exit code is 0");
        TestAssert.True(demoInfoOut.ToString().Contains("duel7"), "'demo info' output contains map name");

        // Test info --json
        using var jsonOut = new StringWriter();
        using var jsonErr = new StringWriter();
        int jsonExit = DmoConverter.Run(new[] { "info", demoPath, "--json" }, jsonOut, jsonErr);
        TestAssert.Equal(0, jsonExit, "DmoConverter info --json exit code is 0");
        TestAssert.True(jsonOut.ToString().Contains("\"map\": \"duel7\""), "Json info contains map property");

        // Test dump command
        using var dumpOut = new StringWriter();
        using var dumpErr = new StringWriter();
        int dumpExit = DmoConverter.Run(new[] { "dump", demoPath, "--limit", "5" }, dumpOut, dumpErr);
        TestAssert.Equal(0, dumpExit, "DmoConverter dump exit code is 0");
        TestAssert.True(dumpOut.ToString().Contains("Timestamp(ms),Tick,ClientNum"), "Dump output contains CSV header");

        // Test convert command to temp fbs file
        string tempFbs = Path.Combine(Path.GetTempPath(), $"dmo_convert_test_{Guid.NewGuid():N}.fbs");
        try
        {
            using var convertOut = new StringWriter();
            using var convertErr = new StringWriter();
            int convertExit = DmoConverter.Run(new[] { "convert", demoPath, "-o", tempFbs }, convertOut, convertErr);
            TestAssert.Equal(0, convertExit, "DmoConverter convert exit code is 0");
            TestAssert.True(File.Exists(tempFbs), "Converted .fbs file exists");

            // Verify converted file loads via DemoPackage.Load
            var loaded = DemoPackage.Load(tempFbs);
            TestAssert.Equal("duel7", loaded.MapName, "Loaded converted package map matches");
            TestAssert.Equal("instagib", loaded.GameMode, "Loaded converted package mode matches");
        }
        finally
        {
            if (File.Exists(tempFbs)) File.Delete(tempFbs);
        }
    }

    [Test]
    public void DemoPackage_Load_AutomaticallyParsesAndTranslatesDmoFile()
    {
        string demoPath = Path.Combine(
            ProjectSettings.GlobalizePath("res://"),
            "test", "golden", "demos", "cannibal_vs_rexus_finals_duel7_game1.dmo");

        if (!File.Exists(demoPath))
        {
            return;
        }

        // DemoPackage.Load on a raw .dmo file transparently converts it
        var loaded = DemoPackage.Load(demoPath);
        TestAssert.True(loaded != null, "DemoPackage.Load succeeded on raw .dmo");
        TestAssert.Equal("duel7", loaded.MapName, "MapName matches");
        TestAssert.Equal("instagib", loaded.GameMode, "GameMode matches");
        TestAssert.True(loaded.Commands != null && loaded.Commands.Count > 0, "Contains converted commands");
    }

    [Test]
    public void SynthesizedDemo_MultiDeathTimelinePreservation_NoDivergence()
    {
        // Synthesize a demo with a death gap between t=1000ms (tick 200) and t=3000ms (tick 600)
        var parsed = new DmoFile
        {
            MapName = "test_arena",
            GameMode = 3,
            GameModeName = "instagib",
            DurationMillis = 4000
        };

        var p0 = parsed.EnsurePlayer(0);
        p0.Name = "Tester";
        p0.Team = "good";

        // Initial spawn at millis 0
        parsed.Spawns.Add(new DmoSpawn
        {
            Millis = 0,
            ClientNum = 0,
            LifeSequence = 0,
            SelectedGun = 4,
            Health = 100,
            Armour = 0,
            Ammo = new[] { 1, 0, 0, 0, 100, 0, 0 }
        });

        // Movement before death: 0ms -> 1000ms (every 100ms)
        for (int m = 0; m <= 1000; m += 100)
        {
            parsed.Positions.Add(new DmoPosition
            {
                Millis = m,
                ClientNum = 0,
                Pos = new Vec3f { X = 100f + m, Y = 200f, Z = 50f + DmoReader.EyeHeight },
                FeetPos = new Vec3f { X = 100f + m, Y = 200f, Z = 50f },
                Yaw = 90f,
                Move = 1,
                PhysState = 1,
                LifeSequence = 0,
                SelectedGun = 4
            });
        }

        // Death at millis 1000
        parsed.Deaths.Add(new DmoDeath
        {
            Millis = 1000,
            VictimCn = 0,
            ActorCn = 1,
            Frags = 1,
            TeamFrags = 1
        });

        // Respawn 2 seconds later at millis 3000
        parsed.Spawns.Add(new DmoSpawn
        {
            Millis = 3000,
            ClientNum = 0,
            LifeSequence = 1,
            SelectedGun = 4,
            Health = 100,
            Armour = 0,
            Ammo = new[] { 1, 0, 0, 0, 100, 0, 0 }
        });

        // Movement after respawn: 3000ms -> 4000ms
        for (int m = 3000; m <= 4000; m += 100)
        {
            parsed.Positions.Add(new DmoPosition
            {
                Millis = m,
                ClientNum = 0,
                Pos = new Vec3f { X = 500f, Y = 600f + (m - 3000), Z = 100f + DmoReader.EyeHeight },
                FeetPos = new Vec3f { X = 500f, Y = 600f + (m - 3000), Z = 100f },
                Yaw = 0f,
                Move = 1,
                PhysState = 1,
                LifeSequence = 1,
                SelectedGun = 4
            });
        }

        var package = DmoReader.ToDemoPackage(parsed);
        TestAssert.True(package.Commands != null, "Package commands present");

        // Encode to FBS and decode back to verify 1:1 roundtrip
        byte[] fbsBytes = package.Encode();
        var decoded = DemoPackage.Decode(fbsBytes);

        var player = new MapPlayer { PlayerId = 0 };
        var replayer = new DemoReplayer(decoded);
        replayer.Attach(player);

        // Tick 0..199: Player is alive and moving
        for (int tick = 0; tick < 200; tick++)
        {
            player.LastMillis = tick * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
            player.PrePhysicsStep();
            TestAssert.False(player.Entity.Dead, $"Tick {tick}: player is alive before death");
            player.Advance(SauerbratenRevival.World.Physics.Phys.PhysFrameTime / 1000.0);
        }

        // Tick 200 (1000 ms): Death command executes
        player.LastMillis = 200 * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
        player.PrePhysicsStep();
        TestAssert.True(player.Entity.Dead, "Tick 200: player dies at exact death timestamp (1000ms)");

        // Ticks 201..599 (1005ms -> 2995ms): Player MUST remain dead throughout the 2-second death gap!
        // Future spawn command at 3000ms must NOT be dequeued prematurely!
        for (int tick = 201; tick < 600; tick++)
        {
            player.LastMillis = tick * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
            player.PrePhysicsStep();
            TestAssert.True(player.Entity.Dead, $"Tick {tick}: player remains dead during 2000ms death gap");
            TestAssert.False(replayer.IsFinished, $"Tick {tick}: replayer is not finished during death gap");
            player.Advance(SauerbratenRevival.World.Physics.Phys.PhysFrameTime / 1000.0);
        }

        // Tick 600 (3000 ms): Respawn command executes at authentic respawn timestamp!
        player.LastMillis = 600 * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
        player.PrePhysicsStep();
        TestAssert.False(player.Entity.Dead, "Tick 600: player respawns at exact respawn timestamp (3000ms)");
        TestAssert.Near(500f, player.Entity.O.X, 1e-2f, "Respawned at authentic X");
        TestAssert.Near(600f, player.Entity.O.Y, 1e-2f, "Respawned at authentic Y");

        // Ticks 601..800: Player moves post-respawn
        for (int tick = 601; tick <= 800; tick++)
        {
            player.LastMillis = tick * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
            player.PrePhysicsStep();
            TestAssert.False(player.Entity.Dead, $"Tick {tick}: player moves post-respawn");
            player.Advance(SauerbratenRevival.World.Physics.Phys.PhysFrameTime / 1000.0);
        }

        // Final step past tick 800 drains queue
        player.LastMillis = 801 * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
        player.PrePhysicsStep();
        TestAssert.True(replayer.IsFinished, "Replayer finishes cleanly after all ticks complete");

        player.Free();
    }

    [Test]
    public void RealWorldDemo_1To1Translation_AndLongPlaybackSynchronization()
    {
        string demoPath = Path.Combine(
            ProjectSettings.GlobalizePath("res://"),
            "test", "golden", "demos", "cannibal_vs_rexus_finals_duel7_game1.dmo");

        if (!File.Exists(demoPath))
        {
            return;
        }

        var dmo = DmoReader.ReadFile(demoPath);
        var package = DmoReader.ToDemoPackage(dmo);

        // Verify 1:1 translation into FBS representation
        byte[] fbsBytes = package.Encode();
        var decoded = DemoPackage.Decode(fbsBytes);

        // 1. All players, names, and team info are preserved 1:1
        TestAssert.Equal(2, decoded.Peers?.Count ?? 0, "2 peers in decoded FBS");
        TestAssert.Equal("Rexus|sp4nk|", decoded.Peers![0].Name, "Peer 0 name matches");
        TestAssert.Equal("Cannibal", decoded.Peers[1].Name, "Peer 1 name matches");

        // 2. All 171 deaths are translated into PlayerDeath commands
        int deathCommandCount = 0;
        foreach (var cmd in decoded.Commands!)
        {
            if (cmd.Op == WorldOp.PlayerDeath) deathCommandCount++;
        }
        TestAssert.Equal(171, deathCommandCount, "171 PlayerDeath commands in decoded FBS");

        // 3. Play back 15 seconds (3000 ticks) with 2 players concurrently
        var player0 = new MapPlayer { PlayerId = 0 };
        var player1 = new MapPlayer { PlayerId = 1 };

        var replayer = new DemoReplayer(decoded);
        replayer.RegisterPlayer(player0);
        replayer.RegisterPlayer(player1);

        for (int tick = 0; tick < 3000; tick++)
        {
            player0.LastMillis = tick * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
            player1.LastMillis = tick * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;

            player0.PrePhysicsStep();
            player1.PrePhysicsStep();

            player0.Advance(SauerbratenRevival.World.Physics.Phys.PhysFrameTime / 1000.0);
            player1.Advance(SauerbratenRevival.World.Physics.Phys.PhysFrameTime / 1000.0);
        }

        // After 15 seconds, assert that both players' timelines have NOT drifted
        // (At 15s = 15000ms = tick 3000, next update is for tick 3000)
        player0.LastMillis = 3000 * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
        player1.LastMillis = 3000 * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
        var p0Update = replayer.GetUpdate(3000, player0);
        var p1Update = replayer.GetUpdate(3000, player1);

        TestAssert.True(p0Update != null, "Player 0 has active update at 15s without timeline desync");
        TestAssert.True(p1Update != null, "Player 1 has active update at 15s without timeline desync");
        TestAssert.Near(3000, p0Update!.Tick, 2, "Player 0 update tick is synchronized with 15s wall clock");
        TestAssert.Near(3000, p1Update!.Tick, 2, "Player 1 update tick is synchronized with 15s wall clock");

        player0.Free();
        player1.Free();
    }

    [Test]
    public void SynthesizedDemo_DamageEvents_ParsedAndDispatchedToPlayerHealth()
    {
        var dmo = new DmoFile
        {
            MapName = "duel7",
            GameMode = 0,
            GameModeName = "ffa",
            DurationMillis = 2000
        };

        var p0 = dmo.EnsurePlayer(0);
        p0.Name = "Alice";
        var p1 = dmo.EnsurePlayer(1);
        p1.Name = "Bob";

        // Initial spawns at t=0
        dmo.Spawns.Add(new DmoSpawn
        {
            Millis = 0,
            ClientNum = 0,
            LifeSequence = 0,
            SelectedGun = 4,
            Health = 100,
            Armour = 100
        });
        dmo.Spawns.Add(new DmoSpawn
        {
            Millis = 0,
            ClientNum = 1,
            LifeSequence = 0,
            SelectedGun = 4,
            Health = 100,
            Armour = 100
        });

        // Positions for player 1 from 0 to 1000ms
        for (int m = 0; m <= 1000; m += 100)
        {
            dmo.Positions.Add(new DmoPosition
            {
                Millis = m,
                ClientNum = 1,
                Pos = new Vec3f { X = 500f, Y = 600f, Z = 114f },
                FeetPos = new Vec3f { X = 500f, Y = 600f, Z = 100f },
                Yaw = 0f,
                Move = 1,
                PhysState = 1,
                LifeSequence = 0,
                SelectedGun = 4
            });
        }

        // Damage event at t=300ms: Bob takes 35 damage -> hp=65, armour=50
        dmo.DamageEvents.Add(new DmoDamage
        {
            Millis = 300,
            TargetCn = 1,
            ActorCn = 0,
            Damage = 35,
            Armour = 50,
            Health = 65
        });

        // Damage event at t=600ms: Bob takes 40 damage -> hp=25, armour=20
        dmo.DamageEvents.Add(new DmoDamage
        {
            Millis = 600,
            TargetCn = 1,
            ActorCn = 0,
            Damage = 40,
            Armour = 20,
            Health = 25
        });

        // Death event at t=900ms: Bob dies
        dmo.DamageEvents.Add(new DmoDamage
        {
            Millis = 900,
            TargetCn = 1,
            ActorCn = 0,
            Damage = 50,
            Armour = 0,
            Health = 0
        });
        dmo.Deaths.Add(new DmoDeath
        {
            Millis = 900,
            VictimCn = 1,
            ActorCn = 0,
            Frags = 1
        });

        var package = DmoReader.ToDemoPackage(dmo);
        TestAssert.True(package.Commands != null, "Commands present");

        int dmgCount = 0;
        foreach (var cmd in package.Commands!)
        {
            if (cmd.Op == WorldOp.PlayerDamage) dmgCount++;
        }
        TestAssert.Equal(3, dmgCount, "3 PlayerDamage commands generated from DamageEvents");

        // Replay through DemoReplayer
        byte[] fbs = package.Encode();
        var decoded = DemoPackage.Decode(fbs);

        int bobPeer = decoded.Peers != null && decoded.Peers.Count > 0 ? decoded.Peers[0].PeerId : 0;
        var playerBob = new MapPlayer { PlayerId = bobPeer };
        var replayer = new DemoReplayer(decoded);
        replayer.Attach(playerBob);

        // Advance to 200ms (tick 40): health is still 100
        for (int tick = 0; tick < 40; tick++)
        {
            playerBob.LastMillis = tick * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
            playerBob.PrePhysicsStep();
            TestAssert.Equal(100, playerBob.Health, $"Tick {tick}: health is 100 before first damage");
            playerBob.Advance(SauerbratenRevival.World.Physics.Phys.PhysFrameTime / 1000.0);
        }

        // Advance to 400ms (tick 80): damage at 300ms (tick 60) has been applied!
        for (int tick = 40; tick < 80; tick++)
        {
            playerBob.LastMillis = tick * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
            playerBob.PrePhysicsStep();
            if (tick >= 60)
            {
                TestAssert.Equal(65, playerBob.Health, $"Tick {tick}: health updated to 65 after first damage at 300ms");
                TestAssert.Equal(50, playerBob.Combat.Armour, $"Tick {tick}: armour updated to 50");
            }
            playerBob.Advance(SauerbratenRevival.World.Physics.Phys.PhysFrameTime / 1000.0);
        }

        // Advance to 700ms (tick 140): damage at 600ms (tick 120) has dropped health to 25!
        for (int tick = 80; tick < 140; tick++)
        {
            playerBob.LastMillis = tick * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
            playerBob.PrePhysicsStep();
            if (tick >= 120)
            {
                TestAssert.Equal(25, playerBob.Health, $"Tick {tick}: health dropped to 25 after second damage at 600ms");
                TestAssert.Equal(20, playerBob.Combat.Armour, $"Tick {tick}: armour dropped to 20");
            }
            playerBob.Advance(SauerbratenRevival.World.Physics.Phys.PhysFrameTime / 1000.0);
        }

        // Advance past 900ms (tick 180): death occurs
        for (int tick = 140; tick <= 185; tick++)
        {
            playerBob.LastMillis = tick * SauerbratenRevival.World.Physics.Phys.PhysFrameTime;
            playerBob.PrePhysicsStep();
            if (tick >= 180)
            {
                TestAssert.True(playerBob.Entity.Dead, $"Tick {tick}: player died at 900ms");
                TestAssert.Equal(0, playerBob.Health, $"Tick {tick}: health is 0 after death");
            }
            playerBob.Advance(SauerbratenRevival.World.Physics.Phys.PhysFrameTime / 1000.0);
        }

        playerBob.Free();
    }

    private static void WriteString(BinaryWriter writer, string s)
    {
        foreach (char c in s)
        {
            writer.Write((sbyte)c);
        }
        writer.Write((sbyte)0);
    }
}
