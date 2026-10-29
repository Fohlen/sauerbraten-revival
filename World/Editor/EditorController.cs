using System.Linq;
using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Meshing;
using SauerbratenRevival.World.Runtime;
using SauerbratenRevival.World.Runtime.Formats.Save;

namespace SauerbratenRevival.World.Editor;

/// <summary>
/// Wires the editor cursor to a live scene (M4d prototype). Each frame it takes the
/// camera's eye ray, casts it against the octree (<see cref="OctaRay"/>), runs the
/// cursor (<see cref="EditCursor"/>), and draws the selection box. Mouse-wheel and
/// key input drive grid size and the fill/push edits through a local
/// <see cref="CommandSink"/>.
///
/// **Scroll dispatch** is a port of Cube2's universal delta (`data/stdlib.cfg`): a
/// held <c>delta_use</c> key sets the *active modifier*, and the mouse wheel calls
/// <c>delta_do</c>, which dispatches on that id — <c>delta__edit_@active @dir</c>.
/// See <see cref="DeltaModifier"/> for the full table. Only one modifier is active
/// at a time (first key down wins), matching the original's <c>delta__active</c>.
///
/// **Prototype scope:** no GUI, no texture/material/entity pickers; the ray cast is
/// the deferred engine caster's seam (see <see cref="OctaRay"/>). Selection/cursor
/// are client-local — nothing here is replicated (docs/editor-and-commands.md §1.1).
///
/// Coordinate note: the octree is Sauerbraten Z-up; the world node is rotated
/// −90° about X, so a Sauerbraten point (x,y,z) appears in Godot at (x,z,−y).
/// This controller converts the camera ray into Sauerbraten space before casting.
/// </summary>
[GlobalClass]
public partial class EditorController : Node3D
{
    /// <summary>The camera whose eye ray drives the cursor. Defaults to a sibling/parent Camera3D.</summary>
    [Export] public Camera3D? Camera { get; set; }

    /// <summary>Maximum cursor cast distance (world units).</summary>
    [Export] public float RayLength { get; set; } = 8192f;

    /// <summary>The world being edited. Set by the scene (e.g. a MapWorld or a synthetic world root).</summary>
    public Octa? Octa { get; set; }

    /// <summary>
    /// Where this map is saved in the native format (docs §7.4). Defaults to a
    /// writable <c>user://</c> path; <c>res://</c> is read-only at runtime, so live
    /// edits must round-trip through <c>user://</c>.
    /// </summary>
    [Export] public string MapDir { get; set; } = "user://maps/untitled";

    /// <summary>The local editor cursor (client-local state).</summary>
    public EditCursor Cursor { get; } = new();

    /// <summary>The sink commands are submitted through (prototype: single-player, peer 0).</summary>
    public CommandSink? Sink { get; set; }

    /// <summary>The chunk renderer to invalidate after an edit (may be null in headless tests).</summary>
    public WorldChunkRenderer? Renderer { get; set; }

    /// <summary>The radiance volume to invalidate after an edit (may be null or shared via Renderer).</summary>
    public Lighting.RadianceVolume? Radiance { get; set; }

    /// <summary>The in-game HUD controller to sync edit mode HUD state to.</summary>
    public Hud.HudController? Hud { get; set; }

    /// <summary>
    /// The currently-selected texture index (the editor's "current texture"), applied
    /// by <c>edittex</c> and the <c>&#96;delta__edit_5&#96;</c> wheel modifier. Set by the M4e
    /// texture picker; a stable default keeps the binds usable before the picker exists.
    /// </summary>
    public int CurrentTexture { get; set; } = Octa.DEFAULT_GEOM;

    /// <summary>The currently-selected material id (the "current material"), applied by <c>editmat</c>.</summary>
    public int CurrentMaterial { get; set; } = Octa.MAT_AIR;

    /// <summary>
    /// <c>allfaces</c> (editbindvar): when true, texture/material edits apply to all
    /// six faces instead of only the pointed face.
    /// </summary>
    public bool AllFaces { get; set; }

    /// <summary>
    /// The active <c>delta_use</c> modifier id, or null when none is held. This is a
    /// port of the original's <c>delta__active</c> variable: the first modifier key
    /// pressed wins and further presses are ignored until it is released.
    /// </summary>
    public DeltaModifier? ActiveModifier { get; private set; }

