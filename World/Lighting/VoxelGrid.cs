using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// Uniform 3D occupancy and diffuse albedo grid (<c>RGBA8</c>, default 64³ = 1 MB)
/// extracted from the runtime <see cref="Octa"/> and <see cref="MetadataCatalogue"/>
/// to accelerate GPU compute radiance hints raymarching (docs/adr/0002-radiance-hints.md).
///
/// Each texel represents a cubic world cell in Godot world space (matching <see cref="RadianceGrid"/> bounds):
/// <list type="bullet">
///   <item><description><c>RGB</c>: average diffuse albedo × vslot tint (0..255 = 0.0..1.0)</description></item>
///   <item><description><c>A</c>: opacity / occupancy (<see cref="AlphaEmpty"/> = 0, <see cref="AlphaPartial"/> = 128, <see cref="AlphaSolid"/> = 255)</description></item>
/// </list>
/// </summary>
public sealed class VoxelGrid
{
    /// <summary>Default voxel grid resolution per axis (64³ = 262,144 voxels = 1 MB).</summary>
    public const int DefaultResolution = 64;

    /// <summary>Alpha byte for empty air (0.0).</summary>
    public const byte AlphaEmpty = 0;

    /// <summary>Alpha byte for partially solid / sloped geometry (≈0.5).</summary>
    public const byte AlphaPartial = 128;

    /// <summary>Alpha byte for entirely solid geometry (1.0).</summary>
    public const byte AlphaSolid = 255;

    /// <summary>Default neutral diffuse albedo byte (128 ≈ 0.5).</summary>
    public const byte DefaultAlbedoByte = 128;

    /// <summary>Packed RGBA8 uint for an empty air voxel (0, 0, 0, 0).</summary>
    public const uint PackedEmpty = 0u;

    /// <summary>Number of voxels per axis (cubic: Resolution × Resolution × Resolution).</summary>
    public int Resolution { get; }

    /// <summary>Minimum corner of the grid in Godot world space.</summary>
    public Vector3 Origin { get; }

    /// <summary>Edge length of the cubic grid in Godot world space.</summary>
    public float Extent { get; }

    /// <summary>World-space edge length of a single voxel cell.</summary>
    public float CellSize => Extent / Resolution;

    /// <summary>Total number of voxels in the grid (<see cref="Resolution"/>³).</summary>
    public int TotalVoxels => Resolution * Resolution * Resolution;

    /// <summary>Godot world-space bounding box enclosing the grid.</summary>
    public Aabb Bounds => new(Origin, new Vector3(Extent, Extent, Extent));

    /// <summary>Sauerbraten-space bounding box enclosing the grid.</summary>
    public Aabb SauerBounds { get; }

    private readonly byte[] _data;

    /// <summary>
    /// Raw RGBA8 byte buffer (<c>Resolution³ * 4</c> bytes).
    /// Layout is row-major in Godot world space: X changes fastest, then Y, then Z,
    /// matching Godot 3D texture data layout for direct <see cref="RenderingDevice"/> upload.
    /// </summary>
    public byte[] Data => _data;

    public VoxelGrid(Vector3 origin, float extent, int resolution = DefaultResolution)
    {
        if (resolution <= 0) throw new ArgumentOutOfRangeException(nameof(resolution), "Resolution must be positive");
        if (extent <= 0f) throw new ArgumentOutOfRangeException(nameof(extent), "Extent must be positive");

        Origin = origin;
        Extent = extent;
        Resolution = resolution;
        SauerBounds = RadianceGrid.WorldBoundsToSauer(Bounds);
        _data = new byte[resolution * resolution * resolution * 4];
    }

    /// <summary>
    /// Create a <see cref="VoxelGrid"/> aligned to the exact world-space cubic bounds of a <see cref="RadianceGrid"/>.
    /// </summary>
    public static VoxelGrid FromRadianceGrid(RadianceGrid radianceGrid, int resolution = DefaultResolution)
    {
        ArgumentNullException.ThrowIfNull(radianceGrid);
        return new VoxelGrid(radianceGrid.Origin, radianceGrid.Extent, resolution);
    }

