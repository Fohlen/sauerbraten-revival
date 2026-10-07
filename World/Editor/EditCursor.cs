using Godot;
using SauerbratenRevival.World;
using SauerbratenRevival.World.Commands;

namespace SauerbratenRevival.World.Editor;

/// <summary>
/// The local editor cursor and selection — a port of the cursor half of
/// <c>rendereditcursor</c> (src/engine/octaedit.cpp) plus <c>normalizelookupcube</c>.
///
/// This is **client-local state**: it is never replicated and never enters the
/// command log (docs/editor-and-commands.md §1.1). The <see cref="Selection"/> it
/// owns is only *sent* by riding on an edit command's payload.
///
/// The cursor resolves an eye ray to a grid-aligned cube (<see cref="Lookup"/>),
/// the face orientation it struck (<see cref="Orient"/>), and the selection in the
/// oriented plane. Rendering the box is separate (M4d).
/// </summary>
public sealed class EditCursor
{
    /// <summary>Grid size in world units (power of two, 8 … worldsize/2).</summary>
    public int GridSize { get; private set; } = 8;

    /// <summary>The normalized lookup cube the cursor points at (origin + size).</summary>
    public Vector3I Lu { get; private set; }

    /// <summary>Size of <see cref="Lu"/> (a power of two ≥ <see cref="GridSize"/>).</summary>
    public int LuSize { get; private set; } = 8;

    /// <summary>The current cursor grid cell (origin, grid-aligned).</summary>
    public Vector3I Cur { get; private set; }

    /// <summary>Cursor sub-grid coordinate: <c>floor(worldPos * 2 / gridsize)</c>.</summary>
    public Vector3I Cor { get; private set; }

    /// <summary>The orientation (0..5) of the face the cursor ray last struck.</summary>
    public int Orient { get; private set; }

    /// <summary>Whether a selection exists (the white box).</summary>
    public bool HasSelection { get; private set; }

    /// <summary>The local selection (client-local; see class remarks).</summary>
    public Selection Selection { get; } = new();

    /// <summary>The grid power (<c>gridsize = 1 &lt;&lt; GridPower</c>), clamped to the world.</summary>
    public int GridPower { get; private set; } = 3;

    /// <summary>Drag state (mouse held); mirrors the original's <c>dragging</c>.</summary>
    public bool Dragging { get; set; }

    /// <summary>Corner-exact drag mode (mirrors <c>selectcorners</c>).</summary>
    public bool SelectCorners { get; set; }

    private Vector3I _lastCur;
    private Vector3I _lastCor;

    /// <summary>Test seam: place the cursor without a ray cast (the ray needs M4d).</summary>
    internal void SetCursorForTest(Vector3I cur, int orient, int gridSize)
    {
        Cur = cur;
        _lastCur = cur;
        Orient = orient;
        GridSize = gridSize;
        GridPower = System.Math.ILogB((uint)gridSize);
    }

    /// <summary>octaedit.cpp:141 <c>gridpower</c> — set the grid; clamped to the world; cancels the selection.</summary>
    public void SetGridPower(int power, int worldSize)
    {
        GridPower = Mathf.Clamp(power, 0, 12);
        GridSize = 1 << GridPower;
        if (GridSize >= worldSize) GridSize = worldSize / 2;
        CancelSelection();
    }

    /// <summary>octaedit.cpp:323 <c>normalizelookupcube(o)</c> — snap <see cref="Lu"/> to the grid.</summary>
    internal void NormalizeLookupCube(Vector3I o)
    {
        if (LuSize > GridSize)
        {
            Lu = new Vector3I(
                Lu.X + (o.X - Lu.X) / GridSize * GridSize,
                Lu.Y + (o.Y - Lu.Y) / GridSize * GridSize,
                Lu.Z + (o.Z - Lu.Z) / GridSize * GridSize);
        }
        else if (GridSize > LuSize)
        {
            Lu = new Vector3I(Lu.X & ~(GridSize - 1), Lu.Y & ~(GridSize - 1), Lu.Z & ~(GridSize - 1));
        }
        LuSize = GridSize;
    }

