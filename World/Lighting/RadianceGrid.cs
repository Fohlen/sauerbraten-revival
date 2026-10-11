using System;
using System.Runtime.InteropServices;
using Godot;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// World-space 3D probe grid storage and coordinate mapping for L1 SH radiance hints
/// (docs/radiance-hints.md §2, §3).
///
/// Features:
/// <list type="bullet">
///   <item><description>Dense array storage of <see cref="RadianceProbe"/> (<c>resolution³</c> elements)</description></item>
///   <item><description>World-space cubic extents centered on geometry with configurable margin</description></item>
///   <item><description>Coordinates: Sauerbraten (Z-up) ↔ Godot world space (Y-up, −90° X rotation)</description></item>
///   <item><description>World ↔ cell and world ↔ UVW mappings</description></item>
///   <item><description>Generation and update of <see cref="ImageTexture3D"/> (<see cref="Image.Format.Rgbah"/>)</description></item>
///   <item><description>CPU-side trilinear probe sampling</description></item>
/// </list>
/// </summary>
public class RadianceGrid
{
	/// <summary>Default probe resolution per axis (32³ = 32,768 probes), matching docs/radiance-hints.md §7.</summary>
	public const int DefaultResolution = 32;

	/// <summary>Number of probes per axis (grid is cubic: Resolution × Resolution × Resolution).</summary>
	public int Resolution { get; }

	/// <summary>Minimum corner of the grid in Godot world space.</summary>
	public Vector3 Origin { get; }

	/// <summary>Edge length of the cubic grid in Godot world space.</summary>
	public float Extent { get; }

	/// <summary>World-space size of a single cell.</summary>
	public float CellSize => Extent / Resolution;

	/// <summary>Total number of probes in the grid (<see cref="Resolution"/>³).</summary>
	public int TotalProbes => Resolution * Resolution * Resolution;

	/// <summary>Godot world-space bounding box enclosing the grid.</summary>
	public Aabb Bounds => new(Origin, new Vector3(Extent, Extent, Extent));

	private readonly RadianceProbe[] _probes;

	/// <summary>Direct access to the underlying probe array.</summary>
	public RadianceProbe[] Probes => _probes;

	public RadianceGrid(Vector3 origin, float extent, int resolution = DefaultResolution)
	{
		if (resolution <= 0) throw new ArgumentOutOfRangeException(nameof(resolution), "Resolution must be positive");
		if (extent <= 0f) throw new ArgumentOutOfRangeException(nameof(extent), "Extent must be positive");

		Origin = origin;
		Extent = extent;
		Resolution = resolution;
		_probes = new RadianceProbe[resolution * resolution * resolution];
	}

	/// <summary>
	/// Linear index in the probe array for cell coordinate (x, y, z).
	/// Row-major order: X changes fastest, then Y, then Z (matching 3D texture depth slices).
	/// </summary>
	public int Index(int x, int y, int z) => x + Resolution * (y + Resolution * z);

	/// <summary>
	/// Decode a linear array index into its 3D cell coordinate (x, y, z).
	/// </summary>
	public Vector3I CellFromIndex(int index)
	{
		int x = index % Resolution;
		int rem = index / Resolution;
		int y = rem % Resolution;
		int z = rem / Resolution;
		return new Vector3I(x, y, z);
	}

	/// <summary>Direct reference to the probe at cell coordinate (x, y, z).</summary>
	public ref RadianceProbe this[int x, int y, int z] => ref _probes[Index(x, y, z)];

	/// <summary>Direct reference to the probe at cell coordinate <paramref name="cell"/>.</summary>
	public ref RadianceProbe this[Vector3I cell] => ref _probes[Index(cell.X, cell.Y, cell.Z)];

	/// <summary>Direct reference to the probe at linear index <paramref name="index"/>.</summary>
	public ref RadianceProbe this[int index] => ref _probes[index];

	/// <summary>True if the given cell coordinates are within [0, Resolution - 1].</summary>
	public bool IsInside(int x, int y, int z) =>
		x >= 0 && x < Resolution &&
		y >= 0 && y < Resolution &&
		z >= 0 && z < Resolution;

	/// <summary>True if the given cell coordinates are within the grid.</summary>
	public bool IsInside(Vector3I cell) => IsInside(cell.X, cell.Y, cell.Z);

	/// <summary>True if the Godot world position falls within the grid's bounding box.</summary>
	public bool ContainsWorld(Vector3 worldPos) => Bounds.HasPoint(worldPos);

