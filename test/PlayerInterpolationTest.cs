using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Network.Interpolation;

namespace SauerbratenRevival.Tests;

public class PlayerInterpolationTest : GoTestBase
{
    public PlayerInterpolationTest(Node testScene) : base(testScene) { }

    [Test]
    public void ShortestAngleDelta_CalculatesShortestArcAcrossWraparound()
    {
        // 350 deg turning to 10 deg (clockwise across 0/360) -> delta = -20
        TestAssert.Near(-20f, PlayerInterpolation.ShortestAngleDelta(350f, 10f), 1e-4f, "350 -> 10 delta is -20");

        // 10 deg turning to 350 deg (counter-clockwise across 0/360) -> delta = +20
        TestAssert.Near(20f, PlayerInterpolation.ShortestAngleDelta(10f, 350f), 1e-4f, "10 -> 350 delta is +20");

        // 5 deg turning to 355 deg -> delta = +10
        TestAssert.Near(10f, PlayerInterpolation.ShortestAngleDelta(5f, 355f), 1e-4f, "5 -> 355 delta is +10");

        // 355 deg turning to 5 deg -> delta = -10
        TestAssert.Near(-10f, PlayerInterpolation.ShortestAngleDelta(355f, 5f), 1e-4f, "355 -> 5 delta is -10");

        // Standard within-quadrant deltas
        TestAssert.Near(15f, PlayerInterpolation.ShortestAngleDelta(45f, 30f), 1e-4f, "45 -> 30 delta is +15");
        TestAssert.Near(-15f, PlayerInterpolation.ShortestAngleDelta(30f, 45f), 1e-4f, "30 -> 45 delta is -15");
    }

    [Test]
    public void NormalizeYaw_NormalizesIntoZeroTo360Range()
    {
        TestAssert.Near(350f, PlayerInterpolation.NormalizeYaw(-10f), 1e-4f, "-10 wraps to 350");
        TestAssert.Near(2.7333f, PlayerInterpolation.NormalizeYaw(362.7333f), 1e-3f, "362.7333 wraps to 2.7333");
        TestAssert.Near(0f, PlayerInterpolation.NormalizeYaw(0f), 1e-4f, "0 remains 0");
        TestAssert.Near(180f, PlayerInterpolation.NormalizeYaw(180f), 1e-4f, "180 remains 180");
        TestAssert.Near(0f, PlayerInterpolation.NormalizeYaw(720f), 1e-4f, "720 wraps to 0");
    }

    [Test]
    public void InitialSpawn_SmoothMillisMinusOne_SnapsWithoutSmoothing()
    {
        var interp = new PlayerInterpolation();
        TestAssert.Equal(-1, interp.SmoothMillis, "Initial SmoothMillis is -1");

        Vector3 pos = Vector3.Zero;
        float yaw = 0f, pitch = 0f, roll = 0f;

        // First packet at t = 1000
        interp.OnRemotePosition(
            ref pos, ref yaw, ref pitch, ref roll,
            packetPos: new Vector3(100f, 200f, 300f),
            packetYaw: 45f,
            packetPitch: 10f,
            packetRoll: 0f,
            lastMillis: 1000);

        // Ground-truth assertion matching tools/predictprobe initial_spawn:
        // smoothmillis becomes 0, pos snaps to packet pos, no delta offset
        TestAssert.Equal(0, interp.SmoothMillis, "SmoothMillis becomes 0 on initial spawn");
        TestAssert.Near(100f, pos.X, 1e-4f, "Spawn X is 100");
        TestAssert.Near(200f, pos.Y, 1e-4f, "Spawn Y is 200");
        TestAssert.Near(300f, pos.Z, 1e-4f, "Spawn Z is 300");
        TestAssert.Near(45f, yaw, 1e-4f, "Spawn Yaw is 45");
        TestAssert.Near(10f, pitch, 1e-4f, "Spawn Pitch is 10");
        TestAssert.Equal(Vector3.Zero, interp.DeltaPos, "Initial DeltaPos is zero");
        TestAssert.Near(0f, interp.DeltaYaw, 1e-4f, "Initial DeltaYaw is zero");
    }