    /// <summary>Pure form of <c>normalizelookupcube</c> for tests (docs use <c>lusize</c>/<c>gridsize</c>).</summary>
    internal static (Vector3I Lu, int LuSize) NormalizePure(Vector3I lu, int luSize, Vector3I o, int gridSize)
    {
        if (luSize > gridSize)
        {
            lu = new Vector3I(
                lu.X + (o.X - lu.X) / gridSize * gridSize,
                lu.Y + (o.Y - lu.Y) / gridSize * gridSize,
                lu.Z + (o.Z - lu.Z) / gridSize * gridSize);
        }
        else if (gridSize > luSize)
        {
            lu = new Vector3I(lu.X & ~(gridSize - 1), lu.Y & ~(gridSize - 1), lu.Z & ~(gridSize - 1));
        }
        return (lu, gridSize);
    }

    /// <summary>octaedit.cpp:144 <c>cubecancel</c> — clear the selection and reset drag state.</summary>
    public void CancelSelection()
    {
        HasSelection = false;
        Selection.S = Vector3I.Zero;
        Dragging = false;
    }

    // ---- M4b: grid / orient / selection commands (all client-local, no world mutation) ----

    /// <summary>
    /// octaedit.cpp:204 <c>reorient</c> — set the selection's oriented axis to the
    /// face the cursor points at (<paramref name="orient"/> defaults to the current
    /// <see cref="Orient"/>). Delegates to <see cref="Selection.Reorient"/>.
    /// </summary>
    public void Reorient(int? orient = null) => Selection.Reorient(orient ?? Orient);

    /// <summary>
    /// octaedit.cpp:210 <c>selextend</c> — grow the selection to include the cursor
    /// cell <see cref="Cur"/>. Delegates to <see cref="Selection.Extend"/>.
    /// </summary>
    public void ExtendSelection()
    {
        Selection.Extend(Cur);
        HasSelection = true;
    }

    /// <summary>octaedit.cpp:272 <c>movesel</c> — step the selection origin by one grid cell along an axis.</summary>
    public void MoveSelection(int dir, int axis)
    {
        Selection.Move(dir, axis);
        HasSelection = true;
    }

    /// <summary>
    /// octaedit.cpp:267 <c>pushsel</c> — move the selection one grid cell along the
    /// oriented axis, in the direction of the pointed face.
    /// </summary>
    public void MoveSelectionOriented(int dir)
    {
        int d = Selection.Dimension(Orient);
        int s = Selection.DimCoord(Orient) != 0 ? -dir : dir;
        Selection.O = SetAxis(Selection.O, d, Selection.O[d] + s * Selection.Grid);
        HasSelection = true;
    }

    /// <summary>octaedit.cpp:264 <c>setselpos</c> — move the selection origin, grid-masked. Size unchanged.</summary>
    public void SetSelectionPos(Vector3I pos)
    {
        Selection.SetPos(pos, GridSize);
        HasSelection = true;
    }

    private static Vector3I SetAxis(Vector3I v, int d, int value) => d switch
    {
        0 => new Vector3I(value, v.Y, v.Z),
        1 => new Vector3I(v.X, value, v.Z),
        _ => new Vector3I(v.X, v.Y, value),
    };

