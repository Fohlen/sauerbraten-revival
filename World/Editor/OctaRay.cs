using Godot;

namespace SauerbratenRevival.World.Editor;

/// <summary>
/// A world ray cast against the octree: from an eye point along a direction, find
/// the distance to the first solid cube (or the max distance for open air). This is
/// the editor's <c>raycube</c>-equivalent used to place the cursor.
///
/// **Status: prototype.** A leaf-stepping DDA: at each step it looks up the leaf
/// containing the ray and advances to that leaf's boundary, so it visits O(leaves
/// along the ray) rather than O(units). (An earlier unit-stepping version hammered
/// the CPU in open air — thousands of leaf lookups per frame.) The original's
/// <c>physics.cpp:raycube</c> descends the octree hierarchically and clips deformed
/// cubes with their planes; it also drives collision. That exact port is deferred
/// (it belongs with collision), so this caster is the seam a real one will replace.
///
/// Coordinates are Sauerbraten (Z-up) units.
/// </summary>
public static class OctaRay
{
    /// <summary>
    /// Cast from <paramref name="o"/> along <paramref name="dir"/> to the first
    /// solid cube. Returns the hit distance, or <paramref name="maxDist"/> if
    /// nothing was hit within range. <paramref name="orient"/> receives the struck
    /// face orientation (or -1 if no hit).
    /// </summary>
    public static float RayCube(Octa octa, Vector3 o, Vector3 dir, float maxDist, out int orient)
    {
        orient = -1;
        if (dir.LengthSquared() < 1e-12f) return maxDist;

        var cell = Floor(o);
        if (Solid(octa, cell)) return 0f; // eye starts inside solid

        // A leaf's boundary is at its grid-aligned origin + size. Looking up the leaf
        // origin (not the raw cell) is what makes the step advance a whole leaf at a
        // time rather than jittering by one unit inside a large leaf.
        float t = 0f;
        for (int guard = 0; guard < 4096; guard++)
        {
            Cube? leaf = octa.InsideWorld(cell) ? octa.LeafCube(cell, out _) : null;
            var leafOrigin = LeafOrigin(octa, cell);
            int size = LeafSize(octa, cell);

            int stepX = dir.X > 0 ? 1 : dir.X < 0 ? -1 : 0;
            int stepY = dir.Y > 0 ? 1 : dir.Y < 0 ? -1 : 0;
            int stepZ = dir.Z > 0 ? 1 : dir.Z < 0 ? -1 : 0;

            float tX = Boundary(Component(o, 0) + dir.X * t, dir.X, stepX, leafOrigin.X, size);
            float tY = Boundary(Component(o, 1) + dir.Y * t, dir.Y, stepY, leafOrigin.Y, size);
            float tZ = Boundary(Component(o, 2) + dir.Z * t, dir.Z, stepZ, leafOrigin.Z, size);

            int axis; float dt;
            if (tX <= tY && tX <= tZ) { axis = 0; dt = tX; }
            else if (tY <= tZ) { axis = 1; dt = tY; }
            else { axis = 2; dt = tZ; }

            if (dt <= 0f) dt = float.Epsilon; // guard against exact-boundary stalls
            t += dt;
            if (t > maxDist) break;

            switch (axis)
            {
                case 0: cell.X = stepX > 0 ? leafOrigin.X + size : leafOrigin.X - 1; break;
                case 1: cell.Y = stepY > 0 ? leafOrigin.Y + size : leafOrigin.Y - 1; break;
                default: cell.Z = stepZ > 0 ? leafOrigin.Z + size : leafOrigin.Z - 1; break;
            }

            // Left the world: nothing more to hit out here.
            if (!octa.InsideWorld(cell)) break;

            if (Solid(octa, cell))
            {
                int step = axis == 0 ? stepX : axis == 1 ? stepY : stepZ;
                orient = 2 * axis + (step > 0 ? 0 : 1);
                return t;
            }
        }

        return maxDist;
    }

    /// <summary>Grid-aligned origin of the leaf containing <paramref name="cell"/> (or the cell itself if outside).</summary>
    private static Vector3I LeafOrigin(Octa octa, Vector3I cell)
    {
        if (!octa.InsideWorld(cell)) return cell;
        octa.LeafCube(cell, out int size);
        int mask = ~(Mathf.Max(size, 1) - 1);
        return new Vector3I(cell.X & mask, cell.Y & mask, cell.Z & mask);
    }

    /// <summary>
    /// Distance along one axis from position <paramref name="p"/> to the next leaf
    /// boundary in the step direction. The leaf spans <c>[cellCoord, cellCoord+size)</c>.
    /// </summary>
    private static float Boundary(float p, float dir, int step, int cellCoord, int size)
    {
        if (dir == 0f || step == 0) return float.PositiveInfinity;
        float boundary = step > 0 ? cellCoord + size : cellCoord;
        return (boundary - p) / dir;
    }

    /// <summary>Size of the leaf containing <paramref name="cell"/>, or 1 if outside the world.</summary>
    private static int LeafSize(Octa octa, Vector3I cell)
    {
        if (!octa.InsideWorld(cell)) return 1;
        octa.LeafCube(cell, out int leafSize);
        return Mathf.Max(leafSize, 1);
    }

    private static bool Solid(Octa octa, Vector3I cell)
    {
        if (!octa.InsideWorld(cell)) return false;
        Cube? c = octa.LeafCube(cell);
        return c != null && !c.IsEmpty();
    }

    private static Vector3I Floor(Vector3 v) => new(
        Mathf.FloorToInt(v.X), Mathf.FloorToInt(v.Y), Mathf.FloorToInt(v.Z));

    private static float Component(Vector3 v, int i) => i switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}
