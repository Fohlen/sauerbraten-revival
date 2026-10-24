using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Effects;

/// <summary>
/// Renders the <c>ET_PARTICLES</c> effects that map to Godot billboard particles
/// (`GpuParticles3D`): fire+smoke, flame, smoke plume, steam (vent), water
/// fountain, snow. Effects that need a custom renderer (fireball/tape/meter/
/// lightning/lens flare) return null and keep <see cref="ParticleEmitter.NeedsCustomRenderer"/>.
/// </summary>
public static class ParticleRenderer
{
    public static Node3D? Build(ParticleEmitter emitter, ITextureSource? textures)
    {
        if (textures == null) return null;

        var root = new Node3D { Name = "Effect" };
        switch (emitter.Effect)
        {
            case 0: // fire and smoke - <radius> <height> <rgb>
                {
                    float radius = emitter.Attr2 != 0 ? emitter.Attr2 / 100.0f : 1.5f;
                    float height = emitter.Attr3 != 0 ? emitter.Attr3 / 100.0f : radius / 3.0f;
                    var flameColor = emitter.Attr4 != 0 ? ColorFromAttr(emitter.Attr4) : new Color(0x90 / 255f, 0x30 / 255f, 0x20 / 255f);
                    root.AddChild(Flame(textures, "particles/flames.png", radius, height, flameColor, 2.0f, 200.0f, 600.0f, -15.0f, 1.0f, true));
                    var smoke = Flame(textures, "particles/smoke.png", radius, height, new Color(0x30 / 255f, 0x30 / 255f, 0x20 / 255f), 4.0f, 100.0f, 2000.0f, -20.0f, 0.25f, false);
                    smoke.Position = new Vector3(0, 0, 4.0f * Mathf.Min(radius, height));
                    root.AddChild(smoke);
                    break;
                }

            case 11: // flame - <radius> <height> <rgb>
                root.AddChild(Flame(textures, "particles/flames.png", emitter.Attr2 / 100.0f, emitter.Attr3 / 100.0f,
                    ColorFromAttr(emitter.Attr4), 2.0f, 200.0f, 600.0f, -15.0f, 1.0f, true));
                break;

            case 12: // smoke plume - <radius> <height> <rgb>
                root.AddChild(Flame(textures, "particles/smoke.png", emitter.Attr2 / 100.0f, emitter.Attr3 / 100.0f,
                    ColorFromAttr(emitter.Attr4), 4.0f, 100.0f, 2000.0f, -20.0f, 0.25f, false));
                break;

            case 1: // steam vent - <dir>
                root.AddChild(Splash(textures, "particles/steam.png", new Color(0x89 / 255f, 0x76 / 255f, 0x61 / 255f), 50.0f, 2.4f, -20.0f, 0.6f, 12, false));
                break;

            case 2: // water fountain - <dir> <rgb>
                {
                    var color = emitter.Attr3 > 0 ? ColorFromAttr(emitter.Attr3) : new Color(0.5f, 0.6f, 0.8f);
                    root.AddChild(Splash(textures, "particles/base.png", color, 150.0f, 0.6f, 2.0f, 0.6f, 16, true));
                    break;
                }

            case 9: // steam - <dir> <rgb>
                root.AddChild(Splash(textures, "particles/steam.png", ColorFromAttr(emitter.Attr4), 10.0f, 2.4f, -20.0f, 0.6f, 12, false));
                break;

            case 10: // water - <dir> <rgb>
                root.AddChild(Splash(textures, "particles/base.png", ColorFromAttr(emitter.Attr4), 10.0f, 0.6f, 2.0f, 0.6f, 16, true));
                break;

            case 13: // snow - <dir> <rgb>
                root.AddChild(Splash(textures, "particles/snow.png", ColorFromAttr(emitter.Attr4), 10.0f, 0.5f, 20.0f, 1.0f, 12, false));
                break;

            default:
                // No billboard representation: drop the empty holder we built
                // before discovering the effect needs a custom renderer.
                root.Free();
                return null; // custom renderer needed
        }
        return root;
    }

    // regularflame: upward jet; size = scale*min(radius,height), speed = min(1,height)*speed, life = fade*height.
    private static GpuParticles3D Flame(ITextureSource textures, string texture, float radius, float height, Color color,
                                        float sizeScale, float speed, float fade, float gravity, float density, bool additive)
    {
        float length = Mathf.Max(Mathf.Min(radius, height), 0.05f);
        return Emit(textures, texture, color, radius,
            size: sizeScale * length,
            speed: Mathf.Min(1.0f, height) * speed,
            lifetime: Mathf.Max(fade * height, 200.0f) / 1000.0f,
            gravity: gravity,
            amount: Mathf.RoundToInt(32 * density),
            additive: additive);
    }

    // regularsplash: N particles in a sphere of `radius`, outward, life ~ fade*3.
    private static GpuParticles3D Splash(ITextureSource textures, string texture, Color color, float radius,
                                         float size, float gravity, float lifetime, int amount, bool additive)
    {
        return Emit(textures, texture, color, radius, size, 0.0f, lifetime, gravity, amount, additive, spread: 180.0f);
    }

    private static GpuParticles3D Emit(ITextureSource textures, string texture, Color color, float radius,
                                       float size, float speed, float lifetime, float gravity, int amount,
                                       bool additive, float spread = 18.0f)
    {
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = Mathf.Max(radius, 0.01f),
            Direction = new Vector3(0, 0, 1), // Sauerbraten is Z-up; the parent world node rotates it.
            Spread = spread,
            InitialVelocityMin = speed,
            InitialVelocityMax = speed,
            Gravity = new Vector3(0, 0, gravity),
            ScaleMin = size * 0.6f,
            ScaleMax = size,
            Color = color,
        };

        var quad = new QuadMesh { Size = Vector2.One };
        quad.Material = new StandardMaterial3D
        {
            AlbedoTexture = textures.Get(texture),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        return new GpuParticles3D
        {
            Name = "Particles",
            Amount = Mathf.Max(amount, 1),
            Lifetime = Mathf.Max(lifetime, 0.1f),
            ProcessMaterial = process,
            DrawPass1 = quad,
            Emitting = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>renderparticles.cpp <c>colorfromattr</c>.</summary>
    public static Color ColorFromAttr(int attr)
    {
        int rgb = (((attr & 0xF) << 4) | ((attr & 0xF0) << 8) | ((attr & 0xF00) << 12)) + 0x0F0F0F;
        return new Color(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f);
    }
}