    /// <summary>
    /// Create and extract a <see cref="VoxelGrid"/> spanning the non-empty geometry of <paramref name="octa"/>.
    /// Uses the same margin calculation as <see cref="RadianceGrid.FromOcta"/> so bounds match.
    /// </summary>
    public static VoxelGrid CreateAndExtract(
        Octa octa,
        MetadataCatalogue? catalogue = null,
        ITextureSource? textureSource = null,
        int resolution = DefaultResolution,
        int probeResolution = RadianceGrid.DefaultResolution,
        int marginCells = 1)
    {
        var radGrid = RadianceGrid.FromOcta(octa, probeResolution, marginCells);
        var voxelGrid = FromRadianceGrid(radGrid, resolution);
        voxelGrid.Extract(octa, catalogue, textureSource);
        return voxelGrid;
    }

    // =========================================================================
    // Indexing & Coordinate Mapping
    // =========================================================================

    /// <summary>Linear voxel index (0 .. TotalVoxels - 1) for Godot voxel coordinates (x, y, z).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Index(int x, int y, int z) => x + Resolution * (y + Resolution * z);

    /// <summary>Byte offset into <see cref="Data"/> for Godot voxel coordinates (x, y, z).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ByteOffset(int x, int y, int z) => Index(x, y, z) * 4;

    /// <summary>True if the voxel coordinates are within [0, Resolution - 1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsInside(int x, int y, int z) =>
        (uint)x < (uint)Resolution && (uint)y < (uint)Resolution && (uint)z < (uint)Resolution;

    /// <summary>True if the voxel coordinates are within [0, Resolution - 1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsInside(Vector3I cell) => IsInside(cell.X, cell.Y, cell.Z);

    /// <summary>Center of voxel (x, y, z) in Godot world coordinates.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 CellCenterWorld(int x, int y, int z) =>
        Origin + new Vector3((x + 0.5f) * CellSize, (y + 0.5f) * CellSize, (z + 0.5f) * CellSize);

    /// <summary>Center of voxel (x, y, z) in Sauerbraten coordinates.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 CellCenterSauer(int x, int y, int z) =>
        RadianceGrid.WorldToSauer(CellCenterWorld(x, y, z));

    /// <summary>Map a Godot world position to the discrete voxel coordinate enclosing it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3I WorldToCell(Vector3 worldPos)
    {
        Vector3 offset = (worldPos - Origin) / CellSize;
        return new Vector3I(
            Mathf.FloorToInt(offset.X),
            Mathf.FloorToInt(offset.Y),
            Mathf.FloorToInt(offset.Z));
    }

    /// <summary>Map a Sauerbraten position to the discrete Godot voxel coordinate enclosing it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3I SauerToCell(Vector3 sauerPos) =>
        WorldToCell(RadianceGrid.SauerToWorld(sauerPos));

    /// <summary>Pack RGBA8 bytes into a little-endian uint32 (R in lowest byte, A in highest byte).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint PackRgba8(byte r, byte g, byte b, byte a) =>
        r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

    /// <summary>Unpack a little-endian uint32 into RGBA8 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (byte R, byte G, byte B, byte A) UnpackRgba8(uint packed) =>
        ((byte)(packed & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)((packed >> 16) & 0xFF), (byte)((packed >> 24) & 0xFF));

    /// <summary>Get the packed RGBA8 uint32 at Godot voxel coordinate (x, y, z).</summary>
    public uint GetPacked(int x, int y, int z)
    {
        Span<uint> uints = MemoryMarshal.Cast<byte, uint>(_data.AsSpan());
        return uints[Index(x, y, z)];
    }

    /// <summary>Get the unpacked RGBA8 bytes at Godot voxel coordinate (x, y, z).</summary>
    public (byte R, byte G, byte B, byte A) GetRgba(int x, int y, int z) =>
        UnpackRgba8(GetPacked(x, y, z));

    /// <summary>Get the normalized opacity/occupancy in [0, 1] at Godot voxel coordinate (x, y, z).</summary>
    public float GetOccupancy(int x, int y, int z) =>
        _data[ByteOffset(x, y, z) + 3] / 255.0f;

