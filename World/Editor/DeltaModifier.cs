namespace SauerbratenRevival.World.Editor;

/// <summary>
/// The <c>delta_use</c> modifier namespace — the ids Cube2's universal delta
/// dispatches on (<c>data/stdlib.cfg</c>: <c>delta__edit_@active</c>). A held
/// editor key sets the active modifier; the mouse wheel then invokes the op with
/// the scroll direction as its argument.
///
/// The numeric values are **part of the input contract** (the default binds in
/// <c>data/defaults.cfg</c> use them literally, e.g. <c>editbind F [delta_use 2]</c>),
/// so they are kept identical to the original. Do not renumber.
///
/// Ids 6/7 and 11–35 exist in the original but target subsystems that are not
/// ported yet (height-map brush, entity attributes, texture/vslot parameters).
/// They are still part of the table so the dispatch is complete and their binds
/// do not silently fall through to the fill default — see
/// <see cref="EditorController.DeltaDo"/>.
/// </summary>
public enum DeltaModifier
{
    /// <summary><c>delta__edit_0</c> — default: empty/fill cubes (<c>editpush dir 1</c>).</summary>
    Fill = 0,

    /// <summary><c>delta__edit_1</c> — grid power.</summary>
    Grid = 1,

    /// <summary><c>delta__edit_2</c> — push/pull cube face (<c>editpush dir 0</c>).</summary>
    Face = 2,

    /// <summary><c>delta__edit_3</c> — push/pull cube corner (<c>editpush dir 2</c>).</summary>
    Corner = 3,

    /// <summary><c>delta__edit_4</c> — rotate geometry/brush (<c>editrotate dir</c>).</summary>
    Rotate = 4,

    /// <summary><c>delta__edit_5</c> — change texture (<c>edittex dir</c>).</summary>
    Texture = 5,

    /// <summary><c>delta__edit_6</c> — change height-map brush (<c>selectbrush</c>). Not ported.</summary>
    HeightMapBrush = 6,

    /// <summary><c>delta__edit_7</c> — entity auto-view (<c>entautoview</c>). Not ported.</summary>
    EntAutoView = 7,

    /// <summary><c>delta__edit_11</c> — entity attribute 1. Not ported.</summary>
    EntAttr1 = 11,

    /// <summary><c>delta__edit_12</c> — entity attribute 2. Not ported.</summary>
    EntAttr2 = 12,

    /// <summary><c>delta__edit_13</c> — entity attribute 3. Not ported.</summary>
    EntAttr3 = 13,

    /// <summary><c>delta__edit_14</c> — entity attribute 4. Not ported.</summary>
    EntAttr4 = 14,

    /// <summary><c>delta__edit_15</c> — entity attribute 5. Not ported.</summary>
    EntAttr5 = 15,

    /// <summary><c>delta__edit_21</c> — texture offset X (<c>voffset</c>). Not ported.</summary>
    TexOffsetX = 21,

    /// <summary><c>delta__edit_22</c> — texture offset Y (<c>voffset</c>). Not ported.</summary>
    TexOffsetY = 22,

    /// <summary><c>delta__edit_23</c> — texture rotation (<c>vrotate</c>). Not ported.</summary>
    TexRotate = 23,

    /// <summary><c>delta__edit_24</c> — texture scale (<c>vscale</c>). Not ported.</summary>
    TexScale = 24,

    /// <summary><c>delta__edit_31</c> — entity attr 1 OR texture offset X (combo).</summary>
    ComboEntAttr1TexOffsetX = 31,

    /// <summary><c>delta__edit_32</c> — entity attr 2 OR texture offset Y (combo).</summary>
    ComboEntAttr2TexOffsetY = 32,

    /// <summary><c>delta__edit_33</c> — entity attr 3 OR texture rotate (combo).</summary>
    ComboEntAttr3TexRotate = 33,

    /// <summary><c>delta__edit_34</c> — entity attr 4 OR texture scale (combo).</summary>
    ComboEntAttr4TexScale = 34,

    /// <summary><c>delta__edit_35</c> — entity attr 5 OR nothing (combo).</summary>
    ComboEntAttr5 = 35,
}
