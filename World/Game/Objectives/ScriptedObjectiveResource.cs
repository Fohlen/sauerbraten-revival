using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Hud;
using SauerbratenRevival.World.Runtime;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game.Objectives;

[Tool]
[GlobalClass]
public partial class ScriptedObjectiveResource : ModeObjectiveResource
{
    /// <summary>GDScript or C# Script implementing objective lifecycle methods.</summary>
    [Export] public Script? ObjectiveScript { get; set; }

    public ScriptedObjectiveResource()
    {
        Id = "custom";
        Name = "custom";
        DefaultTeamPlay = false;
        DefaultMapPool = MapListId.Dm;
        DefaultSpawnArmour = 25;
    }

    public override IModeObjective CreateInstance(GameModeInfo mode) => new ScriptedObjective(this, mode);
}

public class ScriptedObjective : IModeObjective
{
    public ModeObjectiveResource Definition => Resource;
    public ScriptedObjectiveResource Resource { get; }
    public GameModeInfo Mode { get; }

    private readonly GodotObject? _scriptInstance;

    public ScriptedObjective(ScriptedObjectiveResource resource, GameModeInfo mode)
    {
        Resource = resource;
        Mode = mode;
        if (resource.ObjectiveScript != null)
        {
            _scriptInstance = resource.ObjectiveScript.Call("new").AsGodotObject();
        }
    }

    public bool ClaimsEntity(MapEntity e)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("claims_entity"))
        {
            var res = _scriptInstance.Call("claims_entity", (int)e.Type);
            return res.AsBool();
        }
        return false;
    }

    public void SetupWorld(IGameWorld world, IReadOnlyList<MapEntity> entities, Node3D objectiveRoot)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("setup_world"))
        {
            _scriptInstance.Call("setup_world", world as GodotObject, objectiveRoot);
        }
    }

    public void Step(IGameWorld world, int stepMillis, int nowMillis)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("step"))
        {
            _scriptInstance.Call("step", world as GodotObject, stepMillis, nowMillis);
        }
    }

    public void StepPlayer(MapPlayer player, int nowMillis)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("step_player"))
        {
            _scriptInstance.Call("step_player", player, nowMillis);
        }
    }

    public void UpdateVisual(IGameWorld world, int lastMillis)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("update_visual"))
        {
            _scriptInstance.Call("update_visual", world as GodotObject, lastMillis);
        }
    }

    public (Vector3 FeetPos, float Yaw)? ResolveSpawn(MapPlayer player)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("resolve_spawn"))
        {
            var res = _scriptInstance.Call("resolve_spawn", player);
            if (res.VariantType == Variant.Type.Dictionary)
            {
                var dict = res.AsGodotDictionary();
                if (dict.TryGetValue("pos", out var p) && dict.TryGetValue("yaw", out var y))
                    return (p.AsVector3(), y.AsSingle());
            }
        }
        return null;
    }

    public bool OnBeforeDamage(
        MapPlayer? attacker,
        MapPlayer victim,
        Gun gun,
        ref int damage,
        ref Vector3 pushVel,
        int nowMillis)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("on_before_damage"))
        {
            var res = _scriptInstance.Call("on_before_damage", attacker, victim, (int)gun, damage);
            return res.AsBool();
        }
        return true;
    }

    public void OnPlayerDied(MapPlayer victim, MapPlayer? killer, Gun gun, int nowMillis)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("on_player_died"))
        {
            _scriptInstance.Call("on_player_died", victim, killer, (int)gun, nowMillis);
        }
    }

    public ObjectiveHudState GetHudState(MapPlayer localPlayer, int nowMillis)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("get_hud_state"))
        {
            var res = _scriptInstance.Call("get_hud_state", localPlayer, nowMillis);
            if (res.VariantType == Variant.Type.Dictionary)
            {
                var dict = res.AsGodotDictionary();
                int pScore = dict.TryGetValue("primary_score", out var ps) ? ps.AsInt32() : int.MinValue;
                int sScore = dict.TryGetValue("secondary_score", out var ss) ? ss.AsInt32() : int.MinValue;
                bool friendly = !dict.TryGetValue("primary_is_friendly", out var pf) || pf.AsBool();
                HudIcon? icon = dict.TryGetValue("status_icon", out var si) ? (HudIcon)si.AsInt32() : null;
                int counter = dict.TryGetValue("status_counter", out var sc) ? sc.AsInt32() : -1;
                return new ObjectiveHudState(pScore, sScore, friendly, icon, counter);
            }
        }
        return default;
    }
}