    [Test]
    public void SecondPacket_MatchesPredictProbeGroundTruthEvaluations()
    {
        var interp = new PlayerInterpolation();

        Vector3 pos = Vector3.Zero;
        float yaw = 0f, pitch = 0f, roll = 0f;

        // Packet 1 establishes baseline at t = 967 ms
        interp.OnRemotePosition(
            ref pos, ref yaw, ref pitch, ref roll,
            packetPos: new Vector3(100f, 200f, 300f),
            packetYaw: 350f,
            packetPitch: -10f,
            packetRoll: 0f,
            lastMillis: 967);

        // Packet 2 at t = 1000 ms: moved to (105, 202, 301), yaw turned past 0 to 10, pitch to 5
        interp.OnRemotePosition(
            ref pos, ref yaw, ref pitch, ref roll,
            packetPos: new Vector3(105f, 202f, 301f),
            packetYaw: 10f,
            packetPitch: 5f,
            packetRoll: 0f,
            lastMillis: 1000);

        // Verify ground-truth values from predictprobe:
        TestAssert.Equal(1000, interp.SmoothMillis, "SmoothMillis is 1000");
        TestAssert.Near(-5f, interp.DeltaPos.X, 1e-4f, "DeltaPos X is -5");
        TestAssert.Near(-2f, interp.DeltaPos.Y, 1e-4f, "DeltaPos Y is -2");
        TestAssert.Near(-1f, interp.DeltaPos.Z, 1e-4f, "DeltaPos Z is -1");
        TestAssert.Near(-20f, interp.DeltaYaw, 1e-4f, "DeltaYaw is -20 (shortest arc)");
        TestAssert.Near(-15f, interp.DeltaPitch, 1e-4f, "DeltaPitch is -15");
        TestAssert.Near(105f, interp.NewPos.X, 1e-4f, "NewPos X is 105");
        TestAssert.Near(202f, interp.NewPos.Y, 1e-4f, "NewPos Y is 202");
        TestAssert.Near(301f, interp.NewPos.Z, 1e-4f, "NewPos Z is 301");
        TestAssert.Near(10f, interp.NewYaw, 1e-4f, "NewYaw is 10");

        // Verify exact evaluation over time window matching predictprobe evaluations:
        // t=1000: k=1.0, pos=(100, 200, 300), yaw=350, pitch=-10
        Vector3 curPos = pos;
        float curYaw = yaw, curPitch = pitch, curRoll = roll;
        interp.Predict(ref curPos, ref curYaw, ref curPitch, ref curRoll, lastMillis: 1000);
        TestAssert.Near(100f, curPos.X, 1e-3f, "t=1000 Pos X is 100");
        TestAssert.Near(200f, curPos.Y, 1e-3f, "t=1000 Pos Y is 200");
        TestAssert.Near(300f, curPos.Z, 1e-3f, "t=1000 Pos Z is 300");
        TestAssert.Near(350f, curYaw, 1e-3f, "t=1000 Yaw is 350 (zero visual pop)");
        TestAssert.Near(-10f, curPitch, 1e-3f, "t=1000 Pitch is -10");

        // t=1015: k=0.8, pos=(101, 200.4, 300.2), yaw=354, pitch=-7
        interp.Predict(ref curPos, ref curYaw, ref curPitch, ref curRoll, lastMillis: 1015);
        TestAssert.Near(101f, curPos.X, 1e-3f, "t=1015 Pos X is 101");
        TestAssert.Near(200.4f, curPos.Y, 1e-3f, "t=1015 Pos Y is 200.4");
        TestAssert.Near(300.2f, curPos.Z, 1e-3f, "t=1015 Pos Z is 300.2");
        TestAssert.Near(354f, curYaw, 1e-3f, "t=1015 Yaw is 354");
        TestAssert.Near(-7f, curPitch, 1e-3f, "t=1015 Pitch is -7");

        // t=1030: k=0.6, pos=(102, 200.8, 300.4), yaw=358, pitch=-4
        interp.Predict(ref curPos, ref curYaw, ref curPitch, ref curRoll, lastMillis: 1030);
        TestAssert.Near(102f, curPos.X, 1e-3f, "t=1030 Pos X is 102");
        TestAssert.Near(200.8f, curPos.Y, 1e-3f, "t=1030 Pos Y is 200.8");
        TestAssert.Near(300.4f, curPos.Z, 1e-3f, "t=1030 Pos Z is 300.4");
        TestAssert.Near(358f, curYaw, 1e-3f, "t=1030 Yaw is 358");
        TestAssert.Near(-4f, curPitch, 1e-3f, "t=1030 Pitch is -4");

        // t=1045: k=0.4, pos=(103, 201.2, 300.6), yaw=2, pitch=-1
        interp.Predict(ref curPos, ref curYaw, ref curPitch, ref curRoll, lastMillis: 1045);
        TestAssert.Near(103f, curPos.X, 1e-3f, "t=1045 Pos X is 103");
        TestAssert.Near(201.2f, curPos.Y, 1e-3f, "t=1045 Pos Y is 201.2");
        TestAssert.Near(300.6f, curPos.Z, 1e-3f, "t=1045 Pos Z is 300.6");
        TestAssert.Near(2f, curYaw, 1e-3f, "t=1045 Yaw crossed 0 to 2");
        TestAssert.Near(-1f, curPitch, 1e-3f, "t=1045 Pitch is -1");

        // t=1060: k=0.2, pos=(104, 201.6, 300.8), yaw=6, pitch=2
        interp.Predict(ref curPos, ref curYaw, ref curPitch, ref curRoll, lastMillis: 1060);
        TestAssert.Near(104f, curPos.X, 1e-3f, "t=1060 Pos X is 104");
        TestAssert.Near(201.6f, curPos.Y, 1e-3f, "t=1060 Pos Y is 201.6");
        TestAssert.Near(300.8f, curPos.Z, 1e-3f, "t=1060 Pos Z is 300.8");
        TestAssert.Near(6f, curYaw, 1e-3f, "t=1060 Yaw is 6");
        TestAssert.Near(2f, curPitch, 1e-3f, "t=1060 Pitch is 2");

        // t=1075: k=0.0, pos=(105, 202, 301), yaw=10, pitch=5 (completed smoothmove)
        interp.Predict(ref curPos, ref curYaw, ref curPitch, ref curRoll, lastMillis: 1075);
        TestAssert.Near(105f, curPos.X, 1e-3f, "t=1075 Pos X is 105");
        TestAssert.Near(202f, curPos.Y, 1e-3f, "t=1075 Pos Y is 202");
        TestAssert.Near(301f, curPos.Z, 1e-3f, "t=1075 Pos Z is 301");
        TestAssert.Near(10f, curYaw, 1e-3f, "t=1075 Yaw is 10");
        TestAssert.Near(5f, curPitch, 1e-3f, "t=1075 Pitch is 5");

        // t=1090: past window, remains at authoritative target
        interp.Predict(ref curPos, ref curYaw, ref curPitch, ref curRoll, lastMillis: 1090);
        TestAssert.Near(105f, curPos.X, 1e-3f, "t=1090 Pos X is 105");
        TestAssert.Near(10f, curYaw, 1e-3f, "t=1090 Yaw is 10");
    }

