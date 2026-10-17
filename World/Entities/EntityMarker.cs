using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Cube2's edit-mode entity marker: a blue billboard quad. The original spawns a
/// <c>PART_EDIT</c> particle splash (2 particles, colour <c>0x3232FF</c>, texture
/// <c>packages/particles/base.png</c>); we use a static billboard instead, which is
/// cheap and reads the same. The whole layer is in the <see cref="Group"/> so a
/// future game/edit-mode split can hide it with a single call.
///
/// This is deliberately separate from the real particle system
/// (<c>ET_PARTICLES</c> — see docs/adr/0006-ogz-import.md); these are gizmos, not effects.
/// </summary>
public static class EntityMarker
{
    public const string Group = "entity_markers";

    /// <summary>Cube2 <c>0x3232FF</c>.</summary>
    public static readonly Color MarkerColor = new(0x32 / 255.0f, 0x32 / 255.0f, 1.0f);

    private static Texture2D? _texture;
    private static bool _textureTried;

    /// <summary>Attach a marker (and optionally a facing arrow) to an entity node.</summary>
    public static void Add(Node3D parent, ITextureSource? textures, float size = 8.0f, bool arrow = false)
    {
        var marker = CreateMarker(textures, size);
        parent.AddChild(marker);
        if (arrow) parent.AddChild(CreateArrow(size));
    }

    /// <summary>Show/hide all markers (e.g. hide in game mode later).</summary>
    public static void SetVisible(SceneTree tree, bool visible)
    {
        foreach (var node in tree.GetNodesInGroup(Group))
            if (node is Node3D n3) n3.Visible = visible;
    }

    /// <summary>Show/hide all markers under a root node hierarchy.</summary>
    public static void SetVisible(Node root, bool visible)
    {
        if (root.IsInsideTree())
        {
            foreach (var node in root.GetTree().GetNodesInGroup(Group))
                if (node is Node3D n3 && (root == n3 || root.IsAncestorOf(n3)))
                    n3.Visible = visible;
        }
        else
        {
            SetVisibleRecursive(root, visible);
        }
    }

    private static void SetVisibleRecursive(Node node, bool visible)
    {
        if (node.IsInGroup(Group) && node is Node3D n3)
            n3.Visible = visible;
        foreach (var child in node.GetChildren())
            SetVisibleRecursive(child, visible);
    }

    private static Sprite3D CreateMarker(ITextureSource? textures, float size)
    {
        if (!_textureTried)
        {
            _texture = textures?.Get("particles/base.png");
            _textureTried = true;
        }

        var sprite = new Sprite3D
        {
            Name = "EntityMarker",
            Texture = _texture,
            Modulate = MarkerColor,
            PixelSize = size / Mathf.Max(_texture?.GetWidth() ?? 64, 1),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Shaded = false,
            Transparent = true,
            AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
        };
        sprite.AddToGroup(Group);
        return sprite;
    }

    // A cone pointing along the parent's local -Z (the node's facing), so the
    // caller just needs the entity node's basis to encode its yaw.
    private static MeshInstance3D CreateArrow(float size)
    {
        var arrow = new MeshInstance3D
        {
            Name = "EntityArrow",
            Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = size * 0.15f, Height = size, RadialSegments = 6 },
            RotationDegrees = new Vector3(-90, 0, 0),
            Position = new Vector3(0, 0, -size * 0.5f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = MarkerColor,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        arrow.AddToGroup(Group);
        return arrow;
    }
}
