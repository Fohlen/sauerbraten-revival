using System;
using Godot;

namespace SauerbratenRevival.World.Lighting;

/// <summary>
/// A single L1 spherical harmonics (SH) irradiance probe (docs/adr/0002-radiance-hints.md §2).
///
/// Stores:
/// <list type="bullet">
///   <item><description><see cref="Dir"/> (<c>c1</c>): directional irradiance vector (signed, 3 floats)</description></item>
///   <item><description><see cref="Amb"/> (<c>c0</c>): scalar ambient irradiance (1 float)</description></item>
/// </list>
///
/// The cosine-lobe convolution constants (Ramamoorthi &amp; Hanrahan 2001) are folded
/// directly into the probe coefficients at accumulation time, so evaluation at runtime
/// is a simple dot product:
/// <code>
/// E(n) = c0 + dot(c1, n)
/// </code>
/// </summary>
public struct RadianceProbe : IEquatable<RadianceProbe>
{
    /// <summary>
    /// Directional irradiance vector <c>c1</c> in world space (signed).
    /// Folded constant: <c>1.023327 * 0.488603 = 0.5</c>.
    /// </summary>
    public Vector3 Dir;

    /// <summary>
    /// Scalar ambient irradiance <c>c0</c>.
    /// Folded constant: <c>0.886227 * 0.282095 = 0.25</c>.
    /// </summary>
    public float Amb;

    /// <summary>
    /// Colored ambient irradiance <c>c0_rgb</c> in linear RGB (docs/adr/0002-radiance-hints.md §7.1).
    /// Preserves full RGB color of direct lights, sky/sun, and indirect bounce.
    /// </summary>
    public Vector3 AmbColor;

    /// <summary>
    /// Cosine-lobe folded scale for the constant (ambient) term from incoming directional radiance:
    /// <c>0.886227 * Y00 = 0.886227 * 0.282095 ≈ 0.25</c>.
    /// </summary>
    public const float DirectionalC0Factor = 0.25f;

    /// <summary>
    /// Cosine-lobe folded scale for the directional term from incoming directional radiance:
    /// <c>1.023327 * Y1m = 1.023327 * 0.488603 ≈ 0.5</c>.
    /// </summary>
    public const float DirectionalC1Factor = 0.5f;

    public RadianceProbe(Vector3 dir, float amb)
    {
        Dir = dir;
        Amb = amb;
        AmbColor = new Vector3(amb, amb, amb);
    }

    public RadianceProbe(Vector3 dir, float amb, Vector3 ambColor)
    {
        Dir = dir;
        Amb = amb;
        AmbColor = ambColor;
    }

    public static readonly RadianceProbe Zero = default;

    /// <summary>
    /// Project and accumulate an incoming directional light / ray sample into the probe.
    /// </summary>
    /// <param name="incomingDir">Normalized direction vector pointing toward the incoming light source.</param>
    /// <param name="radiance">Incoming radiance (energy).</param>
    public void AddDirectional(Vector3 incomingDir, float radiance)
    {
        Amb += DirectionalC0Factor * radiance;
        Dir += DirectionalC1Factor * radiance * incomingDir;
        AmbColor += DirectionalC0Factor * new Vector3(radiance, radiance, radiance);
    }

    /// <summary>
    /// Project and accumulate a colored incoming directional light / ray sample into the probe.
    /// </summary>
    /// <param name="incomingDir">Normalized direction vector pointing toward the incoming light source.</param>
    /// <param name="radianceColor">Incoming RGB radiance.</param>
    public void AddDirectional(Vector3 incomingDir, Color radianceColor)
    {
        float lum = (radianceColor.R + radianceColor.G + radianceColor.B) / 3.0f;
        Amb += DirectionalC0Factor * lum;
        Dir += DirectionalC1Factor * lum * incomingDir;
        AmbColor += DirectionalC0Factor * new Vector3(radianceColor.R, radianceColor.G, radianceColor.B);
    }