    [Test]
    public void TeleportOrLeapExceedingSmoothDist_BypassesSmoothing()
    {
        var interp = new PlayerInterpolation();

        Vector3 pos = Vector3.Zero;
        float yaw = 0f, pitch = 0f, roll = 0f;

        // Baseline at t = 500 ms
        interp.OnRemotePosition(
            ref pos, ref yaw, ref pitch, ref roll,
            packetPos: new Vector3(100f, 100f, 100f),
            packetYaw: 90f, packetPitch: 0f, packetRoll: 0f,
            lastMillis: 500);

        // Teleport packet at t = 533 ms: distance = 50 >= 32
        interp.OnRemotePosition(
            ref pos, ref yaw, ref pitch, ref roll,
            packetPos: new Vector3(150f, 100f, 100f),
            packetYaw: 270f, packetPitch: 0f, packetRoll: 0f,
            lastMillis: 533);

        // Ground-truth assertion matching tools/predictprobe teleport_exceeding_smoothdist:
        TestAssert.Equal(0, interp.SmoothMillis, "SmoothMillis becomes 0 on teleport exceeding smoothdist");
        TestAssert.Near(150f, pos.X, 1e-4f, "Pos X snaps immediately to 150");
        TestAssert.Near(270f, yaw, 1e-4f, "Yaw snaps immediately to 270");
        TestAssert.Equal(Vector3.Zero, interp.DeltaPos, "DeltaPos is zero");
        TestAssert.Near(0f, interp.DeltaYaw, 1e-4f, "DeltaYaw is zero");
    }

