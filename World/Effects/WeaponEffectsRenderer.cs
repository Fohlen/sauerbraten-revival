using System;
using System.Collections.Generic;
using System.IO;
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

    private class ActiveDecal
    {
        public MeshInstance3D MeshInstance = null!;
        public StandardMaterial3D Material = null!;
        public float StartTime;
        public float FadeStartTime;
        public float TotalLifetime;
    }

    private class ActiveExplosion
    {
        public Vector3 Origin;
        public float Radius;
        public Color Color;
        public float StartTime;
        public float Lifetime;
    }

    private const int MaxDecals = 256;
    private const float BulletDecalFadeTime = 10f; // decalfade default: 10s (r6889)
    private const float BulletDecalFadeOut = 1f;   // fadeouttime: 1s (r6889)

    // Rocket projectile model (weapon.cpp renderprojectiles; the `projectiles/rocket` MD3).
    private const string RocketModelPath = "res://assets/models/projectiles_rocket.glb";
    private const string RocketSkinPath = "res://assets/textures/models/projectiles/rocket/skin.jpg";
    // The converted MD3 is authored in Cube2 model units; 0.2 matches the grenade's
    // chosen world scale (see BounceBody) so both projectiles read at a similar size.
    private const float RocketVisualScale = 0.2f;
    // renderprojectiles draws the model 3 units ahead of the smoke-trail position
    // (weapon.cpp:910 `v.mul(3); v.add(pos)`).
    private const float RocketVisualLead = 3f;

    /// <summary>Optional provider for active flying rockets to render visual rocket trails.</summary>
    public Func<IEnumerable<RocketProjectile>>? ActiveRocketsProvider { get; set; }

    private readonly List<ActiveBeam> _beams = new();
    private readonly List<ActiveExplosion> _explosions = new();
    private readonly ActiveDecal[] _decalPool = new ActiveDecal[MaxDecals];
    private int _decalIndex;
    private Texture2D? _bulletTexture;
    private Texture2D? _scorchTexture;
    private QuadMesh _quadMesh = null!;

    private PackedScene? _rocketScene;
    private Material? _rocketMaterial;
    private readonly Dictionary<RocketProjectile, Node3D> _rocketVisuals = new();
    private readonly List<Node3D> _rocketVisualPool = new();
    private readonly HashSet<RocketProjectile> _activeRocketsScratch = new();
    private readonly List<RocketProjectile> _expiredRocketsScratch = new();

    private MeshInstance3D _meshInstance = null!;
    private ImmediateMesh _mesh = null!;
    private Node3D _decalContainer = null!;

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

        _decalContainer = new Node3D { Name = "DecalContainer" };
        AddChild(_decalContainer);

        string texPath = "res://assets/textures/particles/bullet.png";
        string diskPath = ProjectSettings.GlobalizePath(texPath);
        if (File.Exists(diskPath))
        {
            var img = new Image();
            if (img.Load(diskPath) == Error.Ok)
            {
                _bulletTexture = ImageTexture.CreateFromImage(img);
            }
        }
        if (_bulletTexture == null && ResourceLoader.Exists(texPath))
        {
            _bulletTexture = GD.Load<Texture2D>(texPath);
        }

        string scorchPath = "res://assets/textures/particles/scorch.png";
        string scorchDiskPath = ProjectSettings.GlobalizePath(scorchPath);
        if (File.Exists(scorchDiskPath))
        {
            var img = new Image();
            if (img.Load(scorchDiskPath) == Error.Ok)
            {
                _scorchTexture = ImageTexture.CreateFromImage(img);
            }
        }
        if (_scorchTexture == null && ResourceLoader.Exists(scorchPath))
        {
            _scorchTexture = GD.Load<Texture2D>(scorchPath);
        }

        // Shared 1x1 quad mesh (size is scaled via Node3D scale)
        _quadMesh = new QuadMesh { Size = new Vector2(1f, 1f) };

        for (int i = 0; i < MaxDecals; i++)
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = _bulletTexture,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                NoDepthTest = false,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always
            };

            var meshInst = new MeshInstance3D
            {
                Name = $"BulletDecal_{i}",
                Mesh = _quadMesh,
                MaterialOverride = mat,
                Visible = false
            };
            _decalContainer.AddChild(meshInst);

            _decalPool[i] = new ActiveDecal
            {
                MeshInstance = meshInst,
                Material = mat,
                StartTime = 0f,
                FadeStartTime = 0f,
                TotalLifetime = 0f
            };
        }

        LoadRocketVisual();
    }

    /// <summary>
    /// Loads the converted <c>projectiles/rocket</c> model and its skin once. Mirrors
    /// the grenade visual in <see cref="World.Entities.BounceBody"/> (the imperfect
    /// direct GLB/skin load used for the first pass).
    /// </summary>
    private void LoadRocketVisual()
    {
        if (!ResourceLoader.Exists(RocketModelPath)) return;
        _rocketScene = ResourceLoader.Load<PackedScene>(RocketModelPath);
        if (_rocketScene == null) return;

        var mat = new StandardMaterial3D
        {
            Roughness = 0.3f,
            Metallic = 0.6f
        };
        if (ResourceLoader.Exists(RocketSkinPath))
        {
            mat.AlbedoTexture = ResourceLoader.Load<Texture2D>(RocketSkinPath);
            mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
        }
        else
        {
            mat.AlbedoColor = new Color(0.5f, 0.5f, 0.5f); // fallback when the skin is missing
        }
        _rocketMaterial = mat;
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

    /// <summary>
    /// Spawns a bullet decal mark on a surface.
    /// Matches <c>adddecal(DECAL_BULLET, to, dir, radius)</c> from <c>src/fpsgame/weapon.cpp</c>.
    /// </summary>
    /// <param name="pos">Impact center position in world space.</param>
    /// <param name="dir">Surface normal or inverted ray direction.</param>
    /// <param name="radius">Decal radius (Sauerbraten units: 2.0f for SG/CG/Pistol, 3.0f for Rifle).</param>
    public void AddBulletHole(Vector3 pos, Vector3 dir, float radius)
    {
        if (dir.IsZeroApprox()) return;

        float now = (float)Time.GetTicksMsec() / 1000f;
        var entry = _decalPool[_decalIndex];
        _decalIndex = (_decalIndex + 1) % MaxDecals;

        var meshInst = entry.MeshInstance;
        Vector3 normal = dir.Normalized();
        Vector3 tangent;
        if (MathF.Abs(normal.Y) > 0.99f)
        {
            tangent = normal.Cross(Vector3.Right).Normalized();
        }
        else
        {
            tangent = normal.Cross(Vector3.Up).Normalized();
        }
        Vector3 bitangent = normal.Cross(tangent).Normalized();

        // Random rotation around normal axis (matches original DF_ROTATE)
        float angle = (float)(Random.Shared.NextDouble() * Math.PI * 2.0);
        tangent = tangent.Rotated(normal, angle);
        bitangent = bitangent.Rotated(normal, angle);

        float diameter = radius * 2.0f;
        Basis basis = new Basis(tangent * diameter, bitangent * diameter, normal);

        // Slightly offset quad outwards along normal to avoid Z-fighting with map geometry
        Vector3 offsetPos = pos + normal * 0.05f;

        meshInst.Basis = basis;
        meshInst.Position = offsetPos;
        entry.Material.AlbedoTexture = _bulletTexture;
        entry.Material.AlbedoColor = new Color(1f, 1f, 1f, 1f);
        meshInst.Visible = true;

        entry.StartTime = now;
        entry.FadeStartTime = now + BulletDecalFadeTime;
        entry.TotalLifetime = BulletDecalFadeTime + BulletDecalFadeOut;
    }

    /// <summary>
    /// Spawns a scorch mark decal on a surface from an explosion impact.
    /// Matches <c>adddecal(DECAL_SCORCH, pos, dir, exprad/2)</c> from <c>src/fpsgame/weapon.cpp</c>.
    /// </summary>
    public void AddScorchMark(Vector3 pos, Vector3 dir, float radius)
    {
        if (dir.IsZeroApprox()) dir = new Vector3(0f, 0f, 1f);

        float now = (float)Time.GetTicksMsec() / 1000f;
        var entry = _decalPool[_decalIndex];
        _decalIndex = (_decalIndex + 1) % MaxDecals;

        var meshInst = entry.MeshInstance;
        Vector3 normal = dir.Normalized();
        Vector3 tangent;
        if (MathF.Abs(normal.Y) > 0.99f)
        {
            tangent = normal.Cross(Vector3.Right).Normalized();
        }
        else
        {
            tangent = normal.Cross(Vector3.Up).Normalized();
        }
        Vector3 bitangent = normal.Cross(tangent).Normalized();

        float angle = (float)(Random.Shared.NextDouble() * Math.PI * 2.0);
        tangent = tangent.Rotated(normal, angle);
        bitangent = bitangent.Rotated(normal, angle);

        float diameter = radius * 2.0f;
        Basis basis = new Basis(tangent * diameter, bitangent * diameter, normal);

        Vector3 offsetPos = pos + normal * 0.05f;

        meshInst.Basis = basis;
        meshInst.Position = offsetPos;

        entry.Material.AlbedoTexture = _scorchTexture ?? _bulletTexture;
        entry.Material.AlbedoColor = new Color(1f, 1f, 1f, 1f);
        meshInst.Visible = true;

        entry.StartTime = now;
        entry.FadeStartTime = now + BulletDecalFadeTime;
        entry.TotalLifetime = BulletDecalFadeTime + BulletDecalFadeOut;
    }

    /// <summary>
    /// Spawns explosive fireball flash and sparks.
    /// Matches <c>explode</c> visual effects in <c>src/fpsgame/weapon.cpp:428</c>.
    /// </summary>
    public void AddExplosionEffect(Gun gun, Vector3 pos, float radius)
    {
        float now = (float)Time.GetTicksMsec() / 1000f;
        Color color = gun == Gun.GrenadeLauncher
            ? new Color(0.5f, 1f, 1f, 0.9f) // Blue fireball (PART_EXPLOSION_BLUE)
            : new Color(1f, 0.5f, 0.2f, 0.9f); // Orange/red fireball (PART_EXPLOSION)

        _explosions.Add(new ActiveExplosion
        {
            Origin = pos,
            Radius = Math.Max(4f, radius),
            Color = color,
            StartTime = now,
            Lifetime = 0.35f
        });
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

        // Render flying rockets as their projectile model (weapon.cpp:renderprojectiles).
        UpdateRocketVisuals();

        // Render explosion fireballs
        for (int i = _explosions.Count - 1; i >= 0; i--)
        {
            var exp = _explosions[i];
            float elapsed = now - exp.StartTime;
            if (elapsed >= exp.Lifetime)
            {
                _explosions.RemoveAt(i);
                continue;
            }

            float progress = elapsed / exp.Lifetime;
            float currentRadius = exp.Radius * MathF.Sin(progress * MathF.PI * 0.5f);
            float fade = 1f - progress;
            Color c = exp.Color with { A = exp.Color.A * fade };

            // Draw expanding 3-axis star/burst lines
            _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
            _mesh.SurfaceSetColor(c);

            _mesh.SurfaceAddVertex(exp.Origin - new Vector3(currentRadius, 0, 0));
            _mesh.SurfaceAddVertex(exp.Origin + new Vector3(currentRadius, 0, 0));

            _mesh.SurfaceAddVertex(exp.Origin - new Vector3(0, currentRadius, 0));
            _mesh.SurfaceAddVertex(exp.Origin + new Vector3(0, currentRadius, 0));

            _mesh.SurfaceAddVertex(exp.Origin - new Vector3(0, 0, currentRadius));
            _mesh.SurfaceAddVertex(exp.Origin + new Vector3(0, 0, currentRadius));

            // Diagonal bursts
            float diag = currentRadius * 0.707f;
            _mesh.SurfaceAddVertex(exp.Origin - new Vector3(diag, diag, 0));
            _mesh.SurfaceAddVertex(exp.Origin + new Vector3(diag, diag, 0));

            _mesh.SurfaceAddVertex(exp.Origin - new Vector3(-diag, diag, 0));
            _mesh.SurfaceAddVertex(exp.Origin + new Vector3(-diag, diag, 0));

            _mesh.SurfaceAddVertex(exp.Origin - new Vector3(0, diag, diag));
            _mesh.SurfaceAddVertex(exp.Origin + new Vector3(0, diag, diag));

            _mesh.SurfaceEnd();
        }

        // Process decal fading
        for (int i = 0; i < MaxDecals; i++)
        {
            var entry = _decalPool[i];
            if (!entry.MeshInstance.Visible) continue;

            float elapsed = now - entry.StartTime;
            if (elapsed >= entry.TotalLifetime)
            {
                entry.MeshInstance.Visible = false;
                continue;
            }

            if (now > entry.FadeStartTime)
            {
                float fadeElapsed = now - entry.FadeStartTime;
                float alpha = Math.Clamp(1f - (fadeElapsed / BulletDecalFadeOut), 0f, 1f);
                entry.Material.AlbedoColor = new Color(1f, 1f, 1f, alpha);
            }
        }
    }

    /// <summary>
    /// Shows/positions a pooled <c>projectiles/rocket</c> model for every live rocket,
    /// matching <c>weapon.cpp:renderprojectiles</c>: the model sits <see cref="RocketVisualLead"/>
    /// units in front of the interpolated position and points along the flight direction.
    /// </summary>
    private void UpdateRocketVisuals()
    {
        if (_rocketScene == null)
        {
            HideAllRocketVisuals();
            return;
        }

        _activeRocketsScratch.Clear();
        if (ActiveRocketsProvider?.Invoke() is { } rockets)
        {
            foreach (var rocket in rockets)
            {
                if (rocket.Exploded) continue;
                _activeRocketsScratch.Add(rocket);

                if (!_rocketVisuals.TryGetValue(rocket, out Node3D? visual))
                {
                    visual = TakeRocketVisual();
                    if (visual == null) continue;
                    _rocketVisuals[rocket] = visual;
                }

                Vector3 dir = rocket.Dir;
                if (dir.LengthSquared() < 1e-6f) dir = new Vector3(0f, 1f, 0f);
                visual.Position = rocket.GetVisualPosition() + dir * RocketVisualLead;
                // Bake the model scale into the basis: assigning Basis would otherwise
                // reset the node's Scale to 1 every frame.
                visual.Basis = RocketBasis(dir).Scaled(Vector3.One * RocketVisualScale);
                visual.Visible = true;
            }
        }

        _expiredRocketsScratch.Clear();
        foreach (var pair in _rocketVisuals)
            if (!_activeRocketsScratch.Contains(pair.Key))
                _expiredRocketsScratch.Add(pair.Key);
        for (int i = 0; i < _expiredRocketsScratch.Count; i++)
        {
            Node3D visual = _rocketVisuals[_expiredRocketsScratch[i]];
            visual.Visible = false;
            _rocketVisualPool.Add(visual);
            _rocketVisuals.Remove(_expiredRocketsScratch[i]);
        }
    }

    private void HideAllRocketVisuals()
    {
        foreach (var visual in _rocketVisuals.Values)
        {
            visual.Visible = false;
            _rocketVisualPool.Add(visual);
        }
        _rocketVisuals.Clear();
    }

    private Node3D? TakeRocketVisual()
    {
        if (_rocketVisualPool.Count > 0)
        {
            Node3D pooled = _rocketVisualPool[^1];
            _rocketVisualPool.RemoveAt(_rocketVisualPool.Count - 1);
            return pooled;
        }

        if (_rocketScene?.Instantiate() is not Node3D scene) return null;
        ApplyMaterial(scene, _rocketMaterial!);
        scene.Name = "RocketVisual";
        AddChild(scene);
        return scene;
    }

    /// <summary>
    /// Orients the rocket so its authored <b>+X</b> axis points along <paramref name="dir"/>.
    /// <see cref="Basis.LookingAt"/> aims local -Z at the target, so a +90 deg rotation
    /// about local Y maps +X onto the flight direction (the engine's <c>yaw += 90</c>).
    /// The world is Z-up, so the reference up is the local +Z axis.
    /// </summary>
    private static Basis RocketBasis(Vector3 dir)
    {
        Vector3 up = new(0f, 0f, 1f);
        if (Mathf.Abs(dir.Dot(up)) > 0.999f) up = new Vector3(0f, 1f, 0f);
        return Basis.LookingAt(dir, up) * new Basis(Vector3.Up, Mathf.Pi / 2f);
    }

    private static void ApplyMaterial(Node node, Material material)
    {
        if (node is MeshInstance3D mesh) mesh.MaterialOverride = material;
        foreach (var child in node.GetChildren()) ApplyMaterial(child, material);
    }
}
