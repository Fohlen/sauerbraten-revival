using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Runtime.Formats.Ogz;

namespace SauerbratenRevival.World.Effects;

/// <summary>
/// Builds <see cref="ParticleEmitter"/> nodes from a map's <c>ET_PARTICLES</c>
/// entities and attaches an entity marker. The actual effects (billboard particles,
/// `GPUParticles3D`, or custom shaders) are a later slice; see
/// docs/adr/0006-ogz-import.md#particles-et_particles.
/// </summary>
public static class ParticleBuilder
{
    public static Node3D Build(OgzMap map, ITextureSource? textures = null) => Build(map.Entities, textures);

    public static Node3D Build(IReadOnlyList<MapEntity> entities, ITextureSource? textures = null)
    {
        var root = new Node3D { Name = "Particles" };
        foreach (var (i, e) in entities.OfType(EntityType.Particles))
        {
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
