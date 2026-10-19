using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Ogz;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Builds <see cref="ModeEntity"/> nodes from the game-mode entity types:
/// <c>FLAG</c>, <c>BASE</c>, <c>BOX</c>, <c>BARREL</c>, <c>PLATFORM</c>,
/// <c>ELEVATOR</c>, <c>RESPAWNPOINT</c> and <c>CARROT</c>.
///
/// Representation only. The fixed models (flag colours, base colours, carrot,
/// checkpoint) come from the mode code in <c>ctf.h</c>/<c>capture.h</c> and
/// <c>entities::entmdlname</c>; the movables (box/barrel/platform/elevator) use the
/// mapmodel named by <c>attr2</c>, exactly as <c>movable.cpp rendermovables</c>.
///
/// Attribute handling mirrors <c>entities::fixentity</c>, which — for
/// <c>FLAG/BOX/BARREL/PLATFORM/ELEVATOR</c> — shifts <c>attr5 = attr4</c>,
/// <c>attr4 = attr3</c>. So on disk <c>attr3</c> is the weight/tag and <c>attr4</c>
/// the health/speed, which is what the nodes expose.
/// </summary>
public static class ModeEntityBuilder
{
    /// <summary>movable.cpp <c>BOXWEIGHT</c>.</summary>
    public const int BoxWeight = 25;

    /// <summary>movable.cpp <c>BARRELHEALTH</c>.</summary>
    public const int BarrelHealth = 50;

    /// <summary>movable.cpp <c>BARRELWEIGHT</c>.</summary>
    public const int BarrelWeight = 25;

    /// <summary>movable.cpp <c>PLATFORMSPEED</c>.</summary>
    public const float PlatformSpeed = 8.0f;

    public static Node3D Build(OgzMap map, MetadataCatalogue? catalogue = null,
                               ModelCatalog? models = null, ITextureSource? textures = null,
                               GameModeInfo? mode = null)
    {
        var root = new Node3D { Name = "ModeEntities" };
        var skinCache = new Dictionary<string, Material>();

        foreach (var (i, e) in map.Where(e => IsModeEntity(e) && AppliesToMode(e, mode)))
        {
            string? modelName = ModelName(e, catalogue);
            var info = modelName != null ? models?.Get(modelName) : null;

            var node = new ModeEntity
            {
                Name = $"{EntityType.Name(e.Type)}_{i}",
                Position = e.Position,
                ModeType = e.Type,
                ModeName = EntityType.Name(e.Type),
                Attr1 = e.Attr1,
                Attr2 = e.Attr2,
                Attr3 = e.Attr3,
                Attr4 = e.Attr4,
                Attr5 = e.Attr5,
                Yaw = e.Attr1,
                ModelPath = modelName,
                GlbPath = info?.GlbPath,
                GlbOsPath = info?.GlbOsPath,
                ModelScale = info?.Scale ?? 1.0f,
                ModelTranslation = info?.Translation ?? Vector3.Zero,
                SkinMaterial = ModelSkin.Build(info, textures, skinCache),
            };

            PopulateSemantics(node, e);

            // Orient the entity model in the Z-up world.
            EntityFacing.ApplyYaw(node, e.Attr1);

            // entradius draws a facing arrow for FLAG/MONSTER/TELEDEST/
            // RESPAWNPOINT/BOX/BARREL/PLATFORM/ELEVATOR (size 4).
            bool arrow = DrawsFacingArrow(e.Type);
            EntityMarker.Add(node, textures, arrow: arrow);

            root.AddChild(node);
        }

        return root;
    }

    /// <summary>The mode entity types handled here.</summary>
    public static bool IsModeEntity(OgzEntity e) => e.Type switch
    {
        EntityType.Carrot or EntityType.Base or EntityType.RespawnPoint or
        EntityType.Box or EntityType.Barrel or EntityType.Platform or
        EntityType.Elevator or EntityType.Flag => true,
        _ => false,
    };

