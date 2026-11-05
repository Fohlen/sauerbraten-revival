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

    private readonly Variant _scriptInstanceHolder;
    private readonly GodotObject? _scriptInstance;

    public ScriptedObjective(ScriptedObjectiveResource resource, GameModeInfo mode)
    {
        Resource = resource;
        Mode = mode;
        if (resource.ObjectiveScript != null)
        {
            _scriptInstanceHolder = resource.ObjectiveScript.Call("new");
            _scriptInstance = _scriptInstanceHolder.AsGodotObject();
        }
    }

    private int GetMethodArgCount(string methodName)
    {
        if (_scriptInstance == null) return -1;
        var methodList = _scriptInstance.GetMethodList();
        foreach (var method in methodList)
        {
            if (method.TryGetValue("name", out var name) && name.AsString() == methodName)
            {
                if (method.TryGetValue("args", out var args) && args.VariantType == Variant.Type.Array)
                {
                    return args.AsGodotArray().Count;
                }
            }
        }
        return -1;
    }

    public bool ClaimsEntity(MapEntity e)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("claims_entity"))
        {
            var res = _scriptInstance.Call("claims_entity", e);
            return res.AsBool();
        }
        return false;
    }

    public void SetupWorld(IGameWorld world, IReadOnlyList<MapEntity> entities, Node3D objectiveRoot)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("setup_world"))
        {
            var entityArray = new Godot.Collections.Array();
            for (int i = 0; i < entities.Count; i++)
            {
                entityArray.Add(entities[i]);
            }

            var worldVar = world as GodotObject != null ? (Variant)(GodotObject)world : new Variant();
            int argCount = GetMethodArgCount("setup_world");
            if (argCount == 2)
            {
                _scriptInstance.Call("setup_world", worldVar, objectiveRoot);
            }
            else
            {
                _scriptInstance.Call("setup_world", worldVar, entityArray, objectiveRoot);
            }
        }
    }

    public void Step(IGameWorld world, int stepMillis, int nowMillis)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("step"))
        {
            var worldVar = world as GodotObject != null ? (Variant)(GodotObject)world : new Variant();
            _scriptInstance.Call("step", worldVar, stepMillis, nowMillis);
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
            var worldVar = world as GodotObject != null ? (Variant)(GodotObject)world : new Variant();
            _scriptInstance.Call("update_visual", worldVar, lastMillis);
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

    public void OnPlayerSpawn(MapPlayer player)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("on_player_spawn"))
        {
            _scriptInstance.Call("on_player_spawn", player);
        }
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
            var result = new Godot.Collections.Dictionary
            {
                { "damage", damage },
                { "push", pushVel },
                { "push_vel", pushVel }
            };

            int argCount = GetMethodArgCount("on_before_damage");
            Variant res;
            var attackerVar = attacker != null ? (Variant)attacker : new Variant();
            if (argCount == 4)
            {
                res = _scriptInstance.Call("on_before_damage", attackerVar, victim, (int)gun, result);
            }
            else
            {
                res = _scriptInstance.Call("on_before_damage", attackerVar, victim, (int)gun, result, nowMillis);
            }

            if (result.TryGetValue("damage", out var d)) damage = d.AsInt32();
            if (result.TryGetValue("push", out var p)) pushVel = p.AsVector3();
            else if (result.TryGetValue("push_vel", out var pv)) pushVel = pv.AsVector3();

            if (res.VariantType == Variant.Type.Dictionary)
            {
                var retDict = res.AsGodotDictionary();
                if (retDict.TryGetValue("damage", out var rd)) damage = rd.AsInt32();
                if (retDict.TryGetValue("push", out var rp)) pushVel = rp.AsVector3();
                else if (retDict.TryGetValue("push_vel", out var rpv)) pushVel = rpv.AsVector3();
                if (retDict.TryGetValue("cancel", out var cancel) && cancel.AsBool()) return false;
                if (retDict.TryGetValue("allow", out var allow)) return allow.AsBool();
                return true;
            }
            else if (res.VariantType == Variant.Type.Bool)
            {
                return res.AsBool();
            }

            return true;
        }
        return true;
    }

    public void OnPlayerDied(MapPlayer victim, MapPlayer? killer, Gun gun, int nowMillis)
    {
        if (_scriptInstance != null && _scriptInstance.HasMethod("on_player_died"))
        {
            var killerVar = killer != null ? (Variant)killer : new Variant();
            _scriptInstance.Call("on_player_died", victim, killerVar, (int)gun, nowMillis);
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