    /// <summary>Get the normalized diffuse albedo Color (RGB in [0, 1], A = occupancy) at Godot voxel coordinate (x, y, z).</summary>
    public Color GetColor(int x, int y, int z)
    {
        int offset = ByteOffset(x, y, z);
        return new Color(
            _data[offset] / 255.0f,
            _data[offset + 1] / 255.0f,
            _data[offset + 2] / 255.0f,
            _data[offset + 3] / 255.0f);
    }

    /// <summary>Get the normalized diffuse albedo Color at a Sauerbraten world position (or transparent if outside bounds).</summary>
    public Color SampleSauer(Vector3 sauerPos)
    {
        Vector3I cell = SauerToCell(sauerPos);
        if (!IsInside(cell)) return new Color(0f, 0f, 0f, 0f);
        return GetColor(cell.X, cell.Y, cell.Z);
    }

    /// <summary>Clear the entire voxel buffer to empty air (all zeros).</summary>
    public void Clear() => Array.Clear(_data, 0, _data.Length);

    // =========================================================================
    // Fast Octree Extraction
    // =========================================================================

    private MetadataCatalogue? _cachedCatalogue;
    private ITextureSource? _cachedTextureSource;
    private uint[]? _vslotPalette;
    private int[]? _voxMin;
    private int[]? _voxMax;
    private int[]? _centerCoords;

    /// <summary>
    /// Extract the full 3D occupancy and diffuse albedo grid from <paramref name="octa"/>.
    /// Walks the octree hierarchically and fills solid blocks directly in the destination buffer.
    /// </summary>
    public void Extract(
        Octa octa,
        MetadataCatalogue? catalogue = null,
        ITextureSource? textureSource = null)
    {
        ArgumentNullException.ThrowIfNull(octa);

        Clear();
        if (_vslotPalette == null || !ReferenceEquals(catalogue, _cachedCatalogue) || !ReferenceEquals(textureSource, _cachedTextureSource))
        {
            _vslotPalette = BuildVSlotPalette(catalogue, textureSource);
            _cachedCatalogue = catalogue;
            _cachedTextureSource = textureSource;
        }

        EnsureCoordTables(octa.WorldSize);

        Span<uint> uintData = MemoryMarshal.Cast<byte, uint>(_data.AsSpan());

        WalkNode(
            octa.WorldRoot,
            0, 0, 0,
            octa.WorldSize >> 1,
            Resolution,
            _voxMin!,
            _voxMax!,
            _centerCoords!,
            uintData,
            _vslotPalette);
    }

    private void EnsureCoordTables(int worldSize)
    {
        int tableLen = (worldSize + 1) * 3;
        if (_voxMin != null && _voxMin.Length == tableLen && _centerCoords != null && _centerCoords.Length == Resolution * 3)
            return;

        _voxMin = new int[tableLen];
        _voxMax = new int[tableLen];
        _centerCoords = new int[Resolution * 3];

        float cellSize = CellSize;
        float invCell = 1.0f / cellSize;
        Vector3 sMin = SauerBounds.Position;
        int res = Resolution;

        int stride = worldSize + 1;
        for (int axis = 0; axis < 3; axis++)
        {
            float axisMin = sMin[axis];
            int baseIdx = axis * stride;
            for (int c = 0; c <= worldSize; c++)
            {
                int ceilVal = (int)MathF.Ceiling((c - axisMin) * invCell - 0.5f);
                _voxMin[baseIdx + c] = Math.Max(0, ceilVal);
                _voxMax[baseIdx + c] = Math.Min(res - 1, ceilVal - 1);
            }

            int centerBase = axis * res;
            for (int v = 0; v < res; v++)
            {
                _centerCoords[centerBase + v] = (int)MathF.Floor(axisMin + (v + 0.5f) * cellSize);
            }
        }
    }

