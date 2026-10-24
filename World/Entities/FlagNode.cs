using System;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Materials;
using SauerbratenRevival.World.Models;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Visual 3D representation of a game flag (<c>ET_FLAG</c> / hold base).
/// Handles model loading (flags/blue, flags/red, flags/neutral), positioning,
/// rotation, and carrier attachment matching <c>fpsgame/ctf.h:637 interpflagpos</c>
/// and <c>rendergame()</c>.
/// </summary>
[Tool]
[GlobalClass]
public partial class FlagNode : Models.ModelInstance
{
    private string _currentModelName = "";

    /// <summary>Flag ID in the FlagManager.</summary>
    [Export] public int FlagId { get; set; }

    /// <summary>Owning team ID (1 = blue, 2 = red, 0 = neutral).</summary>
    [Export] public int Team { get; set; }

    /// <summary>Model catalog for loading glTF and materials.</summary>
    public IModelCatalog? Models { get; set; }

    /// <summary>Texture source for skins.</summary>
    public ITextureSource? Textures { get; set; }

    private readonly System.Collections.Generic.Dictionary<string, Material> _skinCache = new();

    /// <summary>
    /// Configures the flag model and skin.
    /// </summary>
    public void SetModel(string modelName)
    {
        if (_currentModelName == modelName && ModelLoaded) return;

        _currentModelName = modelName;
        var info = Models?.Get(modelName);
        if (info == null) return;

        GlbPath = info.GlbPath;
        GlbOsPath = info.GlbOsPath;
        ModelScale = info.Scale;
        ModelTranslation = info.Translation;
        SkinMaterial = ModelSkin.Build(info, Textures, _skinCache);
        Reload();
    }

    public Game.GameModeInfo Mode { get; set; } = Game.GameModeCatalog.Get("ctf");

    /// <summary>
    /// Updates visual position using the node's configured <see cref="Mode"/>.
    /// </summary>
    public void UpdateVisual(FlagState flag, int lastMillis, int localPlayerTeam, Vector3? cameraPos = null, Func<int, (Vector3 pos, float yaw, float aboveEye)>? getOwnerInfo = null)
        => UpdateVisual(flag, Mode, lastMillis, localPlayerTeam, cameraPos, getOwnerInfo);

    /// <summary>
    /// Updates the visual position, facing yaw, and model variant according to flag state.
    /// </summary>
    public void UpdateVisual(FlagState flag, GameModeInfo mode, int lastMillis, int localPlayerTeam, Vector3? cameraPos = null, Func<int, (Vector3 pos, float yaw, float aboveEye)>? getOwnerInfo = null)
    {
        bool isHold = mode.Has(ModeFlags.Hold);

        // Invisible / unrendered check matching ctf.h:665
        if (!flag.HasOwner && flag.DropTime > 0 && flag.DropLoc.X < 0)
        {
            Visible = false;
            return;
        }
        if (isHold && flag.SpawnIndex < 0)
        {
            Visible = false;
            return;
        }

        Visible = true;

        // Model selection matching ctf.h:667:
        // const char *flagname = m_hold && (!f.owner || lastmillis%1000 < 500) ? "flags/neutral" : (m_hold ? ctfteamflag(f.owner->team) : f.team)==ctfteamflag(player1->team) ? "flags/blue" : "flags/red";
        string modelName;
        if (isHold)
        {
            if (!flag.HasOwner || (lastMillis % 1000 < 500))
            {
                modelName = "flags/neutral";
            }
            else
            {
                modelName = flag.Team == localPlayerTeam ? "flags/blue" : "flags/red";
            }
        }
        else
        {
            modelName = flag.Team == localPlayerTeam ? "flags/blue" : "flags/red";
        }

        SetModel(modelName);

        Vector3 pos = flag.InterpFlagPos(out float angle, isHold, cameraPos, getOwnerInfo);
        Position = pos;
        EntityFacing.ApplyYaw(this, angle);
    }
}
