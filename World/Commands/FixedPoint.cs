namespace SauerbratenRevival.World.Commands;

/// <summary>
/// Fixed-point encoding for values that must travel over the wire.
///
/// **The wire carries no floats** (docs/editor-and-commands.md §11.7): positions
/// are integers at a fixed scale, attributes are <c>i16</c>, cubes/selection are
/// already integers. This is Cube2's own scheme — entity positions were sent as
/// <c>(int)(o.x * DMF)</c> with <c>DMF = 16</c> — and it makes determinism trivial
/// (no float rounding to diverge peers) and pass-through <c>Remip</c> safe.
///
/// Entity commands are not implemented yet; this exists so the first one uses the
/// right scale instead of reaching for <c>float</c>.
/// </summary>
public static class FixedPoint
{
    /// <summary>fpsgame/game.h:19 <c>DMF</c> — world positions per unit (1/16 world unit precision).</summary>
    public const int DMF = 16;

    /// <summary>Encode a world position to fixed-point integers (round-to-nearest, as the engine).</summary>
    public static (int X, int Y, int Z) Encode(float x, float y, float z) =>
        ((int)System.MathF.Round(x * DMF),
         (int)System.MathF.Round(y * DMF),
         (int)System.MathF.Round(z * DMF));

    /// <summary>Decode fixed-point integers back to world units.</summary>
    public static (float X, float Y, float Z) Decode(int x, int y, int z) =>
        (x / (float)DMF, y / (float)DMF, z / (float)DMF);
}