	// =========================================================================
	// Coordinate conversions (docs/radiance-hints.md §3)
	// Sauerbraten is Z-up; Godot world is Y-up (rotated −90° about X).
	// =========================================================================

	/// <summary>
	/// Map a point or direction from Sauerbraten space (Z-up) to Godot world space (Y-up).
	/// Rotation −90° about X: (x, y, z) → (x, z, -y).
	/// </summary>
	public static Vector3 SauerToWorld(Vector3 sauer) => new(sauer.X, sauer.Z, -sauer.Y);

	/// <summary>
	/// Map a point or direction from Godot world space (Y-up) to Sauerbraten space (Z-up).
	/// Inverse rotation +90° about X: (x, y, z) → (x, -z, y).
	/// </summary>
	public static Vector3 WorldToSauer(Vector3 world) => new(world.X, -world.Z, world.Y);

	/// <summary>
	/// Map an AABB from Sauerbraten coordinates to Godot world coordinates.
	/// </summary>
	public static Aabb SauerBoundsToWorld(Aabb sauerBounds)
	{
		Vector3 sMin = sauerBounds.Position;
		Vector3 sMax = sauerBounds.End;
		// In Sauer space: y is forward, z is up.
		// In Godot space: y is up (sauer.z), z is -forward (-sauer.y).
		Vector3 wMin = new(sMin.X, sMin.Z, -sMax.Y);
		Vector3 wMax = new(sMax.X, sMax.Z, -sMin.Y);
		return new Aabb(wMin, wMax - wMin);
	}

	/// <summary>
	/// Center of the cell at (x, y, z) in Godot world coordinates.
	/// </summary>
	public Vector3 CellCenterWorld(int x, int y, int z) =>
		Origin + new Vector3((x + 0.5f) * CellSize, (y + 0.5f) * CellSize, (z + 0.5f) * CellSize);

	/// <summary>
	/// Center of the cell in Godot world coordinates.
	/// </summary>
	public Vector3 CellCenterWorld(Vector3I cell) => CellCenterWorld(cell.X, cell.Y, cell.Z);

	/// <summary>
	/// Center of the cell at (x, y, z) in Sauerbraten coordinates (for octree / raycast queries).
	/// </summary>
	public Vector3 CellCenterSauer(int x, int y, int z) => WorldToSauer(CellCenterWorld(x, y, z));

	/// <summary>
	/// Center of the cell in Sauerbraten coordinates.
	/// </summary>
	public Vector3 CellCenterSauer(Vector3I cell) => CellCenterSauer(cell.X, cell.Y, cell.Z);

	/// <summary>
	/// Map a Godot world position to normalized [0, 1] grid UVW coordinates.
	/// Matching shader sampling: <c>uvw = (world_pos - grid_min) / grid_size</c>.
	/// </summary>
	public Vector3 WorldToUvw(Vector3 worldPos) => (worldPos - Origin) / Extent;

	/// <summary>
	/// Map normalized [0, 1] grid UVW coordinates back to Godot world coordinates.
	/// </summary>
	public Vector3 UvwToWorld(Vector3 uvw) => Origin + uvw * Extent;

	/// <summary>
	/// Map a Sauerbraten position directly to normalized [0, 1] grid UVW coordinates.
	/// </summary>
	public Vector3 SauerToUvw(Vector3 sauerPos) => WorldToUvw(SauerToWorld(sauerPos));

	/// <summary>
	/// Map a Godot world position to the discrete cell coordinate enclosing it.
	/// </summary>
	public Vector3I WorldToCell(Vector3 worldPos)
	{
		Vector3 offset = (worldPos - Origin) / CellSize;
		return new Vector3I(
			Mathf.FloorToInt(offset.X),
			Mathf.FloorToInt(offset.Y),
			Mathf.FloorToInt(offset.Z));
	}

	// =========================================================================
	// Grid construction from geometry bounds (docs/radiance-hints.md §3)
	// =========================================================================

