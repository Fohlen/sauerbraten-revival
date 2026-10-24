using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.World.Effects;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Materials;
using ParticleEmitter = SauerbratenRevival.World.Effects.ParticleEmitter;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Verifies ET_PARTICLES representation. church51 has 119: 8 fire+smoke (0),
/// 8 fireballs (3, custom), 103 tape/streak (4, custom).
/// </summary>
public class EntityParticleTest : GoTestBase
{
    public EntityParticleTest(Node testScene) : base(testScene) { }

    [Test]
    public void BuildsParticleEmitters()
    {
        var map = TestData.ReadMap();
        var root = ParticleBuilder.Build(map);

        int count = 0, custom = 0;
        var byEffect = new Dictionary<int, int>();
        foreach (var child in root.GetChildren())
        {
            if (child is not ParticleEmitter emitter) continue;
            int index = int.Parse(emitter.Name.ToString().Split('_')[^1]);
            var entity = map.Entities[index];

            TestAssert.Equal((int)entity.Attr1, emitter.Effect, $"{emitter.Name} effect");
            TestAssert.Equal(ParticleEffect.Name(entity.Attr1), emitter.EffectName, $"{emitter.Name} name");
            TestAssert.Equal((int)entity.Attr2, emitter.Attr2, $"{emitter.Name} attr2");
            TestAssert.Equal((int)entity.Attr3, emitter.Attr3, $"{emitter.Name} attr3");
            TestAssert.Equal((int)entity.Attr4, emitter.Attr4, $"{emitter.Name} attr4");
            TestAssert.Equal((int)entity.Attr5, emitter.Attr5, $"{emitter.Name} attr5");
            TestAssert.True(emitter.Position.DistanceTo(entity.Position) < 0.01f, $"{emitter.Name} position");

            if (emitter.NeedsCustomRenderer) custom++;
            byEffect.TryGetValue(emitter.Effect, out int n);
            byEffect[emitter.Effect] = n + 1;
            count++;
        }

        TestAssert.Equal(119, count, "particle entities");
        TestAssert.Equal(8, byEffect.GetValueOrDefault(0), "fire+smoke");
        TestAssert.Equal(8, byEffect.GetValueOrDefault(3), "fireballs");
        TestAssert.Equal(103, byEffect.GetValueOrDefault(4), "tape");
        TestAssert.Equal(111, custom, "custom-renderer effects (fireball + tape)");
        root.Free();
    }

    [Test]
    public void RendersBillboardEffects()
    {
        var textures = new DumpTextureSource(TestData.ReferenceRoot);
        var map = TestData.ReadMap();
        int rendered = 0;
        foreach (var entity in map.Entities)
        {
            if (entity.Type != EntityType.Particles) continue;
            var emitter = new ParticleEmitter { Effect = entity.Attr1, Attr2 = entity.Attr2, Attr3 = entity.Attr3, Attr4 = entity.Attr4 };
            var node = ParticleRenderer.Build(emitter, textures);

            if (entity.Attr1 == 0) TestAssert.True(node != null, "fire+smoke renders as particles");
            if (entity.Attr1 is 3 or 4) TestAssert.True(node == null, $"effect {entity.Attr1} needs a custom renderer");
            if (node != null) { rendered++; node.Free(); }
            emitter.Free();
        }
        TestAssert.Equal(8, rendered, "only fire+smoke renders on church51");
    }

    [Test]
    public void EffectClassification()
    {
        TestAssert.True(!ParticleEffect.NeedsCustomRenderer(0), "fire+smoke is billboard/GPUParticles");
        TestAssert.True(!ParticleEffect.NeedsCustomRenderer(11), "flame is billboard/GPUParticles");
        TestAssert.True(ParticleEffect.NeedsCustomRenderer(4), "tape needs a custom renderer");
        TestAssert.True(ParticleEffect.NeedsCustomRenderer(3), "fireball needs a custom renderer");
        TestAssert.Equal("tape", ParticleEffect.Name(4), "name(4)");
        TestAssert.Equal("fire+smoke", ParticleEffect.Name(0), "name(0)");
    }
}