    /// <summary>
    /// Re-extract only the sub-region of the voxel grid overlapping <paramref name="dirtySauerRegion"/>.
    /// Returns the inclusive Godot voxel coordinate range <c>(Min, Max)</c> that was updated.
    /// </summary>
    public (Vector3I Min, Vector3I Max) ExtractRegion(
        Octa octa,
        Aabb dirtySauerRegion,
        MetadataCatalogue? catalogue = null,
        ITextureSource? textureSource = null)
    {
        ArgumentNullException.ThrowIfNull(octa);

        if (_vslotPalette == null && catalogue != null)
            _vslotPalette = BuildVSlotPalette(catalogue, textureSource);

        uint[] palette = _vslotPalette ?? DefaultPalette;

        Aabb worldRegion = RadianceGrid.SauerBoundsToWorld(dirtySauerRegion);
        Vector3I c0 = WorldToCell(worldRegion.Position);
        Vector3I c1 = WorldToCell(worldRegion.End);

        int res = Resolution;
        int gx0 = Math.Clamp(Math.Min(c0.X, c1.X), 0, res - 1);
        int gy0 = Math.Clamp(Math.Min(c0.Y, c1.Y), 0, res - 1);
        int gz0 = Math.Clamp(Math.Min(c0.Z, c1.Z), 0, res - 1);
        int gx1 = Math.Clamp(Math.Max(c0.X, c1.X), 0, res - 1);
        int gy1 = Math.Clamp(Math.Max(c0.Y, c1.Y), 0, res - 1);
        int gz1 = Math.Clamp(Math.Max(c0.Z, c1.Z), 0, res - 1);

        Span<uint> uintData = MemoryMarshal.Cast<byte, uint>(_data.AsSpan());

        // Clear the sub-box first
        int countX = gx1 - gx0 + 1;
        for (int gz = gz0; gz <= gz1; gz++)
        {
            int zBase = gz * res;
            for (int gy = gy0; gy <= gy1; gy++)
            {
                int rowStart = gx0 + res * (gy + zBase);
                uintData.Slice(rowStart, countX).Clear();
            }
        }

        // Convert clamped Godot voxel sub-box back to Sauerbraten bounds for octree culling
        Vector3 subWorldMin = Origin + new Vector3(gx0 * CellSize, gy0 * CellSize, gz0 * CellSize);
        Vector3 subWorldMax = Origin + new Vector3((gx1 + 1) * CellSize, (gy1 + 1) * CellSize, (gz1 + 1) * CellSize);
        Aabb subSauerBounds = RadianceGrid.WorldBoundsToSauer(new Aabb(subWorldMin, subWorldMax - subWorldMin));

        float invCell = 1.0f / CellSize;
        Vector3 sMin = SauerBounds.Position;

        WalkNodeBounded(
            octa.WorldRoot,
            0, 0, 0,
            octa.WorldSize >> 1,
            sMin.X, sMin.Y, sMin.Z,
            invCell,
            res,
            gx0, gy0, gz0,
            gx1, gy1, gz1,
            subSauerBounds,
            uintData,
            palette);

        return (new Vector3I(gx0, gy0, gz0), new Vector3I(gx1, gy1, gz1));
    }