    [Test]
    public void CounterClockwiseYawWrap_MatchesPredictProbeScenario4()
    {
        var interp = new PlayerInterpolation();

        Vector3 pos = Vector3.Zero;
        float yaw = 0f, pitch = 0f, roll = 0f;

        interp.OnRemotePosition(
            ref pos, ref yaw, ref pitch, ref roll,
            packetPos: new Vector3(10f, 10f, 10f),
            packetYaw: 5f, packetPitch: 0f, packetRoll: 0f,
            lastMillis: 100);

        interp.OnRemotePosition(
            ref pos, ref yaw, ref pitch, ref roll,
            packetPos: new Vector3(12f, 10f, 10f),
            packetYaw: 355f, packetPitch: 0f, packetRoll: 0f,
            lastMillis: 133);

        TestAssert.Near(10f, interp.DeltaYaw, 1e-4f, "DeltaYaw is +10");
        TestAssert.Near(355f, interp.NewYaw, 1e-4f, "NewYaw is 355");

        // Predict at t = 150 ms
        interp.Predict(ref pos, ref yaw, ref pitch, ref roll, lastMillis: 150);
        TestAssert.Near(2.7333f, yaw, 1e-3f, "Predicted yaw at 150ms is 2.7333 deg");
    }

    [Test]
    public void DeadReckoningCallback_AdvancesNewPosWhileDecayingResidual()
    {
        var interp = new PlayerInterpolation();

        Vector3 pos = Vector3.Zero;
        float yaw = 0f, pitch = 0f, roll = 0f;

        interp.OnRemotePosition(
            ref pos, ref yaw, ref pitch, ref roll,
            packetPos: new Vector3(100f, 100f, 0f),
            packetYaw: 0f, packetPitch: 0f, packetRoll: 0f,
            lastMillis: 1000);

        interp.OnRemotePosition(
            ref pos, ref yaw, ref pitch, ref roll,
            packetPos: new Vector3(104f, 100f, 0f),
            packetYaw: 0f, packetPitch: 0f, packetRoll: 0f,
            lastMillis: 1033);

        // At t = 1040 (7 ms after second packet):
        // k = 1.0 - (1040 - 1033) / 75 = 1.0 - 7/75 = 68/75 ~ 0.9067
        // Suppose dead reckoning moves player forward along X by 1 unit:
        interp.Predict(ref pos, ref yaw, ref pitch, ref roll, lastMillis: 1040, deadReckon: () =>
        {
            pos.X += 1.0f; // Simulated physics step advancing base coordinate
        });

        // NewPos was 104; dead reckoning moved it to 105.
        // DeltaPos is 100 - 104 = -4.
        // Residual added is -4 * (68/75) = -3.6267.
        // Final pos.X = 105 - 3.6267 = 101.3733.
        TestAssert.Near(105f, interp.NewPos.X, 1e-4f, "NewPos advanced by dead reckoning to 105");
        TestAssert.Near(101.3733f, pos.X, 1e-3f, "Visual pos includes dead reckoning + decayed residual");
    }
}