    /// <summary>
    /// Whether an entity is drawn for the active <paramref name="mode"/>. FLAG and
    /// BASE are not generic entities — <c>entities.cpp entmdlname</c> returns NULL
    /// for both and they are drawn by the mode code instead. The engine picks that
    /// code in <c>client.cpp setclientmode</c>: <c>m_capture</c> owns BASE,
    /// <c>m_ctf</c> owns FLAG (Hold uses BASE as (invisible) hold spawns and creates
    /// its flag dynamically, so neither map entity is drawn). Any other mode draws
    /// neither. A null <paramref name="mode"/> is the edit/inspection view and keeps
    /// the raw representation of every type.
    /// </summary>
    public static bool AppliesToMode(OgzEntity e, GameModeInfo? mode)
    {
        if (mode == null) return true;
        return e.Type switch
        {
            // FlagNode renders flags for live ctf state; never duplicate them here.
            EntityType.Flag => false,
            EntityType.Base => mode.Has(ModeFlags.Capture),
            _ => true,
        };
    }

    /// <summary>entities.cpp <c>entradius</c>: types that get a yaw arrow.</summary>
    public static bool DrawsFacingArrow(byte type) => type switch
    {
        EntityType.Flag or EntityType.RespawnPoint or EntityType.Box or
        EntityType.Barrel or EntityType.Platform or EntityType.Elevator => true,
        _ => false,
    };

    /// <summary>
    /// The engine model name. Flag and base colours are hard-coded in the mode code
    /// and default to the neutral (unowned) variant here; movables resolve
    /// <c>attr2</c> through the catalogue (<c>movable.cpp</c> uses
    /// <c>mapmodelname(attr2)</c>); carrot/checkpoint come from <c>entmdlname</c>.
    /// </summary>
    public static string? ModelName(OgzEntity e, MetadataCatalogue? catalogue) => e.Type switch
    {
        EntityType.Carrot => "carrot",
        EntityType.RespawnPoint => "checkpoint",
        EntityType.Flag => FlagModel(e.Attr2),
        EntityType.Base => "base/neutral",
        EntityType.Box or EntityType.Barrel or EntityType.Platform or EntityType.Elevator =>
            e.Attr2 > 0 ? catalogue?.MapModelPath(e.Attr2) : null,
        _ => null,
    };

    /// <summary>ctf.h: team 1 (good) → blue, 2 (evil) → red, otherwise neutral.</summary>
    public static string FlagModel(int team) => team switch
    {
        1 => "flags/blue",
        2 => "flags/red",
        _ => "flags/neutral",
    };

    /// <summary>capture.h <c>basemodels</c>: neutral until a team owns it (game state).</summary>
    public static string BaseModel() => "base/neutral";

    /// <summary>
    /// Resolve the shifted semantics of one mode entity (mirrors the relevant
    /// constructors: <c>movable</c>, <c>ctf.h</c>, <c>capture.h</c>).
    /// </summary>
    private static void PopulateSemantics(ModeEntity node, OgzEntity e)
    {
        switch (e.Type)
        {
            case EntityType.Flag:
                // attr1 = spawn yaw, attr2 = team.
                node.Team = e.Attr2;
                break;

            case EntityType.Base:
                // attr1 = ammo type, attr2 = tag (0 → index, assigned by the mode).
                node.Tag = e.Attr1;
                node.BaseIndex = e.Attr2;
                break;

            case EntityType.Box:
                // attr3 = weight (default BOXWEIGHT).
                node.Weight = e.Attr3 != 0 ? e.Attr3 : BoxWeight;
                break;

            case EntityType.Barrel:
                // attr3 = weight (default BARRELWEIGHT), attr4 = health (default 50).
                node.Weight = e.Attr3 != 0 ? e.Attr3 : BarrelWeight;
                node.Health = e.Attr4 != 0 ? e.Attr4 : BarrelHealth;
                break;

            case EntityType.Platform:
            case EntityType.Elevator:
                // attr3 = tag, attr4 = speed (0 → PLATFORMSPEED); sign of attr4 = dir.
                node.Tag = e.Attr3;
                node.Speed = e.Attr4 != 0 ? Mathf.Abs(e.Attr4) : PlatformSpeed;
                node.Direction = e.Attr4 < 0 ? -1 : 1;
                node.Weight = 1000; // movable.cpp PLATFORMWEIGHT
                break;
        }
    }
}
