using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Builds <see cref="ParticleEmitter"/> nodes from a map's <c>ET_PARTICLES</c>
/// entities and attaches an entity marker. The actual effects (billboard particles,
/// `GPUParticles3D`, or custom shaders) are a later slice; see
/// docs/ogz-import.md#particles-et_particles.
/// </summary>
public static class ParticleBuilder
{
    public static Node3D Build(OgzMap map, ITextureSource? textures = null)
    {
        var root = new Node3D { Name = "Particles" };
        for (int i = 0; i < map.Entities.Count; i++)
        {
            var e = map.Entities[i];
            if (e.Type != EntityType.Particles) continue;

            var emitter = new ParticleEmitter
            {
                Name = $"Particle_{i}",
                Position = e.Position,
                Effect = e.Attr1,
                EffectName = ParticleEffect.Name(e.Attr1),
                Attr2 = e.Attr2,
                Attr3 = e.Attr3,
                Attr4 = e.Attr4,
                Attr5 = e.Attr5,
                NeedsCustomRenderer = ParticleEffect.NeedsCustomRenderer(e.Attr1),
            };
            root.AddChild(emitter);
            EntityMarker.Add(emitter, textures);

            var effect = ParticleRenderer.Build(emitter, textures);
            if (effect != null) emitter.AddChild(effect);
        }
        return root;
    }
}
