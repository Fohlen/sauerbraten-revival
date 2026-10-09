using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Materials;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// The local player's visible body model — one of the five skeletal MD5 models in
/// <see cref="Playermodels"/> (render.cpp <c>playermodels</c>). It is the third-person
/// counterpart to the first-person camera: in first person <see cref="MapPlayer"/>
/// hides it, in third person the detached camera looks at it.
///
/// The bind-pose geometry is rendered as static meshes (see
/// <see cref="ModelInstance"/>); the skeleton drives it once animations are attached.
/// </summary>
[GlobalClass]
public partial class PlayerModel : ModelInstance
{
    /// <summary>Engine model name actually loaded (e.g. "mrfixit/blue"). Diagnostics only.</summary>
    [Export] public string? ModelPath { get; set; }

    /// <summary>Selected table index (engine <c>playermodel</c>, 0..4).</summary>
    [Export] public int ModelIndex { get; set; }

    /// <summary>Team skin (0 = FFA, 1 = blue, 2 = red). Diagnostics only.</summary>
    [Export] public int Team { get; set; }

    /// <summary>
    /// Point this node at a player model's variant for <paramref name="team"/> and load
    /// it, using the world's converted-model catalogue and texture source. Safe to call
    /// before or after the node enters the tree. <paramref name="rotationDegrees"/> and
    /// <paramref name="mirror"/> correct the imported model's frame.
    /// </summary>
    public void Configure(
        ModelCatalog? models,
        ITextureSource? textures,
        int modelIndex,
        int team,
        Dictionary<string, Material>? skinCache = null)
    {
        ModelIndex = modelIndex;
        Team = team;
        var info = models?.Get(Playermodels.Get(modelIndex).Body(team));

        ModelPath = Playermodels.Get(modelIndex).Body(team);
        GlbPath = info?.GlbPath;
        GlbOsPath = info?.GlbOsPath;
        ModelScale = info?.Scale ?? 1.0f;
        // The catalog's mdltrans, plus a fixed nudge that plants the feet on the floor.
        // Assimp's MD5 export puts the model origin at mid-height, so the body needs to
        // be lifted by half its height: +10 in the model's local frame (before the -90
        // about X below). The engine has no such offset — it relies on the MD5 loader's
        // bind-pose handling, which assimp does not reproduce.
        ModelTranslation = (info?.Translation ?? Vector3.Zero) + new Vector3(0f, 0f, 10f);
        // Assimp exports the MD5 body Y-up; -90 about X stands it in the Z-up octree
        // world. Facing is the player yaw (engine renders the body at yaw+90).
        ModelRotationDegrees = new Vector3(-90f, 0f, 0f);
        SkinMaterial = ModelSkin.Build(info, textures, skinCache ?? new Dictionary<string, Material>());

        if (IsInsideTree()) Reload();
    }
}
