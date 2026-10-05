using Godot;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// A player spawn point (<c>ET_PLAYERSTART</c>): team and facing come straight
/// from the map entity. Positioned in Sauerbraten coordinates; meant to live
/// under the Z-up → Y-up world node.
///
/// A small arrow mesh is attached so the spawn is visible in the editor viewport
/// and in the running preview (a bare <see cref="Marker3D"/> only shows a gizmo
/// when the editor owns the node).
/// </summary>
[Tool]
[GlobalClass]
public partial class SpawnPoint : Marker3D
{
    /// <summary>attr2: 0 for non-team modes, 1 or 2 for CTF teams.</summary>
    [Export] public int Team { get; set; }

    /// <summary>attr1: facing yaw in degrees (Sauerbraten convention).</summary>
    [Export] public float Yaw { get; set; }

    [Export] public bool ShowGizmo { get; set; } = true;

    /// <summary>Arrow length in world units (a Sauerbraten player is roughly this tall).</summary>
    [Export] public float GizmoHeight { get; set; } = 16.0f;

    private MeshInstance3D? _gizmo;

    public override void _Ready()
    {
        if (!ShowGizmo) return;
        if (_gizmo != null && IsInstanceValid(_gizmo)) return;
        _gizmo = CreateGizmo();
        AddChild(_gizmo);
    }

    private MeshInstance3D CreateGizmo()
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.2f, 1.0f, 1.0f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        float height = Mathf.Max(GizmoHeight, 1.0f);
        return new MeshInstance3D
        {
            Name = "Gizmo",
            Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = height * 0.25f, Height = height, RadialSegments = 8 },
            Position = new Vector3(0, 0, -height * 0.5f), // base at the spawn, tip pointing forward
            RotationDegrees = new Vector3(-90, 0, 0),      // cone apex (+Y) -> forward (-Z)
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }
}
