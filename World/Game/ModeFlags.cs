using System;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// Engine gamemode flags (fpsgame/game.h <c>M_*</c>, r6889). The values are the
/// engine's bit positions, so they must stay exactly as written — bit 11 is
/// deliberately unused in the original. The <c>m_*</c> predicates in game.h are
/// all built on these flags (e.g. <c>m_insta = m_check(gamemode, M_INSTA)</c>).
///
/// A <b>bitfield, not an index</b>: modes are data (see <see cref="GameModeInfo"/>),
/// and behaviour is keyed on these flags so a mod can define a new mode by choosing
/// a flag combination rather than by claiming an engine slot.
/// </summary>
[Flags]
public enum ModeFlags
{
    None = 0,
    Team = 1 << 0,
    NoItems = 1 << 1,
    NoAmmo = 1 << 2,
    Insta = 1 << 3,
    Efficiency = 1 << 4,
    Tactics = 1 << 5,
    Capture = 1 << 6,
    Regen = 1 << 7,
    Ctf = 1 << 8,
    Protect = 1 << 9,
    Hold = 1 << 10,
    Edit = 1 << 12,
    Demo = 1 << 13,
    Local = 1 << 14,
    Lobby = 1 << 15,
    DmSp = 1 << 16,
    ClassicSp = 1 << 17,
    SlowMo = 1 << 18,
    Collect = 1 << 19,
}