	/// <summary>
	/// Compute the AABB of all non-empty geometry in Sauerbraten coordinates.
	/// Reference implementation from test/RadianceBakeProbe.cs:GeometryBounds.
	/// </summary>
	public static Aabb CalculateGeometryBounds(Octa octa)
	{
		ArgumentNullException.ThrowIfNull(octa);

		Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
		Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
		bool any = false;

		Walk(octa.WorldRoot, 0, 0, 0, octa.WorldSize / 2);
		return any ? new Aabb(min, max - min) : new Aabb(Vector3.Zero, Vector3.One);

		void Walk(Cube[] cubes, int x, int y, int z, int size)
		{
			for (int i = 0; i < 8; i++)
			{
				Cube c = cubes[i];
				int cx = x + ((i & 1) != 0 ? size : 0);
				int cy = y + ((i & 2) != 0 ? size : 0);
				int cz = z + ((i & 4) != 0 ? size : 0);
				if (c.Children != null)
				{
					Walk(c.Children, cx, cy, cz, size >> 1);
					continue;
				}
				if (c.IsEmpty()) continue;
				any = true;
				min = min.Min(new Vector3(cx, cy, cz));
				max = max.Max(new Vector3(cx + size, cy + size, cz + size));
			}
		}
	}

	/// <summary>
	/// Build a cubic <see cref="RadianceGrid"/> spanning the non-empty geometry of an <see cref="Octa"/>,
	/// expanded by <paramref name="marginCells"/> so geometry does not touch grid boundaries.
	/// </summary>
	public static RadianceGrid FromOcta(Octa octa, int resolution = DefaultResolution, int marginCells = 1)
	{
		Aabb sauerBounds = CalculateGeometryBounds(octa);
		return FromSauerBounds(sauerBounds, resolution, marginCells);
	}

	/// <summary>
	/// Build a cubic <see cref="RadianceGrid"/> from an AABB in Sauerbraten space.
	/// </summary>
	public static RadianceGrid FromSauerBounds(Aabb sauerBounds, int resolution = DefaultResolution, int marginCells = 1)
	{
		Aabb worldBounds = SauerBoundsToWorld(sauerBounds);
		return FromWorldBounds(worldBounds, resolution, marginCells);
	}

	/// <summary>
	/// Build a cubic <see cref="RadianceGrid"/> centered on a Godot world-space AABB,
	/// expanded so cells are cubic and padded by <paramref name="marginCells"/> on each side along the maximum axis.
	/// </summary>
	public static RadianceGrid FromWorldBounds(Aabb worldBounds, int resolution = DefaultResolution, int marginCells = 1)
	{
		if (resolution <= 0) throw new ArgumentOutOfRangeException(nameof(resolution));
		if (marginCells < 0) throw new ArgumentOutOfRangeException(nameof(marginCells));

		Vector3 size = worldBounds.Size;
		float maxExtent = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
		if (maxExtent <= 0f) maxExtent = 1f;

		int innerCells = Math.Max(1, resolution - 2 * marginCells);
		float cell = maxExtent / innerCells;
		float extent = resolution * cell;

		Vector3 center = worldBounds.GetCenter();
		Vector3 origin = center - new Vector3(extent, extent, extent) * 0.5f;

		return new RadianceGrid(origin, extent, resolution);
	}

	// =========================================================================
	// Sampling & operations
	// =========================================================================

	/// <summary>Reset all probes to zero.</summary>
	public void Clear() => Array.Clear(_probes, 0, _probes.Length);

	/// <summary>Fill all probes with a constant value.</summary>
	public void Fill(RadianceProbe probe) => Array.Fill(_probes, probe);

	/// <summary>
	/// CPU-side trilinear probe sampling at a Godot world position.
	/// Matches the linear interpolation of SH coefficients in hardware sampler3D.
	/// </summary>
	public RadianceProbe Sample(Vector3 worldPos) => SampleUvw(WorldToUvw(worldPos));

