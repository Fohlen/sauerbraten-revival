using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Effects;

/// <summary>
/// Renders instant tracer lines and trails for hitscan weapon fire (Rifle, Shotgun, Chaingun, Pistol).
/// Ports the visual feel of <c>src/fpsgame/weapon.cpp:shoteffects</c> (<c>particle_trail</c>, <c>particle_flare</c>).
/// </summary>
public partial class WeaponEffectsRenderer : Node3D
{
    private class ActiveBeam
    {
        public Vector3 From;
        public Vector3 To;
        public Color Color;
        public float StartTime;
        public float Lifetime;
        public float Width;
    }

    private readonly List<ActiveBeam> _beams = new();
    private MeshInstance3D _meshInstance = null!;
    private ImmediateMesh _mesh = null!;

    public override void _Ready()
    {
        _mesh = new ImmediateMesh();
        _meshInstance = new MeshInstance3D
        {
            Name = "TracerMesh",
            Mesh = _mesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                NoDepthTest = false,
            }
        };
        AddChild(_meshInstance);
    }

    /// <summary>
    /// Spawns tracer beams from a hitscan result.
    /// </summary>
    public void AddShotEffects(Gun gun, Vector3 from, IReadOnlyList<Vector3> rayEndpoints)
    {
        float now = (float)Time.GetTicksMsec() / 1000f;

        switch (gun)
        {
            case Gun.Rifle:
                // Smoke/rail trail: prominent high-visibility beam lasting 0.5s
                // r6889 particle_trail(PART_SMOKE, 500, hudgunorigin, to, 0x404040, 0.6f, 20)
                if (rayEndpoints.Count > 0)
                {
                    _beams.Add(new ActiveBeam
                    {
                        From = from,
                        To = rayEndpoints[0],
                        Color = new Color(0.85f, 0.9f, 1.0f, 0.85f),
                        StartTime = now,
                        Lifetime = 0.5f,
                        Width = 0.15f
                    });
                }
                break;

            case Gun.Shotgun:
                // Multiple pellet tracers
                // r6889 particle_flare(..., 300, PART_STREAK, 0xFFC864, 0.28f)
                for (int i = 0; i < rayEndpoints.Count; i++)
                {
                    _beams.Add(new ActiveBeam
                    {
                        From = from,
                        To = rayEndpoints[i],
                        Color = new Color(1.0f, 0.78f, 0.39f, 0.7f),
                        StartTime = now,
                        Lifetime = 0.25f,
                        Width = 0.06f
                    });
                }
                break;

            case Gun.Chaingun:
            case Gun.Pistol:
                // Yellow streak
                if (rayEndpoints.Count > 0)
                {
                    _beams.Add(new ActiveBeam
                    {
                        From = from,
                        To = rayEndpoints[0],
                        Color = new Color(1.0f, 0.78f, 0.39f, 0.75f),
                        StartTime = now,
                        Lifetime = 0.18f,
                        Width = 0.08f
                    });
                }
                break;
        }
    }

    public override void _Process(double delta)
    {
        float now = (float)Time.GetTicksMsec() / 1000f;
        _mesh.ClearSurfaces();

        for (int i = _beams.Count - 1; i >= 0; i--)
        {
            var b = _beams[i];
            float elapsed = now - b.StartTime;
            if (elapsed >= b.Lifetime)
            {
                _beams.RemoveAt(i);
                continue;
            }

            float fade = 1f - (elapsed / b.Lifetime);
            Color c = b.Color with { A = b.Color.A * fade };

            // Draw line strip
            _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
            _mesh.SurfaceSetColor(c);
            _mesh.SurfaceAddVertex(b.From);
            _mesh.SurfaceSetColor(c);
            _mesh.SurfaceAddVertex(b.To);
            _mesh.SurfaceEnd();
        }
    }
}
