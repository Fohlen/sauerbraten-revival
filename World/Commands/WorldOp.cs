namespace SauerbratenRevival.World.Commands;

/// <summary>
/// Stable numeric id for a world-mutation command. Part of the wire, save and
/// demo formats — **never reorder, append only** (docs/editor-and-commands.md §1.1).
///
/// The ids mirror Cube2's <c>EDIT_*</c> enum where it maps, but this is our own
/// space; the original never had a stable on-the-wire command id.
/// </summary>
public enum EditOp : ushort
{
    // --- structural ---
    DeleteCube = 0,       // empty the selected cubes (mpdelcube)
    Fill = 1,             // mpeditface mode 1: fill/carve one grid slice
    ReplaceBlock = 2,     // the universal carrier: paste/capture a region
    // --- face / texture / material ---
    SetFaceTexture = 10,  // edittexcube
    ReplaceTexture = 11,  // replacetexcube (per-selection or whole world)
    SetFaceMaterial = 12, // mpeditmat
    FlipCube = 13,        // mpflip (selected cubes, axis d)
    RotateCube = 14,      // mprotate (selected cubes, axis d, direction cw)
    // --- merge ---
    Remip = 20,           // pass-through, deterministic (docs §5.4)
    // --- lifecycle ---
    Snapshot = 100,       // no-op marker; carries a snapshot-ring index
}
