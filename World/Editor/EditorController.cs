using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;
using SauerbratenRevival.World.Meshing;

namespace SauerbratenRevival.World.Editor;

/// <summary>
/// Wires the editor cursor to a live scene (M4d prototype). Each frame it takes the
/// camera's eye ray, casts it against the octree (<see cref="OctaRay"/>), runs the
/// cursor (<see cref="EditCursor"/>), and draws the selection box. Mouse-wheel and
/// key input drive grid size and the fill/push edits through a local
/// <see cref="WorldCommandSink"/>.
///
/// **Prototype scope:** no GUI, no textures/materials; the ray cast is the deferred
/// engine caster's seam (see <see cref="OctaRay"/>). Selection/cursor are
/// client-local — nothing here is replicated (docs/editor-and-commands.md §1.1).
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

    /// <summary>The local editor cursor (client-local state).</summary>
    public EditCursor Cursor { get; } = new();

    /// <summary>The sink commands are submitted through (prototype: single-player, peer 0).</summary>
    public WorldCommandSink? Sink { get; set; }

    /// <summary>The chunk renderer to invalidate after an edit (may be null in headless tests).</summary>
    public WorldChunkRenderer? Renderer { get; set; }

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

        Vector3 eyeGodot = Camera.GlobalPosition;
        Vector3 fwdGodot = -Camera.GlobalTransform.Basis.Z.Normalized();

        Vector3 eye = ToSauer(eyeGodot);
        Vector3 dir = ToSauerDir(fwdGodot);

        float dist = OctaRay.RayCube(Octa, eye, dir, RayLength, out _);
        Cursor.Update(Octa, eye, dir, dist);

        // Right-drag (editextend): grow the selection to the cursor each frame.
        if (_drag == DragMode.Extend)
        {
            Cursor.ExtendSelection();
            Cursor.Reorient();
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

        // Mouse buttons: drag gestures (Cube2 editbind MOUSE1/MOUSE2).
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

        // Keys: one-shot edit actions (Cube2 editbinds).
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.PhysicalKeycode)
            {
                case Key.Space: Cursor.CancelSelection(); break;
                case Key.X: EditTransform(flip: true); break;
                case Key.Delete: EditDelete(); break;
                case Key.C: EditCopy(); break;
                case Key.V: EditPaste(); break;
                case Key.U: EditUndo(); break;
                case Key.I: EditRedo(); break;
                case Key.B: EditCut(); break; // Cube2: B = editcut
                // Numpad selection nudge (KP4-9 = movesel).
                case Key.Kp4: Cursor.MoveSelection(-1, 0); break;
                case Key.Kp6: Cursor.MoveSelection(+1, 0); break;
                case Key.Kp8: Cursor.MoveSelection(+1, 1); break;
                case Key.Kp5: Cursor.MoveSelection(-1, 1); break;
                case Key.Kp9: Cursor.MoveSelection(+1, 2); break;
                case Key.Kp7: Cursor.MoveSelection(-1, 2); break;
            }
        }
    }

    /// <summary>
    /// The <c>delta_use</c> namespace: what a scroll notch does depends on which
    /// editor key is held (Cube2 <c>editbind F/G/Q/R/Y</c>). Falls back to fill/empty.
    /// </summary>
    private static bool Shift => Input.IsKeyPressed(Key.Shift);
    private static bool Held(Key k) => Input.IsKeyPressed(k);

    /// <summary>
    /// One scroll notch: plain = fill/empty (mpeditface mode 1, the white-box
    /// push/pull); Shift = change grid size. Mirrors Cube2's <c>delta_do</c> dispatch.
    /// </summary>
    public void Scroll(int notch)
    {
        // Cube2 delta_use namespace: the held key selects the action.
        if (Held(Key.G) || Shift) { SetGrid(Cursor.GridPower + notch); return; }
        if (Held(Key.F)) { EditFace(notch, 0); return; }   // push/pull cube face
        if (Held(Key.Q)) { EditFace(notch, 2); return; }   // push/pull cube corner
        if (Held(Key.R)) { EditRotate(notch); return; }
        // Default: fill/empty (mpeditface mode 1), the white-box push/pull.
        EditFace(notch, 1);
    }

    /// <summary>Adjust the grid power and rebuild the cursor grid.</summary>
    public void SetGrid(int power)
    {
        if (Octa == null) return;
        Cursor.SetGridPower(power, Octa.WorldSize);
    }

    /// <summary>Run a fill/empty edit (mpeditface mode 1) at the cursor selection.</summary>
    public void EditFill(int dir) => EditFace(dir, 1);

    /// <summary>Run an <c>mpeditface</c> edit (mode 0 face, 1 fill, 2 corner) at the cursor selection.</summary>
    public void EditFace(int dir, int mode)
    {
        if (Octa == null) return;
        var sel = CurrentSelection();
        OctaEdit.EditFace(Octa, sel, dir, mode);
        InvalidateRegion(sel);
    }

    /// <summary>Rotate the selection's cubes (mprotate) about the oriented axis.</summary>
    public void EditRotate(int cw)
    {
        if (Octa == null) return;
        var sel = CurrentSelection();
        OctaEdit.RotateSelection(Octa, sel, cw);
        InvalidateRegion(sel);
    }

    /// <summary>Flip the selection (mpflip) along the oriented axis.</summary>
    public void EditTransform(bool flip)
    {
        if (Octa == null) return;
        var sel = CurrentSelection();
        if (flip) OctaEdit.FlipSelection(Octa, sel);
        InvalidateRegion(sel);
    }

    /// <summary>Delete the selected cubes (editdel / delcube).</summary>
    public void EditDelete()
    {
        if (Sink == null || Octa == null) return;
        var sel = CurrentSelection();
        Sink.Submit(new WorldCommand(WorldOp.DeleteCube, new DeleteCubePayload(new SelectionPayload(sel)),
            source: CommandSource.Editor));
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
        if (Sink == null || Octa == null || Clipboard == null) return;
        var sel = CurrentSelection();
        Sink.Submit(new WorldCommand(WorldOp.ReplaceBlock,
            new ReplaceBlockPayload(new SelectionPayload(sel), Clipboard),
            source: CommandSource.Editor));
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

    /// <summary>Re-mesh the region an edit touched (expanded by one grid cell).</summary>
    private void InvalidateRegion(Selection sel)
    {
        if (Renderer == null) return;
        var o = new Vector3(sel.O.X, sel.O.Y, sel.O.Z);
        var size = new Vector3(sel.S.X * sel.Grid, sel.S.Y * sel.Grid, sel.S.Z * sel.Grid);
        Renderer.MarkDirty(new Aabb(o, size), margin: sel.Grid);
    }

    // ---- selection box rendering ----

    private void DrawBox()
    {
        if (_mesh == null) return;

        var sel = Cursor.Selection;
        bool visible = Cursor.HasSelection || !sel.IsZero;
        // Rebuild only when the box actually changed (avoids per-frame GPU churn).
        string key = visible ? $"{sel.O}|{sel.S}|{sel.Grid}" : "";
        if (key == _lastBoxKey) return;
        _lastBoxKey = key;

        _mesh.ClearSurfaces();
        _box!.Visible = visible;
        if (!visible) return;

        var o = new Vector3(sel.O.X, sel.O.Y, sel.O.Z);
        var s = new Vector3(sel.S.X * sel.Grid, sel.S.Y * sel.Grid, sel.S.Z * sel.Grid);

        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        // White selection box, grey grid lines, red origin marker (Cube2 boxs/boxsgrid/boxs3D).
        EmitBox(o, s, new Color(1f, 1f, 1f, 1f));
        EmitGrid(o, s, sel.Grid, sel.Orient, new Color(0.4f, 0.4f, 0.4f, 1f));
        float m = Mathf.Min(sel.Grid * 0.25f, 2f);
        EmitBox(o - new Vector3(m * 0.5f, m * 0.5f, m * 0.5f), new Vector3(m, m, m), new Color(0.8f, 0f, 0f, 1f));
        _mesh.SurfaceEnd();
    }

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

    private static Vector3 ToSauer(Vector3 g) => new(g.X, -g.Z, g.Y);
    private static Vector3 ToSauerDir(Vector3 g) => new(g.X, -g.Z, g.Y);
}