    private static void WalkNode(
        Cube[] cubes,
        int x, int y, int z,
        int size,
        int res,
        int[] voxMin,
        int[] voxMax,
        int[] centerCoords,
        Span<uint> uintData,
        uint[] palette)
    {
        int stride = voxMin.Length / 3;
        int strideY = stride;
        int strideZ = stride * 2;
        int centerY = res;
        int centerZ = res * 2;

        for (int i = 0; i < 8; i++)
        {
            Cube c = cubes[i];
            int cx = x + ((i & 1) != 0 ? size : 0);
            int cy = y + ((i & 2) != 0 ? size : 0);
            int cz = z + ((i & 4) != 0 ? size : 0);

            int vx0 = voxMin[cx];
            int vx1 = voxMax[cx + size];
            if (vx0 > vx1) continue;

            int vy0 = voxMin[strideY + cy];
            int vy1 = voxMax[strideY + cy + size];
            if (vy0 > vy1) continue;

            int vz0 = voxMin[strideZ + cz];
            int vz1 = voxMax[strideZ + cz + size];
            if (vz0 > vz1) continue;

            if (c.Children != null)
            {
                // When an octant covers at most 2 voxel centers per axis, point-query the covered
                // voxel centers directly instead of recursively walking all 8..4096 sub-cubes!
                if (vx1 - vx0 <= 1 && vy1 - vy0 <= 1 && vz1 - vz0 <= 1)
                {
                    for (int vz = vz0; vz <= vz1; vz++)
                    {
                        int pz = centerCoords[centerZ + vz];
                        int gy = vz;
                        for (int vy = vy0; vy <= vy1; vy++)
                        {
                            int py = centerCoords[centerY + vy];
                            int gz = (res - 1) - vy;
                            int rowOffset = res * (gy + res * gz);
                            for (int vx = vx0; vx <= vx1; vx++)
                            {
                                int px = centerCoords[vx];
                                Cube leaf = c;
                                int subSize = size >> 1;
                                while (leaf.Children != null)
                                {
                                    int step = (((pz & subSize) != 0 ? 4 : 0) | ((py & subSize) != 0 ? 2 : 0) | ((px & subSize) != 0 ? 1 : 0));
                                    leaf = leaf.Children[step];
                                    subSize >>= 1;
                                }
                                if (leaf.IsEmpty()) continue;

                                byte subAlpha = leaf.IsEntirelySolid() ? AlphaSolid : AlphaPartial;
                                uintData[vx + rowOffset] = ComputeCubePackedColor(leaf, subAlpha, palette);
                            }
                        }
                    }
                    continue;
                }

                WalkNode(
                    c.Children, cx, cy, cz, size >> 1,
                    res, voxMin, voxMax, centerCoords, uintData, palette);
                continue;
            }

            if (c.IsEmpty())
                continue;

            int gx0 = vx0;
            int gx1 = vx1;
            int gy0 = vz0;
            int gy1 = vz1;
            int gz0 = (res - 1) - vy1;
            int gz1 = (res - 1) - vy0;

            bool isSolid = c.IsEntirelySolid();
            byte alpha = isSolid ? AlphaSolid : AlphaPartial;
            uint packed = ComputeCubePackedColor(c, alpha, palette);

            int countX = gx1 - gx0 + 1;
            for (int gz = gz0; gz <= gz1; gz++)
            {
                int zBase = gz * res;
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    int rowStart = gx0 + res * (gy + zBase);
                    uintData.Slice(rowStart, countX).Fill(packed);
                }
            }
        }
    }

    private static void WalkNodeBounded(
        Cube[] cubes,
        int x, int y, int z,
        int size,
        float sMinX, float sMinY, float sMinZ,
        float invCell,
        int res,
        int clampGx0, int clampGy0, int clampGz0,
        int clampGx1, int clampGy1, int clampGz1,
        Aabb subSauerBounds,
        Span<uint> uintData,
        uint[] palette)
    {
        Vector3 bMin = subSauerBounds.Position;
        Vector3 bMax = subSauerBounds.End;

        for (int i = 0; i < 8; i++)
        {
            Cube c = cubes[i];
            int cx = x + ((i & 1) != 0 ? size : 0);
            int cy = y + ((i & 2) != 0 ? size : 0);
            int cz = z + ((i & 4) != 0 ? size : 0);

            if (cx + size <= bMin.X || cx >= bMax.X ||
                cy + size <= bMin.Y || cy >= bMax.Y ||
                cz + size <= bMin.Z || cz >= bMax.Z)
            {
                continue;
            }

            if (c.Children != null)
            {
                int pvx0 = (int)MathF.Ceiling((cx - sMinX) * invCell - 0.5f);
                int pvx1 = (int)MathF.Ceiling((cx + size - sMinX) * invCell - 0.5f) - 1;
                if (pvx0 > pvx1) continue;

                int pvy0 = (int)MathF.Ceiling((cy - sMinY) * invCell - 0.5f);
                int pvy1 = (int)MathF.Ceiling((cy + size - sMinY) * invCell - 0.5f) - 1;
                if (pvy0 > pvy1) continue;

                int pvz0 = (int)MathF.Ceiling((cz - sMinZ) * invCell - 0.5f);
                int pvz1 = (int)MathF.Ceiling((cz + size - sMinZ) * invCell - 0.5f) - 1;
                if (pvz0 > pvz1) continue;

                WalkNodeBounded(
                    c.Children, cx, cy, cz, size >> 1,
                    sMinX, sMinY, sMinZ, invCell, res,
                    clampGx0, clampGy0, clampGz0,
                    clampGx1, clampGy1, clampGz1,
                    subSauerBounds, uintData, palette);
                continue;
            }

            if (c.IsEmpty())
                continue;

            int vx0 = Math.Max(0, (int)MathF.Ceiling((cx - sMinX) * invCell - 0.5f));
            int vx1 = Math.Min(res - 1, (int)MathF.Ceiling((cx + size - sMinX) * invCell - 0.5f) - 1);
            if (vx0 > vx1) continue;

            int vy0 = Math.Max(0, (int)MathF.Ceiling((cy - sMinY) * invCell - 0.5f));
            int vy1 = Math.Min(res - 1, (int)MathF.Ceiling((cy + size - sMinY) * invCell - 0.5f) - 1);
            if (vy0 > vy1) continue;

            int vz0 = Math.Max(0, (int)MathF.Ceiling((cz - sMinZ) * invCell - 0.5f));
            int vz1 = Math.Min(res - 1, (int)MathF.Ceiling((cz + size - sMinZ) * invCell - 0.5f) - 1);
            if (vz0 > vz1) continue;

            int gx0 = Math.Max(clampGx0, vx0);
            int gx1 = Math.Min(clampGx1, vx1);
            if (gx0 > gx1) continue;

            int gy0 = Math.Max(clampGy0, vz0);
            int gy1 = Math.Min(clampGy1, vz1);
            if (gy0 > gy1) continue;

            int gz0 = Math.Max(clampGz0, (res - 1) - vy1);
            int gz1 = Math.Min(clampGz1, (res - 1) - vy0);
            if (gz0 > gz1) continue;

            bool isSolid = c.IsEntirelySolid();
            byte alpha = isSolid ? AlphaSolid : AlphaPartial;
            uint packed = ComputeCubePackedColor(c, alpha, palette);

            int countX = gx1 - gx0 + 1;
            for (int gz = gz0; gz <= gz1; gz++)
            {
                int zBase = gz * res;
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    int rowStart = gx0 + res * (gy + zBase);
                    uintData.Slice(rowStart, countX).Fill(packed);
                }
            }
        }
    }

    /// <summary>
    /// Compute the packed RGBA8 color for a non-empty leaf cube by averaging the diffuse RGB
    /// of its non-sky face textures and combining with <paramref name="alpha"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeCubePackedColor(Cube c, byte alpha, uint[] palette)
    {
        // Fast path if all 6 faces share the same texture index
        ushort t0 = c.Texture.T0;
        if (t0 == c.Texture.T1 && t0 == c.Texture.T2 &&
            t0 == c.Texture.T3 && t0 == c.Texture.T4 && t0 == c.Texture.T5)
        {
            uint rgb = t0 < palette.Length
                ? palette[t0]
                : PackRgba8(DefaultAlbedoByte, DefaultAlbedoByte, DefaultAlbedoByte, 0);
            return rgb | ((uint)alpha << 24);
        }

        uint sumR = 0, sumG = 0, sumB = 0, count = 0;
        for (int orient = 0; orient < 6; orient++)
        {
            ushort tex = c.Texture[orient];
            if (tex == Octa.DEFAULT_SKY) continue;

            uint rgb = tex < palette.Length
                ? palette[tex]
                : PackRgba8(DefaultAlbedoByte, DefaultAlbedoByte, DefaultAlbedoByte, 0);
            sumR += rgb & 0xFF;
            sumG += (rgb >> 8) & 0xFF;
            sumB += (rgb >> 16) & 0xFF;
            count++;
        }

        if (count == 0)
        {
            return PackRgba8(DefaultAlbedoByte, DefaultAlbedoByte, DefaultAlbedoByte, alpha);
        }

        byte r = (byte)(sumR / count);
        byte g = (byte)(sumG / count);
        byte b = (byte)(sumB / count);
        return PackRgba8(r, g, b, alpha);
    }

    // =========================================================================
    // VSlot Diffuse Albedo Palette
    // =========================================================================

    private static readonly uint[] DefaultPalette =
    {
        PackRgba8(0, 0, 0, 0), // 0: DEFAULT_SKY
        PackRgba8(DefaultAlbedoByte, DefaultAlbedoByte, DefaultAlbedoByte, 0), // 1: DEFAULT_GEOM
    };

    private static readonly Dictionary<string, Color> _texColorCache = new();

    /// <summary>
    /// Precompute a lookup table mapping vslot index → packed RGB8 (alpha = 0) diffuse albedo.
    /// </summary>
    public static uint[] BuildVSlotPalette(MetadataCatalogue? catalogue, ITextureSource? textureSource)
    {
        if (catalogue == null || catalogue.VSlots.Count == 0)
            return DefaultPalette;

        int maxIndex = 1;
        foreach (int idx in catalogue.VSlots.Keys)
        {
            if (idx > maxIndex) maxIndex = idx;
        }

        uint defaultRgb = PackRgba8(DefaultAlbedoByte, DefaultAlbedoByte, DefaultAlbedoByte, 0);
        var palette = new uint[maxIndex + 1];
        Array.Fill(palette, defaultRgb);
        palette[Octa.DEFAULT_SKY] = 0u;

        foreach (var (idx, vslot) in catalogue.VSlots)
        {
            if (idx <= Octa.DEFAULT_SKY) continue;
            Color c = ComputeVSlotColor(idx, catalogue, textureSource);
            byte r = (byte)Math.Clamp((int)MathF.Round(c.R * 255f), 0, 255);
            byte g = (byte)Math.Clamp((int)MathF.Round(c.G * 255f), 0, 255);
            byte b = (byte)Math.Clamp((int)MathF.Round(c.B * 255f), 0, 255);
            palette[idx] = PackRgba8(r, g, b, 0);
        }

        return palette;
    }

    /// <summary>
    /// Compute the average RGB diffuse albedo for a vslot, combining catalogue vslot color tint
    /// and optional average texture RGB from <paramref name="textureSource"/>.
    /// </summary>
    public static Color ComputeVSlotColor(int vslotIndex, MetadataCatalogue? catalogue, ITextureSource? textureSource)
    {
        if (catalogue == null || !catalogue.VSlots.TryGetValue(vslotIndex, out var vslot))
            return new Color(RadianceVolume.DefaultAlbedo, RadianceVolume.DefaultAlbedo, RadianceVolume.DefaultAlbedo);

        Color tint = new(vslot.Color.X, vslot.Color.Y, vslot.Color.Z);
        Color texColor = Colors.White;

        if (textureSource != null && catalogue.Slots.TryGetValue(vslot.Slot, out var slot) && slot?.DiffusePath != null)
        {
            texColor = ComputeAverageTextureColor(textureSource, slot.DiffusePath);
        }

        return new Color(
            Mathf.Clamp(tint.R * texColor.R, 0.05f, 1.0f),
            Mathf.Clamp(tint.G * texColor.G, 0.05f, 1.0f),
            Mathf.Clamp(tint.B * texColor.B, 0.05f, 1.0f));
    }

    private static Color ComputeAverageTextureColor(ITextureSource textureSource, string diffusePath)
    {
        if (_texColorCache.TryGetValue(diffusePath, out Color cached))
            return cached;

        Image? img = textureSource.GetImage(diffusePath);
        if (img == null || img.IsEmpty())
        {
            _texColorCache[diffusePath] = Colors.White;
            return Colors.White;
        }

        if (img.IsCompressed() && img.Decompress() != Error.Ok)
        {
            _texColorCache[diffusePath] = Colors.White;
            return Colors.White;
        }

        int w = img.GetWidth();
        int h = img.GetHeight();
        if (w <= 0 || h <= 0)
        {
            _texColorCache[diffusePath] = Colors.White;
            return Colors.White;
        }

        int stepX = Math.Max(1, w / 8);
        int stepY = Math.Max(1, h / 8);
        float sumR = 0f, sumG = 0f, sumB = 0f;
        int count = 0;

        for (int y = 0; y < h; y += stepY)
        {
            for (int x = 0; x < w; x += stepX)
            {
                Color c = img.GetPixel(x, y);
                sumR += c.R;
                sumG += c.G;
                sumB += c.B;
                count++;
            }
        }

        Color avg = count > 0
            ? new Color(sumR / count, sumG / count, sumB / count)
            : Colors.White;
        _texColorCache[diffusePath] = avg;
        return avg;
    }
}
