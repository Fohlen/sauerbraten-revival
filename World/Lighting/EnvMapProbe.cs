using Godot;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// An <c>ET_ENVMAP</c> reflection/cubemap probe. In Cube 2 the entity defines a
/// locational cubemap: geometry within <see cref="Radius"/> samples the nearest
/// probe (<c>closestenvmap</c>), falling back to the sky cubemap. Godot's
/// <see cref="ReflectionProbe"/> models the same idea.
///
/// The Sauerbraten attributes are kept verbatim (<see cref="Attr1"/>/<see cref="Attr2"/>/
/// <see cref="Attr3"/>) alongside the resolved, clamped values the engine would use
/// (<see cref="Radius"/>/<see cref="CubeMapSize"/>/<see cref="Blur"/>), so the test can
/// assert both against <see cref="EnvMapBuilder"/>.
/// </summary>
[GlobalClass]
public partial class EnvMapProbe : ReflectionProbe
{
    /// <summary>Raw <c>attr1</c> (radius; 0 → default).</summary>
    [Export] public int Attr1 { get; set; }

    /// <summary>Raw <c>attr2</c> (cubemap size 4..9; 0 → default).</summary>
    [Export] public int Attr2 { get; set; }

    /// <summary>Raw <c>attr3</c> (blur 1..2; 0 → none).</summary>
    [Export] public int Attr3 { get; set; }

    /// <summary>Resolved radius in world units (see <see cref="EnvMapBuilder.ResolveRadius"/>).</summary>
    [Export] public int Radius { get; set; } = EnvMapBuilder.DefaultRadius;

    /// <summary>Resolved cubemap size exponent (see <see cref="EnvMapBuilder.ResolveCubeMapSize"/>).</summary>
    [Export] public int CubeMapSize { get; set; } = EnvMapBuilder.DefaultSize;

    /// <summary>Resolved blur amount, 0 for none (see <see cref="EnvMapBuilder.ResolveBlur"/>).</summary>
    [Export] public int Blur { get; set; }

    /// <summary>Default SkyBuilder cube face resolution: <c>1 &lt;&lt; envmapsize</c>.</summary>
    public int CubeMapResolution => 1 << CubeMapSize;

    /// <summary>
    /// Bounding box in Sauerbraten coordinates enclosing this probe's spherical influence volume.
    /// </summary>
    public Aabb SauerBounds => new(
        Position - new Vector3(Radius, Radius, Radius),
        new Vector3(Radius * 2f, Radius * 2f, Radius * 2f)
    );

    public override void _Ready()
    {
        // A box probe of side = diameter envelops the sphere the entity describes.
        float extent = Mathf.Max(2.0f * Radius, 1.0f);
        Size = new Vector3(extent, extent, extent);
        MaxDistance = Mathf.Max(Radius, 1.0f);

        // Cube 2 blends between the nearest probe and the sky; a soft blend keeps
        // the transition from looking like a hard cut at the radius boundary.
        BlendDistance = Mathf.Max(Radius * 0.1f, 1.0f);
        BoxProjection = true;
        UpdateMode = UpdateModeEnum.Once;

        // Blur softens the reflection — Godot has no direct blur control, so the
        // resolved value is informational for now (see EnvMapBuilder).
        if (Blur > 0) Intensity = Mathf.Lerp(1.0f, 0.6f, (Blur - 1) / 1.0f);
    }
}
