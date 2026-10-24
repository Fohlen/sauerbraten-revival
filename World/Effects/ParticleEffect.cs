namespace SauerbratenRevival.World.Effects;

/// <summary>
/// <c>ET_PARTICLES</c> effect ids (<c>renderparticles.cpp makeparticles</c>,
/// switch on <c>attr1</c>) and which need a non-billboard renderer.
/// </summary>
public static class ParticleEffect
{
    public static string Name(int effect) => effect switch
    {
        0 => "fire+smoke",
        1 => "steam vent",
        2 => "water fountain",
        3 => "fireball",
        4 => "tape",
        5 => "meter",
        6 => "metervs",
        7 => "lightning",
        9 => "steam",
        10 => "water",
        11 => "flame",
        12 => "smoke plume",
        13 => "snow",
        32 => "lens flare",
        33 => "lens flare (sparkle)",
        34 => "lens flare (sun)",
        35 => "lens flare (sparklesun)",
        _ => "unknown",
    };

    /// <summary>
    /// Effects that a plain billboard particle cannot represent (tape/streak,
    /// fireball/explosion, meter, lightning, lens flare) and that need a custom
    /// renderer/shader. Marked as deferred TODOs rather than ported now.
    /// </summary>
    public static bool NeedsCustomRenderer(int effect) =>
        effect is 3 or 4 or 5 or 6 or 7 || effect is >= 32 and <= 35;
}
