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
        DrawBox();
    }

    // ---- input (prototype wheel dispatch) ----

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Octa == null || @event is not InputEventMouseButton mb || !mb.Pressed) return;

        bool shift = Input.IsKeyPressed(Key.Shift);
        switch (mb.ButtonIndex)
        {
            // Plain wheel: fill/empty (mpeditface mode 1). Shift+wheel: grid size.
            case MouseButton.WheelUp:
                if (shift) SetGrid(Cursor.GridPower + 1);
                else EditFill(+1);
                break;
            case MouseButton.WheelDown:
                if (shift) SetGrid(Cursor.GridPower - 1);
                else EditFill(-1);
                break;
        }
    }

    /// <summary>Adjust the grid power and rebuild the cursor grid.</summary>
    public void SetGrid(int power)
    {
        if (Octa == null) return;
        Cursor.SetGridPower(power, Octa.WorldSize);
    }

    /// <summary>Run a fill/empty edit (mpeditface mode 1) at the cursor selection.</summary>
    public void EditFill(int dir)
    {
        if (Sink == null || Octa == null) return;
        var sel = Cursor.Selection;
        sel.Grid = Cursor.GridSize;
        sel.Orient = Cursor.Orient;
        if (sel.IsZero)
        {
            // No selection yet: edit the single cursor cell.
            sel.O = Cursor.Cur;
            sel.S = new Vector3I(1, 1, 1);
        }
        var payload = new FillPayload(
            new SelectionPayload(sel), dir, 1);
        Sink.Submit(new WorldCommand(
            WorldOp.Fill, payload,
            source: CommandSource.Editor));

        // The command changed the octree; re-mesh the affected region. Fill mutates a
        // single grid slice, so invalidate the selection AABB expanded by one cell.
        if (Renderer != null)
        {
            var o = new Vector3(sel.O.X, sel.O.Y, sel.O.Z);
            var size = new Vector3(sel.S.X * sel.Grid, sel.S.Y * sel.Grid, sel.S.Z * sel.Grid);
            Renderer.MarkDirty(new Aabb(o, size), margin: sel.Grid);
        }
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
        EmitBox(o, s, new Color(1f, 1f, 1f, 1f));
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
        {
            _mesh!.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(p[edges[e, 0]]);
            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(p[edges[e, 1]]);
        }
    }

    // ---- Sauerbraten (Z-up) <-> Godot (Y-up) ----

    private static Vector3 ToSauer(Vector3 g) => new(g.X, -g.Z, g.Y);
    private static Vector3 ToSauerDir(Vector3 g) => new(g.X, -g.Z, g.Y);
}
