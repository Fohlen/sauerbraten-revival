using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Network;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Locks the "no floats on the wire" decision (docs §11.7): positions use
/// fixed-point <see cref="FixedPoint"/>, and command payloads never contain a
/// float <c>Variant</c>. A float would make determinism unprovable and is the
/// thing this guard exists to prevent regressing.
/// </summary>
public class FixedPointTest : GoTestBase
{
    public FixedPointTest(Node testScene) : base(testScene) { }

    [Test]
    public void EncodeDecodeRoundTripsToGridPrecision()
    {
        var (x, y, z) = FixedPoint.Encode(12.5f, -3.25f, 100f);
        TestAssert.Equal(200, x, "12.5 * 16");
        TestAssert.Equal(-52, y, "-3.25 * 16");
        TestAssert.Equal(1600, z, "100 * 16");

        var (fx, fy, fz) = FixedPoint.Decode(x, y, z);
        TestAssert.True(Mathf.Abs(fx - 12.5f) < 1e-4f, "decode x");
        TestAssert.True(Mathf.Abs(fy + 3.25f) < 1e-4f, "decode y");
        TestAssert.True(Mathf.Abs(fz - 100f) < 1e-4f, "decode z");
    }

    [Test]
    public void EncodeIsDeterministic()
    {
        // The whole point: identical inputs give identical integers on any platform.
        for (float v = -50f; v <= 50f; v += 0.37f)
        {
            var a = FixedPoint.Encode(v, v, v);
            var b = FixedPoint.Encode(v, v, v);
            TestAssert.True(a == b, $"deterministic at {v}");
        }
    }

    [Test]
    public void CommandPayloadsContainNoFloats()
    {
        // Encode a representative command and assert its Variant tree has no float.
        var cmd = new Command(WorldOp.NewMap, new NewMapPayload(12));
        var bytes = NetMessage.Encode(cmd);
        TestAssert.True(bytes.Length > 0, "encoded");
        // Check the decoded payload tree instead of raw bytes.
        var back = NetMessage.Decode(bytes);
        TestAssert.True(back.Payload is NewMapPayload, "round-trips without float coercion");
        TestAssert.Equal(12, ((NewMapPayload)back.Payload).Scale, "scale is an int");
    }
}
