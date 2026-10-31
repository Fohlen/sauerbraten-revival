namespace SauerbratenRevival.World.Game;

/// <summary>
/// Identifies the classic engine map lists (data/lists.cfg MapList_DM, MapList_CAP, MapList_CTF, MapList_SP, MapList_Concept, MapList_RPG).
/// Dense 0-based indices matching <see cref="MapListCatalog.All"/>.
/// </summary>
public enum MapListId
{
    None = -1,
    Dm = 0,
    Capture = 1,
    Ctf = 2,
    Sp = 3,
    Concept = 4,
    Rpg = 5,
}
