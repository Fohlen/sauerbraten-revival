using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Builds <see cref="Monster"/> nodes from a map's <c>MONSTER</c> entities.
///
/// Attributes (monster.cpp <c>clearmonsters</c>): <c>attr1</c> = yaw,
/// <c>attr2</c> = monster type (index into <see cref="MonsterTypes.All"/>),
/// <c>attr3</c> = tag. An out-of-range type is clamped to 0, matching the
/// constructor's warning. The nine models are converted by <c>tools/model-import</c>.
///
/// Representation only; the AI/physics is game logic (see docs/ogz-import.md).
/// </summary>
public static class MonsterBuilder
{
    /// <summary>shared/ents.h <c>physent</c> default radius.</summary>
    public const float BaseRadius = 4.1f;

    /// <param name="warn">
    /// Sink for fallback diagnostics. Defaults to <see cref="GD.PushWarning"/>;
    /// tests substitute a collector so an intentionally out-of-range type does
    /// not surface as an unexpected engine warning (with C# backtrace).
    /// </param>
    public static Node3D Build(
        OgzMap map,
        ModelCatalog? models = null,
        ITextureSource? textures = null,
        Action<string>? warn = null)
    {
        warn ??= GD.PushWarning;
        var root = new Node3D { Name = "Monsters" };
        var skinCache = new Dictionary<string, Material>();

        foreach (var (i, e) in map.OfType(EntityType.Monster))
        {
            int type = e.Attr2;
            var info = MonsterTypes.Get(type);
            if (info == null)
            {
                warn($"[monster] unknown monster type {type} at entity {i}; using type 0");
                type = 0;
                info = MonsterTypes.All[0];
            }

            var model = models?.Get(info.ModelName);
            var vwep = info.ViewWeapon != null ? models?.Get(info.ViewWeapon) : null;

            // bscale scales the body box (monster constructor).
            float scale = info.BScale / 10.0f;
            var node = new Monster
            {
                Name = $"Monster_{i}",
                Position = e.Position,
                MonsterType = type,
                Tag = e.Attr3,
                Yaw = e.Attr1,
                ModelPath = info.ModelName,
                ViewWeapon = info.ViewWeapon,
                DisplayName = info.Name,
                Gun = info.Gun,
                Health = info.Health,
                Speed = info.Speed,
                Weight = info.Weight,
                BScale = info.BScale,
                Radius = BaseRadius * scale,
                EyeHeight = 8.0f * scale,
                AboveEye = 7.0f * scale,
                GlbPath = model?.GlbPath,
                GlbOsPath = model?.GlbOsPath,
                ModelScale = model?.Scale ?? 1.0f,
                ModelTranslation = model?.Translation ?? Vector3.Zero,
                SkinMaterial = ModelSkin.Build(model, textures, skinCache),
            };

            EntityFacing.ApplyYaw(node, e.Attr1);
            // entradius draws a facing arrow for MONSTER (size 4), like the mode entities.
            EntityMarker.Add(node, textures, arrow: true);

            // The view-weapon hangs off the body's tag_weapon bone in the engine
            // (monster.cpp rendermonsters). Hanging it under the body node is the
            // import-time representation; the exact bone offset is a render concern.
            if (vwep != null || info.ViewWeapon != null)
            {
                node.AddChild(new MonsterViewWeapon
                {
                    Name = "ViewWeapon",
                    ModelPath = info.ViewWeapon,
                    GlbPath = vwep?.GlbPath,
                    GlbOsPath = vwep?.GlbOsPath,
                    ModelScale = vwep?.Scale ?? 1.0f,
                    ModelTranslation = vwep?.Translation ?? Vector3.Zero,
                    SkinMaterial = ModelSkin.Build(vwep, textures, skinCache),
                });
            }

            root.AddChild(node);
        }

        return root;
    }
}