    /// <summary>
    /// The cursor-cast half of <c>rendereditcursor</c>: given the eye and a
    /// normalized look ray, resolve the pointed-at cube, orientation, and cursor
    /// cell, and (when not dragging) keep the selection anchored to the cursor.
    /// <paramref name="rayDist"/> is how far the ray reached before hitting
    /// something solid (world ray cast), or a large value for open air.
    /// </summary>
    public void Update(Octa octa, Vector3 eye, Vector3 ray, float rayDist)
    {
        // World point the ray reaches (nudged back and forward around the hit).
        Vector3 w = ray * (rayDist + 0.05f) + eye;
        if (!InsideWorld(octa, w))
        {
            float clamped = rayDist;
            for (int i = 0; i < 3; i++)
            {
                float rc = Component(ray, i);
                if (rc == 0f) continue;
                float bound = (rc > 0f ? octa.WorldSize : 0) - Component(eye, i);
                clamped = Mathf.Min(clamped, bound / rc);
            }
            w = ray * (clamped - 0.05f) + eye;
            if (!InsideWorld(octa, w))
            {
                w = new Vector3(
                    Mathf.Clamp(eye.X, 0, octa.WorldSize),
                    Mathf.Clamp(eye.Y, 0, octa.WorldSize),
                    Mathf.Clamp(eye.Z, 0, octa.WorldSize));
            }
        }

        Cube c = OctaEdit.LookupCube(octa, new Vector3I((int)w.X, (int)w.Y, (int)w.Z), 0, out Vector3I lu, out int luSize);
        Lu = lu;
        LuSize = luSize;

        // Orientation: intersect the ray with the grid-sized box at the lookup cube.
        if (CursorGeometry.RayBoxIntersect(
                new Vector3(lu.X, lu.Y, lu.Z),
                new Vector3(GridSize, GridSize, GridSize),
                eye, ray, out _, out int orient))
        {
            Orient = orient;
        }

        NormalizeLookupCube(new Vector3I((int)w.X, (int)w.Y, (int)w.Z));
        Cur = Lu;
        Cor = new Vector3I(
            (int)(w.X * 2 / GridSize),
            (int)(w.Y * 2 / GridSize),
            (int)(w.Z * 2 / GridSize));

        if (Dragging)
        {
            UpdateSelection();
            int d = Selection.Dimension(Selection.Orient);
            int r = Octa.R[d], cc = Octa.C[d];
            Selection.Cx = Mathf.Min(Cor[r], _lastCor[r]);
            Selection.Cy = Mathf.Min(Cor[cc], _lastCor[cc]);
            Selection.Cxs = Mathf.Max(Cor[r], _lastCor[r]);
            Selection.Cys = Mathf.Max(Cor[cc], _lastCor[cc]);

            if (!SelectCorners)
            {
                Selection.Cx &= ~1; Selection.Cy &= ~1; Selection.Cxs &= ~1; Selection.Cys &= ~1;
                Selection.Cxs -= Selection.Cx - 2;
                Selection.Cys -= Selection.Cy - 2;
            }
            else
            {
                Selection.Cxs -= Selection.Cx - 1;
                Selection.Cys -= Selection.Cy - 1;
            }
            Selection.Cx &= 1; Selection.Cy &= 1;
            HasSelection = true;
        }
        else if (!HasSelection)
        {
            Selection.O = Lu;
            Selection.S = new Vector3I(1, 1, 1);
            Selection.Cx = Selection.Cy = 0;
            Selection.Cxs = Selection.Cys = 2;
            Selection.Grid = GridSize;
            Selection.Orient = Orient;
        }

        int sd = Selection.Dimension(Selection.Orient);
        int sR = Octa.R[sd], sC = Octa.C[sd];
        Selection.Corner = (Cor[sR] - (Lu[sR] * 2) / GridSize) + (Cor[sC] - (Lu[sC] * 2) / GridSize) * 2;

        _lastCur = Cur;
        _lastCor = Cor;
    }

    /// <summary>octaedit.cpp:329 <c>updateselection</c> — grow the selection to the cursor span.</summary>
    private void UpdateSelection()
    {
        Selection.O = new Vector3I(
            Mathf.Min(_lastCur.X, Cur.X),
            Mathf.Min(_lastCur.Y, Cur.Y),
            Mathf.Min(_lastCur.Z, Cur.Z));
        Selection.S = new Vector3I(
            Mathf.Abs(_lastCur.X - Cur.X) / Selection.Grid + 1,
            Mathf.Abs(_lastCur.Y - Cur.Y) / Selection.Grid + 1,
            Mathf.Abs(_lastCur.Z - Cur.Z) / Selection.Grid + 1);
    }

    /// <summary>Set the selection anchor for a drag (call when a drag begins).</summary>
    public void BeginDrag()
    {
        _lastCur = Cur;
        _lastCor = Cor;
        Selection.Grid = GridSize;
        Selection.Orient = Orient;
        Dragging = true;
    }

    private static bool InsideWorld(Octa octa, Vector3 w) =>
        w.X >= 0 && w.X < octa.WorldSize && w.Y >= 0 && w.Y < octa.WorldSize && w.Z >= 0 && w.Z < octa.WorldSize;

    private static float Component(Vector3 v, int i) => i switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };
}