    /// <summary>
    /// Accumulate uniform ambient irradiance into the probe.
    /// </summary>
    /// <param name="irradiance">Diffuse ambient irradiance.</param>
    public void AddAmbient(float irradiance)
    {
        Amb += irradiance;
        AmbColor += new Vector3(irradiance, irradiance, irradiance);
    }

    /// <summary>
    /// Accumulate colored uniform ambient irradiance into the probe.
    /// </summary>
    /// <param name="irradianceColor">Diffuse RGB ambient irradiance.</param>
    public void AddAmbient(Color irradianceColor)
    {
        Amb += (irradianceColor.R + irradianceColor.G + irradianceColor.B) / 3.0f;
        AmbColor += new Vector3(irradianceColor.R, irradianceColor.G, irradianceColor.B);
    }

    /// <summary>
    /// Evaluate diffuse irradiance for a surface normal <paramref name="normal"/> (unclamped).
    /// </summary>
    public readonly float Evaluate(Vector3 normal) => Amb + Dir.Dot(normal);

    /// <summary>
    /// Evaluate diffuse irradiance for a surface normal <paramref name="normal"/> clamped to ≥ 0.
    /// </summary>
    public readonly float EvaluateClamped(Vector3 normal) => Mathf.Max(0f, Amb + Dir.Dot(normal));

    /// <summary>
    /// Evaluate tinted diffuse irradiance matching the scalar shader formula (docs/adr/0002-radiance-hints.md §2.3):
    /// <code>
    /// irr = max(sky_tint * c0 + dot(c1, normal), vec3(0.0))
    /// </code>
    /// </summary>
    public readonly Color Evaluate(Vector3 normal, Color skyTint)
    {
        float directional = Dir.Dot(normal);
        return new Color(
            Mathf.Max(0f, skyTint.R * Amb + directional),
            Mathf.Max(0f, skyTint.G * Amb + directional),
            Mathf.Max(0f, skyTint.B * Amb + directional));
    }

    /// <summary>
    /// Evaluate full RGB diffuse irradiance preserving colored ambient and directional terms (docs/adr/0002-radiance-hints.md §7.1):
    /// <code>
    /// chroma = (c0 > 1e-4) ? (c0_rgb / c0) : sky_tint
    /// irr = max(c0_rgb + chroma * dot(c1, normal), vec3(0.0))
    /// </code>
    /// </summary>
    public readonly Color EvaluateColor(Vector3 normal, Color skyTint)
    {
        float directional = Dir.Dot(normal);
        Vector3 chroma = Amb > 1e-4f
            ? AmbColor / Amb
            : new Vector3(skyTint.R, skyTint.G, skyTint.B);
        return new Color(
            Mathf.Max(0f, AmbColor.X + chroma.X * directional),
            Mathf.Max(0f, AmbColor.Y + chroma.Y * directional),
            Mathf.Max(0f, AmbColor.Z + chroma.Z * directional));
    }

    /// <summary>
    /// Linearly interpolate between two L1 SH probes.
    /// Because L1 SH coefficients are linear, this is equivalent to interpolating their irradiance functions.
    /// </summary>
    public static RadianceProbe Lerp(RadianceProbe a, RadianceProbe b, float t)
    {
        return new RadianceProbe(
            a.Dir.Lerp(b.Dir, t),
            Mathf.Lerp(a.Amb, b.Amb, t),
            a.AmbColor.Lerp(b.AmbColor, t));
    }

    public readonly bool Equals(RadianceProbe other) =>
        Dir.IsEqualApprox(other.Dir) && Mathf.IsEqualApprox(Amb, other.Amb);

    public override readonly bool Equals(object? obj) =>
        obj is RadianceProbe other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(Dir, Amb);

    public static bool operator ==(RadianceProbe left, RadianceProbe right) => left.Equals(right);
    public static bool operator !=(RadianceProbe left, RadianceProbe right) => !left.Equals(right);

    public override readonly string ToString() => $"RadianceProbe(Dir={Dir}, Amb={Amb:F3})";
}