    /// <summary>
    /// The local copy buffer (editcopy). Client-local: a copy is not a world
    /// mutation, so it never enters the command log; only a paste does.
    /// </summary>
    public OctaEdit.CapturedBlock? Clipboard { get; private set; }

    private MeshInstance3D? _box;
    private ImmediateMesh? _mesh;
    private string _lastBoxKey = "";

    public override void _Ready()
    {
        Camera ??= GetViewport().GetCamera3D();
        _mesh = new ImmediateMesh();
        _box = new MeshInstance3D
        {
            Name = "SelectionBox",
            Mesh = _mesh,
            // Draw over geometry without depth fighting, but still depth-tested.
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = false,
            },
        };
        AddChild(_box);
    }

    public override void _Process(double delta)
    {
        if (Octa == null || Camera == null) return;

        // Keep the delta_use active modifier in sync with currently-held keys.
        UpdateActiveModifier();

        Vector3 eyeGodot = Camera.GlobalPosition;
        Vector3 fwdGodot = -Camera.GlobalTransform.Basis.Z.Normalized();

        Vector3 eye = ToSauer(eyeGodot);
        Vector3 dir = ToSauerDir(fwdGodot);

        float dist = OctaRay.RayCube(Octa, eye, dir, RayLength, Cursor.GridSize, out _);
        Cursor.Update(Octa, eye, dir, dist);

        // Right-drag (editextend): grow the selection to the cursor each frame.
        if (_drag == DragMode.Extend)
        {
            Cursor.ExtendSelection();
            Cursor.Reorient();
        }

        if (Hud != null)
        {
            Hud.IsEditMode = true;
            Hud.EditGridSize = Cursor.GridSize;
            Hud.EditGridPower = Cursor.GridPower;
            Hud.EditSelChildCount = Cursor.SelChildCount;
            Hud.EditSelChildMat = Cursor.SelChildMat;
            Hud.EditAllFaces = AllFaces;
            Hud.EditSelPos = Cursor.Selection.O;
        }

        DrawBox();
    }

    // ---- input (prototype wheel dispatch) ----

    private float _panAccum;
    /// <summary>Trackpad pan units per scroll notch (tuned; a wheel sends one notch per event).</summary>
    private const float PanNotch = 5f;

    /// <summary>Which drag gesture is active (set by mouse buttons).</summary>
    private enum DragMode { None, BoxSelect, Extend }
    private DragMode _drag = DragMode.None;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Octa == null) return;

        // Mouse buttons: drag gestures (Cube2 editbind MOUSE1/MOUSE2/MOUSE3).
        if (@event is InputEventMouseButton button)
        {
            switch (button.ButtonIndex)
            {
                case MouseButton.Left:
                    // editdrag: cancel the selection, then rubber-band a new one.
                    if (button.Pressed) { Cursor.CancelSelection(); Cursor.BeginDrag(); _drag = DragMode.BoxSelect; }
                    else if (_drag == DragMode.BoxSelect) { Cursor.Dragging = false; _drag = DragMode.None; }
                    return;
                case MouseButton.Right:
                    // editextend: grow the selection to the cursor while held.
                    if (button.Pressed) { _drag = DragMode.Extend; }
                    else if (_drag == DragMode.Extend) { _drag = DragMode.None; }
                    return;
                case MouseButton.Middle:
                    // selcorners (corners): corner-exact rubber-band selection.
                    if (button.Pressed)
                    {
                        Cursor.CancelSelection();
                        Cursor.SelectCorners = true;
                        Cursor.BeginDrag();
                        _drag = DragMode.BoxSelect;
                    }
                    else
                    {
                        Cursor.SelectCorners = false;
                        if (_drag == DragMode.BoxSelect) { Cursor.Dragging = false; _drag = DragMode.None; }
                    }
                    return;
                case MouseButton.WheelUp when button.Pressed: Scroll(+1); return;
                case MouseButton.WheelDown when button.Pressed: Scroll(-1); return;
                // MOUSE6/7 (back/forward) = editcut.
                case MouseButton.Xbutton1 when button.Pressed: EditCut(); return;
                case MouseButton.Xbutton2 when button.Pressed: EditCut(); return;
            }
            return;
        }

        // macOS trackpad / Magic Mouse: scrolling arrives as a fractional pan
        // gesture, never as WheelUp/WheelDown. Accumulate into notches so one
        // notch is one edit, matching a wheel.
        if (@event is InputEventPanGesture pan)
        {
            _panAccum += pan.Delta.Y;
            while (Mathf.Abs(_panAccum) >= PanNotch)
            {
                // Godot: positive delta.Y scrolls content down (like WheelDown).
                Scroll(_panAccum > 0 ? -1 : +1);
                _panAccum -= Mathf.Sign(_panAccum) * PanNotch;
            }
            return;
        }

        UpdateActiveModifier();

        // One-shot editor actions (InputMap, remappable).
        DispatchActions(@event);
    }

    /// <summary>
    /// Map remappable <c>InputMap</c> actions to editor calls. Each is checked with
    /// <c>IsActionPressed</c> on the event so bindings can be changed in Project
    /// Settings (or at runtime) without touching this code.
    /// </summary>
    private void DispatchActions(InputEvent e)
    {
        if (Pressed(e, "editor_cancel")) Cursor.CancelSelection();
        if (Pressed(e, "editor_flip")) EditTransform(flip: true);
        if (Pressed(e, "editor_delete")) EditDelete();
        if (Pressed(e, "editor_copy")) EditCopy();
        if (Pressed(e, "editor_paste")) EditPaste();
        if (Pressed(e, "editor_cut")) EditCut();
        if (Pressed(e, "editor_undo")) EditUndo();
        if (Pressed(e, "editor_redo")) EditRedo();
        if (Pressed(e, "editor_save")) SaveMap();
        if (Pressed(e, "editor_load")) LoadMap();

        // Remaining edit binds (data/defaults.cfg editbind).
        if (Pressed(e, "editor_allfaces")) ToggleAllFaces();          // editbindvar 6
        if (Pressed(e, "editor_selcorners")) ToggleSelCorners();      // editbind MOUSE3 is a mouse gesture
        if (Pressed(e, "editor_remip")) EditRemip();                  // K/remip
        if (Pressed(e, "editor_tex")) EditTex(CurrentTexture);        // edittex (current texture)
        if (Pressed(e, "editor_mat_air")) EditMat(Octa.MAT_AIR);      // editmat air
        if (Pressed(e, "editor_mat_clip")) EditMat(Octa.MAT_CLIP);    // editmat clip
        if (Pressed(e, "editor_mat_death")) EditMat(Octa.MAT_DEATH); // editmat death
        if (Pressed(e, "editor_pastehilite")) PasteHilite();          // pastehilite

        // pushsel: move the selection along the oriented axis (editbind PUSH/PULL use wheel).
        if (Pressed(e, "editor_pushsel_fwd")) PushSelection(+1);
        if (Pressed(e, "editor_pushsel_back")) PushSelection(-1);

        // Numpad selection nudge (KP4-9 = movesel).
        if (Pressed(e, "editor_sel_left")) Cursor.MoveSelection(-1, 0);
        if (Pressed(e, "editor_sel_right")) Cursor.MoveSelection(+1, 0);
        if (Pressed(e, "editor_sel_fwd")) Cursor.MoveSelection(+1, 1);
        if (Pressed(e, "editor_sel_back")) Cursor.MoveSelection(-1, 1);
        if (Pressed(e, "editor_sel_up")) Cursor.MoveSelection(+1, 2);
        if (Pressed(e, "editor_sel_down")) Cursor.MoveSelection(-1, 2);
    }

    /// <summary>True if the event just triggered a remappable action (ignoring key echo).</summary>
    private static bool Pressed(InputEvent e, string action)
    {
        if (e is InputEventKey k && (k.Echo || !k.Pressed)) return false;
        return e.IsActionPressed(action);
    }

    // ---- delta_use / delta_do (data/stdlib.cfg) ----

    /// <summary>
    /// The modifier each <c>editor_mod_*</c> action selects. Ids match the original's
    /// <c>delta_use</c> arguments (the defaults in <c>data/defaults.cfg</c>). Ordered
    /// so the first pressed of several wins deterministically (see
    /// <see cref="PressModifier"/>).
    /// </summary>
    private static readonly (string Action, DeltaModifier Modifier)[] ModifierActions =
    {
        ("editor_mod_grid", DeltaModifier.Grid),
        ("editor_mod_face", DeltaModifier.Face),
        ("editor_mod_corner", DeltaModifier.Corner),
        ("editor_mod_rotate", DeltaModifier.Rotate),
        ("editor_mod_texture", DeltaModifier.Texture),
        ("editor_mod_attr1", DeltaModifier.ComboEntAttr1TexOffsetX),
        ("editor_mod_attr2", DeltaModifier.ComboEntAttr2TexOffsetY),
        ("editor_mod_attr3", DeltaModifier.ComboEntAttr3TexRotate),
        ("editor_mod_attr4", DeltaModifier.ComboEntAttr4TexScale),
    };

    /// <summary>True if a held modifier action is currently pressed (the <c>delta_use</c> namespace).</summary>
    private static bool Held(string action) => InputMap.HasAction(action) && Input.IsActionPressed(action);

    /// <summary>
    /// <c>delta_use N</c> — select the active modifier. Only the first key down wins:
    /// while a modifier is active, further presses are ignored; the active one is
    /// cleared when it is released. This mirrors the original's <c>delta__active</c>
    /// guard, which exists to stop "janky behavior if multiple delta_use keys are
    /// pressed simultaneously".
    /// </summary>
    public void PressModifier(DeltaModifier modifier)
    {
        ActiveModifier ??= modifier;
    }

    /// <summary><c>delta_use</c> release — clear the active modifier if it matches.</summary>
    public void ReleaseModifier(DeltaModifier modifier)
    {
        if (ActiveModifier == modifier) ActiveModifier = null;
    }

    /// <summary>
    /// Poll held modifier actions each frame (the original resolves the active
    /// modifier from held keys at wheel time). A key down sets the active modifier;
    /// the first still-held modifier keeps it; nothing held clears it. Shift is an
    /// extra grid modifier for muscle memory.
    /// </summary>
    private void UpdateActiveModifier()
    {
        if (ActiveModifier.HasValue && !Held(ActionFor(ActiveModifier.Value)) &&
            !(ActiveModifier == DeltaModifier.Grid && Held("editor_grid_shift")))
            ActiveModifier = null;
        if (ActiveModifier.HasValue) return;
        foreach (var (action, modifier) in ModifierActions)
        {
            if (Held(action)) { ActiveModifier = modifier; return; }
        }
        // Shift is accepted as a grid modifier for the original's muscle memory
        // (the old hardcoded dispatch, before the full delta table).
        if (Held("editor_grid_shift")) ActiveModifier = DeltaModifier.Grid;
    }

    private static string ActionFor(DeltaModifier modifier)
    {
        foreach (var (action, m) in ModifierActions)
            if (m == modifier) return action;
        return string.Empty;
    }

    /// <summary>
    /// <c>delta_do &lt;arg&gt;</c> — one scroll notch, dispatched on the active modifier
    /// (or the fill default when none is held). This is the editor half of Cube2's
    /// universal delta; the full <c>delta__edit_@active</c> table is reproduced here
    /// so every bind routes to the right op (or an explicit no-op for subsystems not
    /// yet ported) instead of silently falling through to fill.
    /// </summary>
    public void DeltaDo(int notch)
    {
        switch (ActiveModifier ?? DeltaModifier.Fill)
        {
            case DeltaModifier.Fill:
                EditFace(notch, 1);          // delta__edit_0: editpush dir 1
                break;
            case DeltaModifier.Grid:
                SetGrid(Cursor.GridPower + notch); // delta__edit_1: gridpower
                break;
            case DeltaModifier.Face:
                EditFace(notch, 0);          // delta__edit_2: editpush dir 0
                break;
            case DeltaModifier.Corner:
                EditFace(notch, 2);          // delta__edit_3: editpush dir 2
                break;
            case DeltaModifier.Rotate:
                EditRotate(notch);           // delta__edit_4: editrotate dir
                break;
            case DeltaModifier.Texture:
                EditTexScroll(notch);       // delta__edit_5: edittex dir
                break;

            // Height-map brush (6) and entity auto-view (7): no subsystem yet.
            case DeltaModifier.HeightMapBrush:
            case DeltaModifier.EntAutoView:
                break;

            // Entity attributes: no entity editing yet (delta__edit_11..15).
            case DeltaModifier.EntAttr1:
            case DeltaModifier.EntAttr2:
            case DeltaModifier.EntAttr3:
            case DeltaModifier.EntAttr4:
            case DeltaModifier.EntAttr5:
                break;

            // Texture/vslot parameters: no vslot model yet (delta__edit_21..24).
            case DeltaModifier.TexOffsetX:
            case DeltaModifier.TexOffsetY:
            case DeltaModifier.TexRotate:
            case DeltaModifier.TexScale:
                break;

            // Combos (delta__edit_31..35): entity attr when entities are selected,
            // else the texture parameter. Entity editing is not ported; with the
            // `entediting` var off the original also picks the texture branch, which
            // is likewise not ported yet — so these are no-ops until M4e/M4f.
            case DeltaModifier.ComboEntAttr1TexOffsetX:
            case DeltaModifier.ComboEntAttr2TexOffsetY:
            case DeltaModifier.ComboEntAttr3TexRotate:
            case DeltaModifier.ComboEntAttr4TexScale:
            case DeltaModifier.ComboEntAttr5:
                break;
        }
    }

    /// <summary>
    /// Legacy entry point: a scroll notch through the delta dispatch. Kept so the
    /// existing input tests and callers keep working; <see cref="DeltaDo"/> is the
    /// canonical name.
    /// </summary>
    public void Scroll(int notch) => DeltaDo(notch);

    /// <summary>Adjust the grid power and rebuild the cursor grid.</summary>
    public void SetGrid(int power)
    {
        if (Octa == null) return;
        Cursor.SetGridPower(power, Octa.WorldSize);
    }

    /// <summary>Run a fill/empty edit (mpeditface mode 1) at the cursor selection.</summary>
    public void EditFill(int dir) => EditFace(dir, 1);

    /// <summary>
    /// Run an <c>mpeditface</c> edit (mode 0 face, 1 fill, 2 corner) at the cursor
    /// selection. Submitted through the sink so it is sequenced, undoable and
    /// replayable / replicable like every other world mutation.
    /// </summary>
    public void EditFace(int dir, int mode)
    {
        if (Sink == null) return;
        var sel = CurrentSelection();
        Sink.Submit(Command.CreateEditFace(sel, dir, mode));
        InvalidateRegion(sel);
    }

    /// <summary>Rotate the selection's cubes (mprotate) about the oriented axis.</summary>
    public void EditRotate(int cw)
    {
        if (Sink == null) return;
        var sel = CurrentSelection();
        Sink.Submit(Command.CreateRotate(sel, cw));
        InvalidateRegion(sel);
    }

    /// <summary>Flip the selection (mpflip) along the oriented axis.</summary>
    public void EditTransform(bool flip)
    {
        if (Sink == null || !flip) return;
        var sel = CurrentSelection();
        Sink.Submit(Command.CreateFlip(sel));
        InvalidateRegion(sel);
    }

    /// <summary>
    /// <c>edittex dir</c> (octaedit.cpp:2486) — step the current texture through the
    /// most-recently-used list and paint it across the selection. Also the
    /// <c>delta__edit_5</c> wheel op.
    ///
    /// The MRU history is a picker concern (M4e); until it lands, a scroll walks the
    /// texture index by <paramref name="dir"/>, which exercises the same
    /// <see cref="WorldOp.SetFaceTexture"/> path. <c>allfaces</c> paints all six faces.
    /// </summary>
    public void EditTexScroll(int dir)
    {
        CurrentTexture = System.Math.Max(0, CurrentTexture + dir);
        EditTex(CurrentTexture);
    }

    /// <summary>Paint texture <paramref name="tex"/> across the selection (edittex). <c>allfaces</c> paints all six faces.</summary>
    public void EditTex(int tex)
    {
        if (Sink == null) return;
        CurrentTexture = tex;
        var sel = CurrentSelection();
        Sink.Submit(Command.CreateSetTexture(sel, tex, AllFaces ? -1 : sel.Orient));
        InvalidateRegion(sel);
    }

    /// <summary>
    /// <c>editmat name filter</c> (octaedit.cpp:2763) — apply a material across the
    /// selection. <paramref name="filter"/> is the original's packed
    /// <c>filtermat | (filtergeom &lt;&lt; 16)</c>; pass -1 for none.
    /// </summary>
    public void EditMat(int materialId, int filter = -1)
    {
        if (Sink == null) return;
        CurrentMaterial = materialId;
        var sel = CurrentSelection();
        Sink.Submit(Command.CreateSetMaterial(sel, materialId, filter));
        InvalidateRegion(sel);
    }

    /// <summary>
    /// <c>editremip</c> — merge/split cube runs (octaedit.cpp remip). Pass-through and
    /// deterministic (docs §5.4); <c>mipvis</c> is the user toggle that changes the
    /// collapse decision, so it rides the payload.
    /// </summary>
    public void EditRemip(bool mipvis = false)
    {
        if (Sink == null) return;
        Sink.Submit(Command.CreateRemip(mipvis));
        InvalidateAll();
    }

    /// <summary>
    /// <c>pushsel dir</c> (octaedit.cpp:2070) — move the selection one grid cell along
    /// the oriented axis in the direction of the pointed face. Client-local (a
    /// selection move, not a world mutation), so it does not enter the command log.
    /// </summary>
    public void PushSelection(int dir) => Cursor.MoveSelectionOriented(dir);

    /// <summary>
    /// <c>pastehilite</c> — give the clipboard block a selection the size of what was
    /// copied, at the cursor, so the paste lands where it was previewed.
    /// </summary>
    public void PasteHilite()
    {
        if (Clipboard == null) return;
        var d = Clipboard.Descriptor;
        Cursor.CancelSelection();
        Cursor.SetSelectionPos(Cursor.Cur);
        Cursor.Selection.S = d.S;
        Cursor.Selection.Grid = d.Grid;
        Cursor.Selection.Reorient(d.Orient);
    }

    /// <summary>Toggle <c>allfaces</c> (editbindvar): texture/material edits target all six faces.</summary>
    public void ToggleAllFaces() => AllFaces = !AllFaces;

    /// <summary>Toggle <c>selcorners</c>: corner-exact drag selection (also Mouse3).</summary>
    public void ToggleSelCorners() => Cursor.SelectCorners = !Cursor.SelectCorners;

    /// <summary>Delete the selected cubes (editdel / delcube).</summary>
    public void EditDelete()
    {
        if (Sink == null) return;
        var sel = CurrentSelection();
        Sink.Submit(Command.CreateDeleteCube(sel));
        InvalidateRegion(sel);
    }

    /// <summary>The selection as the editor currently sees it (cursor cell if none).</summary>
    private Selection CurrentSelection()
    {
        var sel = Cursor.Selection;
        sel.Grid = Cursor.GridSize;
        sel.Orient = Cursor.Orient;
        if (sel.IsZero) { sel.O = Cursor.Cur; sel.S = new Vector3I(1, 1, 1); }
        return sel;
    }

    // ---- clipboard (editcopy / editcut / editpaste) ----

    /// <summary>editcopy — capture the selection into the local clipboard. Not a command.</summary>
    public void EditCopy()
    {
        if (Octa == null) return;
        var sel = CurrentSelection();
        Clipboard = OctaEdit.BlockCopy(Octa, new Block3(sel), sel.Grid);
    }

    /// <summary>editcut — capture the selection, then delete it.</summary>
    public void EditCut()
    {
        EditCopy();
        EditDelete();
    }

    /// <summary>editpaste — write the clipboard into the world at the cursor selection.</summary>
    public void EditPaste()
    {
        if (Sink == null || Clipboard == null) return;
        var sel = CurrentSelection();
        Sink.Submit(Command.CreateReplaceBlock(sel, Network.NetMessage.SerializeBlock(Clipboard)));
        InvalidateRegion(sel);
    }

    /// <summary>
    /// Rebuild the whole world mesh. Used after undo/redo/remip, which can touch any
    /// chunk — and undo of a <c>NewMap</c> can even change the world size, so a full
    /// <see cref="WorldChunkRenderer.Setup"/> (not just dirtying) is the safe path.
    /// </summary>
    private void InvalidateAll()
    {
        if (Octa == null || Renderer == null) return;
        Renderer.ReSetup();
    }

    /// <summary>editundo — revert this peer's last editor command.</summary>
    public void EditUndo() { Sink?.Undo(); InvalidateAll(); }

    /// <summary>redo — re-apply the last undone command.</summary>
    public void EditRedo() { Sink?.Redo(); InvalidateAll(); }

    // ---- save / load (native format, docs §7) ----

    /// <summary>
    /// Save the current world to <see cref="MapDir"/> in the native format. If history.res
    /// exists, commits the editor changes into the linear commit chain (ADR 0008).
    /// Otherwise, saves a compacted base snapshot (docs §7.3).
    /// </summary>
    public void SaveMap()
    {
        if (Octa == null) return;
        var entities = Sink?.World.Entities;
        MapStore.SaveCompacted(MapDir, Octa, entities);
        GD.Print($"[editor] saved '{MapDir}' (hash {MapStore.ContentHash(MapDir)})");
    }

    /// <summary>
    /// Load the world from <see cref="MapDir"/>, replacing the live octree. The
    /// command log is reset: the loaded state is the new base (docs §7.3). Undo does
    /// not span a load.
    /// </summary>
    public void LoadMap()
    {
        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(MapDir))) return;
        var loaded = MapStore.Load(MapDir);
        Octa = loaded.World;
        Sink = new CommandSink(new OctaWorld(loaded.World, loaded.Snapshot.Entities));

        // The renderer was built for the old octree: rebuild it against the loaded one.
        Renderer?.Setup(loaded.World, materials: null);
        DetachWorldFromParent();
        GD.Print($"[editor] loaded '{MapDir}'");
    }

    /// <summary>Binds editor authority (octree, command sink, renderer, radiance) to a map source.</summary>
    public void BindMapSource(IMapSource source)
    {
        Octa = source.Octa;
        Sink = source.Sink;
        Renderer = source.Renderer;
        Radiance = source.Radiance;
        if (source is MapLevel ml && !string.IsNullOrEmpty(ml.MapDir))
        {
            MapDir = ml.MapDir;
        }
    }

    /// <summary>Re-point the owning map node at the newly loaded octree, if any.</summary>
    private void DetachWorldFromParent()
    {
        var parent = GetParent();
        if (parent is IMapSource parentMs)
        {
            parentMs.Octa = Octa;
            parentMs.Sink = Sink;
            parentMs.Renderer = Renderer;
            return;
        }

        if (parent != null)
        {
            foreach (var node in parent.GetChildren())
            {
                if (node is IMapSource ms)
                {
                    ms.Octa = Octa;
                    ms.Sink = Sink;
                    ms.Renderer = Renderer;
                    break;
                }
            }
        }
    }

    /// <summary>Re-mesh the region an edit touched (expanded by one grid cell).</summary>
    private void InvalidateRegion(Selection sel)
    {
        var o = new Vector3(sel.O.X, sel.O.Y, sel.O.Z);
        var size = new Vector3(sel.S.X * sel.Grid, sel.S.Y * sel.Grid, sel.S.Z * sel.Grid);
        var aabb = new Aabb(o, size);
        Renderer?.MarkDirty(aabb, margin: sel.Grid);
        // If Radiance is set directly and not wired through Renderer.Radiance, mark it dirty directly:
        if (Renderer?.Radiance == null)
        {
            Radiance?.MarkDirty(aabb, margin: sel.Grid);
        }
    }

    // ---- selection box rendering ----

    private void DrawBox()
    {
        if (_mesh == null) return;

        var sel = Cursor.Selection;
        bool showSel = Cursor.HasSelection;
        // Rebuild only when the cursor/box actually changed (avoids per-frame GPU churn).
        // The cursor tracks the pointed cube every frame, so it must be part of the key.
        string key = $"{Cursor.Lu}|{Cursor.LuSize}|{Cursor.Orient}|{(showSel ? $"{sel.O}|{sel.S}|{sel.Grid}|{sel.Orient}" : "")}";
        if (key == _lastBoxKey) return;
        _lastBoxKey = key;

        _mesh.ClearSurfaces();
        _box!.Visible = true;

        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

        // The grey cursor: the face rectangle of the pointed cube. This is drawn
        // every frame and follows the mouse even while a selection exists — the
        // original's `boxs(orient, vec(lu), vec(lusize))` (octaedit.cpp:542). Without
        // it the only box on screen is the selection, which is anchored once a
        // selection exists, so the cursor appears to stop following.
        EmitFaceRect(new Vector3(Cursor.Lu.X, Cursor.Lu.Y, Cursor.Lu.Z), Cursor.LuSize,
            Cursor.Orient, new Color(120f / 255f, 120f / 255f, 120f / 255f, 1f));

        if (showSel)
        {
            var o = new Vector3(sel.O.X, sel.O.Y, sel.O.Z);
            var s = new Vector3(sel.S.X * sel.Grid, sel.S.Y * sel.Grid, sel.S.Z * sel.Grid);
            // White selection box, grey grid lines, red origin marker (Cube2 boxs/boxsgrid/boxs3D).
            EmitBox(o, s, new Color(1f, 1f, 1f, 1f));
            EmitGrid(o, s, sel.Grid, sel.Orient, new Color(0.4f, 0.4f, 0.4f, 1f));
            float m = Mathf.Min(sel.Grid * 0.25f, 2f);
            EmitBox(o - new Vector3(m * 0.5f, m * 0.5f, m * 0.5f), new Vector3(m, m, m), new Color(0.8f, 0f, 0f, 1f));
        }

        _mesh.SurfaceEnd();
    }

    /// <summary>
    /// octaedit.cpp <c>boxs(orient, o, s)</c> — the pointed face's outline: a line
    /// loop on the plane perpendicular to <paramref name="orient"/>, at the far side
    /// of the cube (so it sits on the face the ray struck).
    /// </summary>
    private void EmitFaceRect(Vector3 o, int size, int orient, Color color)
    {
        int d = Selection.Dimension(orient), dc = Selection.DimCoord(orient);
        int ra = Octa.R[d], ca = Octa.C[d];
        float s = size;

        // boxs: o[D[d]] += dc * s[D[d]]
        o[d] += dc * s;

        Vector3 p0 = o;
        Vector3 p1 = AddAxis(o, ra, s);
        Vector3 p2 = AddAxis(p1, ca, s);
        Vector3 p3 = AddAxis(o, ca, s);
        Line(p0, p1, color);
        Line(p1, p2, color);
        Line(p2, p3, color);
        Line(p3, p0, color);
    }

    private static Vector3 AddAxis(Vector3 v, int axis, float amount) => axis switch
    {
        0 => new Vector3(v.X + amount, v.Y, v.Z),
        1 => new Vector3(v.X, v.Y + amount, v.Z),
        _ => new Vector3(v.X, v.Y, v.Z + amount),
    };

    private void EmitBox(Vector3 o, Vector3 s, Color color)
    {
        var p = new Vector3[8];
        for (int i = 0; i < 8; i++)
            p[i] = o + new Vector3((i & 1) != 0 ? s.X : 0, (i & 2) != 0 ? s.Y : 0, (i & 4) != 0 ? s.Z : 0);
        // 12 edges.
        int[,] edges =
        {
            {0,1},{1,3},{3,2},{2,0},
            {4,5},{5,7},{7,6},{6,4},
            {0,4},{1,5},{2,6},{3,7},
        };
        for (int e = 0; e < 12; e++)
            Line(p[edges[e, 0]], p[edges[e, 1]], color);
    }

    private void Line(Vector3 a, Vector3 b, Color color)
    {
        _mesh!.SurfaceSetColor(color);
        _mesh.SurfaceAddVertex(a);
        _mesh.SurfaceSetColor(color);
        _mesh.SurfaceAddVertex(b);
    }

    /// <summary>
    /// octaedit.cpp <c>boxsgrid</c> — subdivide the selection face in its oriented
    /// plane at grid spacing, so the cube grid is visible. Drawn on the two in-plane
    /// axes (R and C of the oriented dimension).
    /// </summary>
    private void EmitGrid(Vector3 o, Vector3 s, int grid, int orient, Color color)
    {
        int d = Selection.Dimension(orient);
        int ra = Octa.R[d], ca = Octa.C[d];
        var size = new int[3];
        size[0] = (int)s.X; size[1] = (int)s.Y; size[2] = (int)s.Z;
        // Lines parallel to C, spaced along R.
        for (int x = grid; x < size[ra]; x += grid)
        {
            var p0 = o; var p1 = o;
            p0.X += Axis(ra, 0, x); p0.Y += Axis(ra, 1, x); p0.Z += Axis(ra, 2, x);
            p1 = p0;
            var cLen = size[ca];
            p1.X += Axis(ca, 0, cLen); p1.Y += Axis(ca, 1, cLen); p1.Z += Axis(ca, 2, cLen);
            Line(p0, p1, color);
        }
        // Lines parallel to R, spaced along C.
        for (int y = grid; y < size[ca]; y += grid)
        {
            var p0 = o; var p1 = o;
            p0.X += Axis(ca, 0, y); p0.Y += Axis(ca, 1, y); p0.Z += Axis(ca, 2, y);
            p1 = p0;
            var rLen = size[ra];
            p1.X += Axis(ra, 0, rLen); p1.Y += Axis(ra, 1, rLen); p1.Z += Axis(ra, 2, rLen);
            Line(p0, p1, color);
        }
    }

    private static float Axis(int axis, int comp, float v) => comp == axis ? v : 0f;

    // ---- Sauerbraten (Z-up) <-> Godot (Y-up) ----

    private static Vector3 ToSauer(Vector3 g) => new(-g.X, -g.Z, g.Y);
    private static Vector3 ToSauerDir(Vector3 g) => new(-g.X, -g.Z, g.Y);
}