	/// <summary>
	/// CPU-side trilinear probe sampling with normalized UVW coordinates.
	/// </summary>
	public RadianceProbe SampleUvw(Vector3 uvw)
	{
		// Grid probe centers sit at (i + 0.5) / Resolution
		float px = uvw.X * Resolution - 0.5f;
		float py = uvw.Y * Resolution - 0.5f;
		float pz = uvw.Z * Resolution - 0.5f;

		int x0 = Mathf.Clamp(Mathf.FloorToInt(px), 0, Resolution - 1);
		int y0 = Mathf.Clamp(Mathf.FloorToInt(py), 0, Resolution - 1);
		int z0 = Mathf.Clamp(Mathf.FloorToInt(pz), 0, Resolution - 1);

		int x1 = Mathf.Min(x0 + 1, Resolution - 1);
		int y1 = Mathf.Min(y0 + 1, Resolution - 1);
		int z1 = Mathf.Min(z0 + 1, Resolution - 1);

		float fx = Mathf.Clamp(px - Mathf.Floor(px), 0f, 1f);
		float fy = Mathf.Clamp(py - Mathf.Floor(py), 0f, 1f);
		float fz = Mathf.Clamp(pz - Mathf.Floor(pz), 0f, 1f);

		ref readonly var c000 = ref _probes[Index(x0, y0, z0)];
		ref readonly var c100 = ref _probes[Index(x1, y0, z0)];
		ref readonly var c010 = ref _probes[Index(x0, y1, z0)];
		ref readonly var c110 = ref _probes[Index(x1, y1, z0)];
		ref readonly var c001 = ref _probes[Index(x0, y0, z1)];
		ref readonly var c101 = ref _probes[Index(x1, y0, z1)];
		ref readonly var c011 = ref _probes[Index(x0, y1, z1)];
		ref readonly var c111 = ref _probes[Index(x1, y1, z1)];

		var c00 = RadianceProbe.Lerp(c000, c100, fx);
		var c10 = RadianceProbe.Lerp(c010, c110, fx);
		var c01 = RadianceProbe.Lerp(c001, c101, fx);
		var c11 = RadianceProbe.Lerp(c011, c111, fx);

		var c0 = RadianceProbe.Lerp(c00, c10, fy);
		var c1 = RadianceProbe.Lerp(c01, c11, fy);

		return RadianceProbe.Lerp(c0, c1, fz);
	}

	// =========================================================================
	// ImageTexture3D generation and upload (docs/radiance-hints.md §2.2, §6)
	// =========================================================================

	/// <summary>
	/// Build an array of 2D slice <see cref="Image"/>s in <see cref="Image.Format.Rgbah"/> format,
	/// one image per depth slice along Z.
	/// Each texel contains:
	/// <list type="bullet">
	///   <item><description><c>R</c>: <c>c1.X</c> (16-bit half float, signed)</description></item>
	///   <item><description><c>G</c>: <c>c1.Y</c> (16-bit half float, signed)</description></item>
	///   <item><description><c>B</c>: <c>c1.Z</c> (16-bit half float, signed)</description></item>
	///   <item><description><c>A</c>: <c>c0</c> (16-bit half float, scalar ambient)</description></item>
	/// </list>
	/// </summary>
	public Godot.Collections.Array<Image> BuildImages()
	{
		var images = new Godot.Collections.Array<Image>();
		int sliceTexels = Resolution * Resolution;
		int sliceBytesLen = sliceTexels * 8; // 4 channels × 2 bytes (Half) = 8 bytes/texel

		for (int z = 0; z < Resolution; z++)
		{
			byte[] sliceBytes = new byte[sliceBytesLen];
			Span<Half> halfSpan = MemoryMarshal.Cast<byte, Half>(sliceBytes.AsSpan());
			int halfIdx = 0;

			for (int y = 0; y < Resolution; y++)
			{
				for (int x = 0; x < Resolution; x++)
				{
					ref readonly var probe = ref _probes[Index(x, y, z)];
					halfSpan[halfIdx++] = (Half)probe.Dir.X;
					halfSpan[halfIdx++] = (Half)probe.Dir.Y;
					halfSpan[halfIdx++] = (Half)probe.Dir.Z;
					halfSpan[halfIdx++] = (Half)probe.Amb;
				}
			}

			var sliceImage = Image.CreateFromData(Resolution, Resolution, false, Image.Format.Rgbah, sliceBytes);
			images.Add(sliceImage);
		}

		return images;
	}

	/// <summary>
	/// Create a new Godot <see cref="ImageTexture3D"/> resource initialized with the current grid probes.
	/// </summary>
	public ImageTexture3D CreateTexture()
	{
		var images = BuildImages();
		var tex = new ImageTexture3D();
		Error err = tex.Create(Image.Format.Rgbah, Resolution, Resolution, Resolution, false, images);
		if (err != Error.Ok)
		{
			throw new InvalidOperationException($"Failed to create ImageTexture3D ({Resolution}³, Rgbah): {err}");
		}
		return tex;
	}

	/// <summary>
	/// Update an existing <see cref="ImageTexture3D"/> with current CPU probe data.
	/// Replaces the whole 3D texture using <see cref="ImageTexture3D.Update"/>.
	/// </summary>
	public void UpdateTexture(ImageTexture3D texture)
	{
		ArgumentNullException.ThrowIfNull(texture);
		var images = BuildImages();
		texture.Update(images);
	}
}
