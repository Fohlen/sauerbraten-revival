namespace SauerbratenRevival.World.Game;

/// <summary>
/// Engine armour types (fpsgame/game.h:69 <c>A_BLUE/A_GREEN/A_YELLOW</c>). The
/// value is the absorption percentage step used by <c>dodamage</c>:
/// <c>ad = damage*(armourtype+1)*25/100</c>, so blue takes 0 extra off, green 20%,
/// yellow 40%.
/// </summary>
public enum ArmourType
{
    Blue = 0,
    Green,
    Yellow,
}
