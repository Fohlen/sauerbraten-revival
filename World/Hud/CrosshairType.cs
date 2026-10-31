namespace SauerbratenRevival.World.Hud;

/// <summary>
/// Identifies the classic crosshair slots (rendergl.cpp <c>crosshairs[MAXCROSSHAIRS]</c>, fpsgame/fps.cpp:1110).
/// Dense 0-based indices matching <see cref="CrosshairCatalog.All"/> and <see cref="Crosshairs.All"/>.
/// </summary>
public enum CrosshairType
{
    None = -1,
    Default = 0,
    Teammate = 1,
    Hit = 2,
}
